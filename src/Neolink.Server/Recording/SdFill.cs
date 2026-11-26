// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Streaming;

namespace Neolink.Recording;

/// <summary>Fetches a battery camera's own recording of each event from its SD card: the
/// camera started at its PIR trigger, so its copy holds the seconds Neolink missed.</summary>
public sealed class SdFill
{
    /// <summary>Camera clips above this are left on the card (a transfer holds the camera awake).</summary>
    public const long MaxBytes = 60L * 1024 * 1024;
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Patience = TimeSpan.FromHours(6); // a backlog of old events must not hold a woken camera

    private sealed class Cam
    {
        public required ICameraControl Control { get; init; }
        public required Func<bool> Battery { get; init; }
        /// <summary>Keeps the camera's session up for a transfer (a parked session is reconnected).</summary>
        public Action<TimeSpan>? Hold { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        /// <summary>The last card check (mounted and recording), good for an hour.</summary>
        public (DateTime At, bool Ready, string Why) Card;
        /// <summary>The last camera-clock reading (server minus camera), good for an hour.</summary>
        public (DateTime At, TimeSpan? Offset) Clock;
        /// <summary>The run of fills holding a battery camera up; see StretchOpen.</summary>
        public DateTime StretchStart, LastActive;
        public bool Exhausted, SeenAsleep;
    }

    private static readonly TimeSpan MaxStretch = TimeSpan.FromMinutes(5);

    /// <summary>A battery camera gives fills 5 min per run; a backlog waits for its next wake
    /// (or 5 idle minutes), so the camera is never held up for a queue.</summary>
    private static bool StretchOpen(Cam cam, string camera)
    {
        if (!cam.Battery()) return true;
        lock (cam)
        {
            var now = DateTime.UtcNow;
            if (cam.SeenAsleep || now - cam.LastActive > MaxStretch)
            {
                cam.SeenAsleep = cam.Exhausted = false;
                cam.StretchStart = now;
            }
            if (!cam.Exhausted && now - cam.StretchStart > MaxStretch)
            {
                cam.Exhausted = true;
                Log.Info($"{camera}: camera copies have held the camera for {MaxStretch.TotalMinutes:0} min; the rest wait for its next wake");
            }
            return !cam.Exhausted;
        }
    }

    /// <summary>Card times are the camera's clock; this is what to add to put them on the server's.</summary>
    private async Task<TimeSpan> ClockShiftAsync(Cam cam, string camera, CancellationToken ct)
    {
        if (DateTime.UtcNow - cam.Clock.At >= CardCheckFor)
        {
            var offset = await cam.Control.GetClockOffsetAsync(ct).ConfigureAwait(false);
            cam.Clock = (DateTime.UtcNow, offset);
            if (offset is { } o && Math.Abs(o.TotalSeconds) >= 2)
                Log.Info($"{camera}: camera clock is {Math.Abs(o.TotalSeconds):0} s {(o > TimeSpan.Zero ? "behind" : "ahead of")} the server; its card times are corrected");
            else if (offset == null)
                Log.Info($"{camera}: camera clock could not be read; its card times are taken as they are");
        }
        return cam.Clock.Offset ?? TimeSpan.Zero;
    }

    private static readonly TimeSpan CardCheckFor = TimeSpan.FromHours(1);

    /// <summary>A camera without a mounted card, or not recording to it, is never searched.</summary>
    private async Task<bool> CardReadyAsync(Cam cam, string camera, CancellationToken ct)
    {
        if (DateTime.UtcNow - cam.Card.At < CardCheckFor) return cam.Card.Ready;
        var state = await cam.Control.GetSdStateAsync(ct).ConfigureAwait(false);
        var why = state switch
        {
            null => "the camera did not report its SD card",
            { Mounted: false } => "no SD card is mounted",
            { Recording: false } => "the camera's SD recording is off",
            _ => "",
        };
        cam.Card = (DateTime.UtcNow, why.Length == 0, why);
        if (why.Length > 0) Log.Info($"{camera}: camera copies of events are not fetched: {why} (checked again in an hour)");
        return cam.Card.Ready;
    }

    private readonly EventStore _store;
    private readonly RecordingSettings _settings;
    private readonly CancellationToken _shutdown;
    private readonly Dictionary<string, Cam> _cams = new(StringComparer.OrdinalIgnoreCase);

    // Per-event outcome of the fetch, for whoever waits on the camera's copy (the event
    // GIF). Created by whichever side gets there first, so hook order does not matter.
    private readonly Dictionary<string, (TaskCompletionSource<string?> Tcs, DateTime DoneUtc)> _outcomes = new();
    private static readonly TimeSpan OutcomeKeep = TimeSpan.FromHours(1);

    /// <summary>How long the camera is held awake per renewal while its copy is pending.</summary>
    private static readonly TimeSpan HoldSpan = TimeSpan.FromMinutes(2);
    /// <summary>How long a camera that was already parked at the event's end gets to come
    /// back for the fetch before the normal wait-for-its-next-wake loop takes over.</summary>
    private static readonly TimeSpan ReconnectFor = TimeSpan.FromSeconds(60);

    public SdFill(EventStore store, RecordingSettings settings, CancellationToken shutdown)
    {
        _store = store;
        _settings = settings;
        _shutdown = shutdown;
    }

    public void Register(string camera, ICameraControl control, Func<bool> battery, Action<TimeSpan>? hold)
    {
        lock (_cams) _cams[camera] = new Cam { Control = control, Battery = battery, Hold = hold };
    }

    /// <summary>The per-camera switch; off until the camera's Recording tab turns it on.</summary>
    public bool Enabled(string camera) => _settings.Get(camera).SdFill ?? false;

    /// <summary>Detached: the event pump never waits for the card. The camera is held awake
    /// from here, so it cannot doze between the event's end and the fetch.</summary>
    public void OnEventClosed(EventRecord rec)
    {
        Cam? cam;
        lock (_cams) _cams.TryGetValue(rec.Camera, out cam);
        if (cam == null) { Complete(rec.Id, "camera copies are not set up for this camera"); return; }
        if (!Enabled(rec.Camera)) { Complete(rec.Id, "camera copies are off for this camera"); return; }
        try { cam.Hold?.Invoke(HoldSpan); } catch { /* best effort */ }
        _ = Task.Run(() => RunAsync(cam, rec));
    }

    /// <summary>Completes when the fetch for this event is over: null when the camera's copy
    /// (camera.mp4) was saved, otherwise why there is none. Never faults.</summary>
    public Task<string?> CopyOutcomeAsync(EventRecord rec)
    {
        lock (_outcomes) return Outcome(rec.Id).Task;
    }

    private TaskCompletionSource<string?> Outcome(string id)
    {
        // Caller holds _outcomes.
        var cutoff = DateTime.UtcNow - OutcomeKeep;
        foreach (var old in _outcomes.Where(kv => kv.Value.Tcs.Task.IsCompleted && kv.Value.DoneUtc < cutoff)
                     .Select(kv => kv.Key).ToList())
            _outcomes.Remove(old);
        if (!_outcomes.TryGetValue(id, out var e))
            _outcomes[id] = e = (new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously), default);
        return e.Tcs;
    }

    private void Complete(string id, string? why)
    {
        lock (_outcomes)
        {
            var tcs = Outcome(id);
            if (tcs.TrySetResult(why)) _outcomes[id] = (tcs, DateTime.UtcNow);
        }
    }

    private async Task RunAsync(Cam cam, EventRecord rec)
    {
        string? outcome = "the fetch ended without a result";
        try { outcome = await FetchLoopAsync(cam, rec).ConfigureAwait(false); }
        finally { Complete(rec.Id, outcome); }
    }

    /// <summary>Null when fetched, otherwise why not (also logged).</summary>
    private async Task<string?> FetchLoopAsync(Cam cam, EventRecord rec)
    {
        int misses = 0;
        var deadline = DateTime.UtcNow + Patience;
        // Right after the event the hold (OnEventClosed) keeps the camera up, or brings a
        // camera that had already parked straight back; past this window a sleeping camera
        // is not woken again, its next self-wake is awaited instead.
        var reconnectUntil = DateTime.UtcNow + ReconnectFor;
        try
        {
            while (!_shutdown.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (_store.Find(rec.Id) == null) return "the event was deleted meanwhile";
                if (!cam.Control.Online)
                {
                    if (DateTime.UtcNow < reconnectUntil)
                    {
                        try { cam.Hold?.Invoke(HoldSpan); } catch { }
                        await Task.Delay(TimeSpan.FromSeconds(5), _shutdown).ConfigureAwait(false);
                        continue;
                    }
                    lock (cam) cam.SeenAsleep = true;
                    await Task.Delay(TimeSpan.FromMinutes(1), _shutdown).ConfigureAwait(false);
                    continue;
                }
                if (!StretchOpen(cam, rec.Camera))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _shutdown).ConfigureAwait(false);
                    continue;
                }
                // Keep it up while this one waits its turn for the card.
                try { cam.Hold?.Invoke(HoldSpan); } catch { }
                string? verdict;
                await cam.Gate.WaitAsync(_shutdown).ConfigureAwait(false);
                try
                {
                    if (!await CardReadyAsync(cam, rec.Camera, _shutdown).ConfigureAwait(false))
                        return cam.Card.Why.Length > 0 ? cam.Card.Why : "the SD card is not ready";
                    verdict = await TryOnceAsync(cam, rec, _shutdown).ConfigureAwait(false);
                    lock (cam) cam.LastActive = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
                {
                    verdict = "retry: a viewer's transfer took precedence"; // pre-empted by a pick in the SD view
                }
                catch (Exception ex) when (ex is IOException or CameraOfflineException or TimeoutException)
                {
                    verdict = $"retry: {Log.Flatten(ex)}"; // a dropped link or a parked camera: worth another go
                }
                finally { cam.Gate.Release(); }
                if (verdict == null) return null; // fetched
                if (verdict.StartsWith("retry: ", StringComparison.Ordinal))
                {
                    if (++misses >= 6)
                    {
                        Log.Info($"{rec.Camera}: no camera copy for the event at {rec.StartUtc.ToLocalTime():HH:mm:ss}: {verdict[7..]}");
                        return verdict[7..];
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20), _shutdown).ConfigureAwait(false);
                    continue;
                }
                Log.Info($"{rec.Camera}: no camera copy for the event at {rec.StartUtc.ToLocalTime():HH:mm:ss}: {verdict}");
                return verdict;
            }
            if (_shutdown.IsCancellationRequested) return "the server is shutting down";
            Log.Info($"{rec.Camera}: no camera copy for the event at {rec.StartUtc.ToLocalTime():HH:mm:ss}: " +
                     $"the camera stayed out of reach for {Patience.TotalHours:0} h");
            return $"the camera stayed out of reach for {Patience.TotalHours:0} h";
        }
        catch (OperationCanceledException) { return "the server is shutting down"; }
        catch (Exception ex)
        {
            Log.Info($"{rec.Camera}: fetching the camera's copy of the event at {rec.StartUtc.ToLocalTime():HH:mm:ss} failed: {Log.Flatten(ex)}");
            return $"fetching it failed: {Log.Flatten(ex)}";
        }
    }

    /// <summary>Null when fetched; "retry: why" to try again; any other text gives up.</summary>
    private async Task<string?> TryOnceAsync(Cam cam, EventRecord rec, CancellationToken ct)
    {
        // Card times are camera-local; the server runs in the same zone.
        var startLocal = rec.StartUtc.ToLocalTime();
        var endLocal = rec.EndUtc.ToLocalTime();
        cam.Hold?.Invoke(HoldSpan); // the search and transfer must outlive the park timer
        var shift = await ClockShiftAsync(cam, rec.Camera, ct).ConfigureAwait(false);
        var list = await cam.Control.GetSdRecordingsAsync(DateOnly.FromDateTime(startLocal - shift), ct).ConfigureAwait(false);
        if (list == null) return "retry: the camera did not answer the SD search";
        var pick = Pick(list, startLocal, endLocal, shift);
        if (pick == null) return "retry: the card lists no recording inside the event";
        var pickStart = pick.Start + shift;
        var pickEnd = pick.End + shift;
        if (pickStart > startLocal + After)
            return $"the card's first clip starts {(pickStart - startLocal).TotalSeconds:0} s after Neolink's recording, so there is nothing to fill";
        if (pickEnd > DateTime.Now.AddSeconds(-5)) return "retry: the camera is still writing it";
        if (pick.SizeBytes > MaxBytes) return $"its copy is {pick.SizeBytes / 1024 / 1024} MB, above the {MaxBytes / 1024 / 1024} MB limit";

        cam.Hold?.Invoke(HoldSpan);
        var download = await cam.Control.OpenSdRecordingAsync(pick.Name, ct, yield: true).ConfigureAwait(false);
        var spooled = await Web.SdSpool.SpoolAsync(rec.Camera, pick.Name, download, ct).ConfigureAwait(false);
        if (_store.Find(rec.Id) == null) return "the event was deleted meanwhile";
        var dir = _store.EventDir(rec);
        var tmp = Path.Combine(dir, "camera.mp4.tmp");
        await using (var src = File.OpenRead(spooled))
        await using (var dst = FootageVault.Create(tmp))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        File.Move(tmp, Path.Combine(dir, "camera.mp4"), overwrite: true);
        Web.SdSpool.Discard(rec.Camera, pick.Name); // the event folder holds it now

        rec.HasCameraClip = true;
        rec.CameraClipStartUtc = pickStart.ToUniversalTime();
        rec.CameraClipSeconds = (pickEnd - pickStart).TotalSeconds;
        _store.Save(rec);
        var lead = (startLocal - pickStart).TotalSeconds;
        Log.Info($"{rec.Camera}: the camera's own copy of the event at {startLocal:HH:mm:ss} fetched from its SD card " +
                 $"({pick.SizeBytes / 1024} KB, starts {lead:0} s {(lead >= 0 ? "before" : "after")} Neolink's" +
                 $"{(shift == TimeSpan.Zero ? "" : $", camera clock corrected by {shift.TotalSeconds:+0;-0} s")})");
        return null;
    }

    /// <summary>The earliest card recording inside the event (from 45 s before its start to its end):
    /// a long Neolink event spans several of the camera's short PIR clips. Card times plus
    /// <paramref name="shift"/> are server time.</summary>
    internal static SdRecording? Pick(IReadOnlyList<SdRecording> list, DateTime startLocal, DateTime endLocal,
        TimeSpan shift = default) =>
        list.Where(r => r.Start + shift >= startLocal - Before && r.Start + shift <= endLocal)
            .OrderBy(r => r.Start)
            .FirstOrDefault();
}
