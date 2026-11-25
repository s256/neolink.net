// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Diagnostics;
using System.Globalization;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Turns a finished event clip into a short animated GIF (event.gif, next to
/// clip.mp4 in the event folder) for notifications and for download from
/// GET /api/events/{id}/gif. The clip goes through <see cref="FootageVault"/>
/// on the way in (encrypted footage works) and the GIF is written through it
/// on the way out (encrypted when new footage is). Long events are sped up to
/// fit <see cref="NotificationSettings.GifMaxSeconds"/> instead of being cut
/// off, so the whole event is always in the animation. When the result is
/// over the size budget, smaller/slower profiles are tried before giving up.
/// Never throws: a missing ffmpeg, clip or a failed encode returns null.
/// </summary>
public static class EventGif
{
    public const string FileName = "event.gif";
    public const string ContentType = "image/gif";

    /// <summary>Seconds of pre-roll kept ahead of the detection, so the
    /// animation shows the subject entering rather than starting mid-stride.</summary>
    internal const double LeadInSeconds = 1.0;

    // One encode at a time, server-wide: a burst of events across cameras must
    // queue rather than run a pile of ffmpegs next to the recording pumps.
    private static readonly SemaphoreSlim EncodeGate = new(1, 1);

    // A 60 s, 5 MP clip through a pipe on a slow NAS, plus palette generation.
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(180);

    /// <summary>One encode attempt's shape.</summary>
    internal readonly record struct Profile(int Width, int Fps, int Colors);

    /// <summary>The profiles tried in order: the configured one first, then
    /// progressively smaller ones until the GIF fits the byte budget.</summary>
    internal static IReadOnlyList<Profile> Profiles(int width, int fps)
    {
        width = Math.Clamp(width, 160, 1280);
        fps = Math.Clamp(fps, 2, 15);
        var list = new List<Profile> { new(width, fps, 128) };
        int w = width, f = fps;
        for (int i = 0; i < 4; i++)
        {
            w = Math.Max(160, (int)(w * 0.75) / 2 * 2);
            f = Math.Max(3, f - 2);
            var p = new Profile(w, f, i < 2 ? 96 : 64);
            if (!list.Contains(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>Speed-up factor so <paramref name="spanSeconds"/> of footage
    /// plays in at most <paramref name="maxSeconds"/>; 1 = real time.</summary>
    internal static double SpeedFactor(double spanSeconds, int maxSeconds) =>
        maxSeconds <= 0 || spanSeconds <= maxSeconds ? 1.0 : spanSeconds / maxSeconds;

    /// <summary>The -filter_complex graph for one attempt: drop pre-roll
    /// (a pipe cannot seek), rebase and speed up timestamps, resample, scale,
    /// and build a per-clip palette — the difference between a muddy GIF and
    /// a readable one.</summary>
    internal static string FilterGraph(double skipSeconds, double speed, Profile p)
    {
        var parts = new List<string>();
        if (skipSeconds > 0.05)
            parts.Add(FormattableString.Invariant($"trim=start={skipSeconds:0.###}"));
        parts.Add(speed > 1.001
            ? FormattableString.Invariant($"setpts=(PTS-STARTPTS)/{speed:0.####}")
            : "setpts=PTS-STARTPTS");
        parts.Add(FormattableString.Invariant($"fps={p.Fps}"));
        parts.Add(FormattableString.Invariant($"scale={p.Width}:-2:flags=lanczos"));
        return "[0:v]" + string.Join(",", parts) +
               FormattableString.Invariant(
                   $",split[a][b];[a]palettegen=max_colors={p.Colors}:stats_mode=diff[pal];") +
               "[b][pal]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";
    }

    /// <summary>Path of the event's GIF if it was already made.</summary>
    public static string? Existing(EventStore events, string eventId) =>
        events.ArtifactPath(eventId, FileName);

    /// <summary>Makes (or reuses) the event's GIF and returns its bytes, or null.
    /// <paramref name="triggerUtc"/> is the detection time (pre-roll before it,
    /// minus a short lead-in, is skipped).</summary>
    public static async Task<byte[]?> CreateAsync(EventStore events, EventRecord rec,
        NotificationSettings s, DateTime? triggerUtc)
    {
        try
        {
            if (Existing(events, rec.Id) is { } done)
                return await ReadAsync(done).ConfigureAwait(false);
            if (Media.Ffmpeg.ExePath is not { } ffmpeg)
            {
                Log.Info($"{rec.Camera}: event GIF skipped — it needs ffmpeg, and none was found " +
                         "(set NEOLINK_FFMPEG, or install ffmpeg; the Docker image ships one)");
                return null;
            }
            if (rec.Ongoing)
            {
                Log.Debug($"{rec.Camera}: event GIF requested while the event is ongoing — skipped");
                return null;
            }
            string dir;
            try { dir = events.EventDir(rec); }
            catch (KeyNotFoundException) { return null; } // deleted/expired meanwhile
            var clip = Path.Combine(dir, "clip.mp4");
            if (!rec.HasClip || !File.Exists(clip))
            {
                Log.Info($"{rec.Camera}: event GIF skipped — this event has no saved clip");
                return null;
            }

            double skip = triggerUtc is { } t && t > rec.StartUtc
                ? Math.Max(0, (t - rec.StartUtc).TotalSeconds - LeadInSeconds)
                : 0;
            double clipLen = rec.ClipSeconds is double cs && cs > 0
                ? cs
                : Math.Max(1, (rec.EndUtc - rec.StartUtc).TotalSeconds);
            if (skip >= clipLen - 0.5) skip = 0; // clip shorter than its pre-roll: keep it all
            double span = Math.Max(1, clipLen - skip);
            int maxSeconds = Math.Clamp(s.GifMaxSeconds, 3, 60);
            double speed = SpeedFactor(span, maxSeconds);
            long budget = (long)Math.Clamp(s.GifMaxMegabytes, 1, 49) * 1024 * 1024;

            await EncodeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Someone else may have made it while this one queued.
                if (Existing(events, rec.Id) is { } made)
                    return await ReadAsync(made).ConfigureAwait(false);
                return await EncodeAndStoreAsync(events, ffmpeg, rec, s, clip, dir, skip, span, speed, maxSeconds, budget)
                    .ConfigureAwait(false);
            }
            finally { EncodeGate.Release(); }
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Camera}: event GIF could not be made: {Log.Flatten(ex)} " +
                     "— the event and its clip are unaffected");
            return null;
        }
    }

    private static async Task<byte[]?> EncodeAndStoreAsync(EventStore events, string ffmpeg, EventRecord rec,
        NotificationSettings s, string clip, string dir, double skip, double span, double speed,
        int maxSeconds, long budget)
    {
        var sw = Stopwatch.StartNew();
        byte[]? best = null;
        foreach (var p in Profiles(s.GifWidth, s.GifFps))
        {
            byte[] gif;
            try
            {
                gif = await EncodeAsync(ffmpeg, clip, skip, speed, maxSeconds, p).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"{rec.Camera}: event GIF encode failed ({Log.Flatten(ex)})");
                return null; // a broken clip will not get better at a smaller size
            }
            if (gif.Length < 6) return null;
            best = gif;
            if (gif.Length <= budget) break;
            Log.Debug($"{rec.Camera}: event GIF at {p.Width}px/{p.Fps}fps is " +
                      $"{gif.Length / 1024} KB, over the {budget / 1024} KB budget — retrying smaller");
        }
        if (best == null || best.Length > budget)
        {
            Log.Info($"{rec.Camera}: event GIF skipped — even the smallest profile was over " +
                     $"{s.GifMaxMegabytes} MB (raise the limit or lower the max length)");
            return null;
        }

        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        await FootageVault.WriteAllBytesAsync(tmp, best, CancellationToken.None).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        rec.HasGif = true; // the web UI's events page offers the GIF from here on
        events.Save(rec);
        var sped = speed > 1.001
            ? " at " + speed.ToString("0.#", CultureInfo.InvariantCulture) + "x speed"
            : "";
        Log.Info($"{rec.Camera}: event GIF ready ({best.Length / 1024} KB, " +
                 $"{span:0}s of footage{sped}, " +
                 $"{sw.Elapsed.TotalSeconds:0.0}s to encode)");
        return best;
    }

    private static async Task<byte[]> ReadAsync(string path)
    {
        await using var src = FootageVault.OpenRead(path); // decrypts when encrypted
        using var ms = new MemoryStream();
        await src.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static async Task<byte[]> EncodeAsync(string ffmpeg, string clipPath,
        double skipSeconds, double speed, int maxSeconds, Profile p)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            // Same appetite cap as snapshot sampling: a full-speed decode must
            // not starve the camera pumps on a small box.
            "-threads", "2",
            "-i", "pipe:0",
            "-filter_complex", FilterGraph(skipSeconds, speed, p),
            "-an",
            "-t", maxSeconds.ToString(CultureInfo.InvariantCulture),
            "-loop", "0",
            "-f", "gif", "pipe:1",
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg would not start");
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* may have exited already; priority is best-effort */ }
        using var cts = new CancellationTokenSource(EncodeTimeout);
        var feed = Task.Run(async () =>
        {
            try { await EventEmailer.FeedClipAsync(clipPath, proc.StandardInput.BaseStream, cts.Token).ConfigureAwait(false); }
            catch { /* ffmpeg may stop reading early (-t reached) — normal */ }
            finally { try { proc.StandardInput.Close(); } catch { } }
        });
        var drainErr = proc.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(stdout, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"ffmpeg did not finish the GIF within {EncodeTimeout.TotalSeconds:0} s");
        }
        if (!proc.WaitForExit(10_000)) { try { proc.Kill(entireProcessTree: true); } catch { } }
        await Task.WhenAny(feed, Task.Delay(5_000)).ConfigureAwait(false);
        if (proc.HasExited && proc.ExitCode != 0)
        {
            var err = (await drainErr.ConfigureAwait(false)).Split('\n')
                .Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new IOException(err ?? $"ffmpeg exit code {proc.ExitCode}");
        }
        return stdout.ToArray();
    }
}
