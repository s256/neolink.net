// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neolink.Notifications;

/// <summary>How the SMTP session is secured.</summary>
public enum SmtpSecurity { StartTls, Ssl, None }

/// <summary>
/// User-configured email-notification settings, persisted as notifications.json
/// in the state dir. The SMTP password is stored ONLY as an AES-GCM token
/// (<see cref="PasswordEnc"/>) via <see cref="SecretProtector"/> — never in
/// plaintext, and never returned to the UI (write-only).
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Master opt-in. Off = no email is ever sent, whatever else is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>The single address that receives every alert.</summary>
    public string Recipient { get; set; } = "";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string Username { get; set; } = "";

    /// <summary>AES-GCM token of the SMTP password (see SecretProtector); "" = none.</summary>
    public string PasswordEnc { get; set; } = "";

    /// <summary>From address; falls back to the username when blank.</summary>
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Neolink.NET";

    // Per-alert switches. Storage-full is the only one on by default (it needs no
    // per-site tuning); the noisier alerts start off so the user opts into each.
    public bool AlertStorage { get; set; } = true;
    public bool AlertOverload { get; set; }
    public bool AlertCameraOffline { get; set; }
    public bool AlertWriteFailure { get; set; }

    /// <summary>Default minutes a camera must stay unreachable before it alerts.</summary>
    public int OfflineThresholdMinutes { get; set; } = 10;

    /// <summary>Attach snapshots from the camera's last detection to its offline
    /// alert. Off by default: existing installs keep the plain text alert.</summary>
    public bool OfflineAttachSnapshots { get; set; }

    /// <summary>How many images that attachment carries (1-10).</summary>
    public int OfflineSnapshotCount { get; set; } = 3;

    /// <summary>Only attach when the last detection is this recent (minutes);
    /// 0 = no limit. Stops a week-old detection riding along as evidence.</summary>
    public int OfflineSnapshotLookbackMinutes { get; set; } = 60;

    // Detection-event emails (per-camera opt-in lives in RecordingSettings; the
    // knobs that shape every event email live here with the rest of the mail
    // config).
    /// <summary>Snapshots attached to each event email, sampled evenly across
    /// the clip. 1–50; 50 is deliberately the ceiling — beyond that the mail
    /// stops being a notification and starts being the recording.</summary>
    public int EventSnapshots { get; set; } = 3;

    /// <summary>Per-camera quiet time between event emails (minutes, 0 = every
    /// event). A busy driveway must not become a full inbox.</summary>
    public int EventCooldownMinutes { get; set; } = 2;

    /// <summary>Seconds into an event before its email is sent; snapshots cover
    /// the event so far. 0 = wait for the end and sample the whole clip.</summary>
    public int EventEmailDelaySeconds { get; set; } = 5;

    /// <summary>Where event snapshots come from. "clip" (default) decodes the
    /// saved clip file; "memory" samples the recording's own stream in RAM and
    /// never opens the clip — for clip storage on network mounts, where reading
    /// a file mid-write can damage it.</summary>
    public string EventSnapshotMode { get; set; } = "clip";

    // Animated GIF of each event, made from the finished clip (needs ffmpeg).
    /// <summary>Make event.gif for every event when it ends, keep it with the
    /// event (GET /api/events/{id}/gif) and attach it to event notifications.
    /// Off by default: it costs one ffmpeg encode per event.</summary>
    public bool EventGif { get; set; }

    /// <summary>Longest animation, in seconds (3-60). Longer events are sped up
    /// to fit rather than cut off, so the whole event is always in it.</summary>
    public int GifMaxSeconds { get; set; } = 12;

    /// <summary>Width in pixels (160-1280); height follows the aspect ratio.</summary>
    public int GifWidth { get; set; } = 480;

    /// <summary>Frames per second (2-15).</summary>
    public int GifFps { get; set; } = 8;

    /// <summary>Size budget in MB (1-49; Telegram bots may upload up to 50 MB,
    /// most mail servers stop at 10-25 MB). Over it, smaller profiles are tried.</summary>
    public int GifMaxMegabytes { get; set; } = 8;

    /// <summary>When the notification already left mid-event (send delay &gt; 0),
    /// send the GIF to the webhook as a second message once the event ends.
    /// With a delay of 0 the one notification carries the GIF itself.</summary>
    public bool GifFollowUp { get; set; } = true;

    // Webhook channel: one HTTP endpoint, shaped by a preset in the UI or the
    // raw knobs below. Independent of the email master switch.
    public bool WebhookEnabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    /// <summary>Trust any TLS certificate the endpoint presents (internal CA /
    /// self-signed reverse proxies).</summary>
    public bool WebhookInsecureTls { get; set; }
    /// <summary>AES-GCM token of the webhook access token, sent as
    /// "Authorization: Bearer …" (an explicit Authorization header line wins).
    /// Write-only like <see cref="PasswordEnc"/>; "" = none.</summary>
    public string WebhookTokenEnc { get; set; } = "";
    /// <summary>"POST" or "PUT".</summary>
    public string WebhookMethod { get; set; } = "POST";
    /// <summary>"json" (full event + snapshots base64), "text" (rendered
    /// template), "snapshot" (first image as body, text in headers — ntfy),
    /// "multipart" (payload_json + image files — Discord), "telegram" (Bot API
    /// sendAnimation/sendPhoto/sendMessage, chosen by what the alert carries).</summary>
    public string WebhookBodyMode { get; set; } = "json";
    /// <summary>Body template for text/multipart modes; blank = mode default.
    /// Placeholders render here and in header values.</summary>
    public string WebhookBodyTemplate { get; set; } = "";
    /// <summary>Extra request headers, one "Name: value" line each.</summary>
    public List<string> WebhookHeaders { get; set; } = new();
    /// <summary>UI memory only — which preset filled the knobs.</summary>
    public string WebhookPreset { get; set; } = "json";
    /// <summary>Send server alerts (storage, offline…) to the webhook too.</summary>
    public bool WebhookServerAlerts { get; set; } = true;
    /// <summary>The server's address as the USER reaches it (proxy, Docker and
    /// NAT hide it from the server itself). Optional; when set, webhook
    /// notifications carry a link that opens the event. "" = no links.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>Per-camera overrides of <see cref="OfflineThresholdMinutes"/>
    /// (0 = never alert for that camera). Absent = use the default.</summary>
    public Dictionary<string, int> CameraOfflineOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective offline threshold for a camera (minutes); 0 = off.</summary>
    public int OfflineMinutesFor(string camera) =>
        CameraOfflineOverrides.TryGetValue(camera, out var m) ? m : OfflineThresholdMinutes;

    /// <summary>The From address actually used (username when From is blank).</summary>
    public string EffectiveFrom => string.IsNullOrWhiteSpace(From) ? Username : From;

    public NotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Recipient = Recipient,
        SmtpHost = SmtpHost,
        SmtpPort = SmtpPort,
        Security = Security,
        Username = Username,
        PasswordEnc = PasswordEnc,
        From = From,
        FromName = FromName,
        AlertStorage = AlertStorage,
        AlertOverload = AlertOverload,
        AlertCameraOffline = AlertCameraOffline,
        AlertWriteFailure = AlertWriteFailure,
        OfflineThresholdMinutes = OfflineThresholdMinutes,
        OfflineAttachSnapshots = OfflineAttachSnapshots,
        OfflineSnapshotCount = OfflineSnapshotCount,
        OfflineSnapshotLookbackMinutes = OfflineSnapshotLookbackMinutes,
        CameraOfflineOverrides = new(CameraOfflineOverrides, StringComparer.OrdinalIgnoreCase),
        EventSnapshots = EventSnapshots,
        EventCooldownMinutes = EventCooldownMinutes,
        EventEmailDelaySeconds = EventEmailDelaySeconds,
        EventSnapshotMode = EventSnapshotMode,
        EventGif = EventGif,
        GifMaxSeconds = GifMaxSeconds,
        GifWidth = GifWidth,
        GifFps = GifFps,
        GifMaxMegabytes = GifMaxMegabytes,
        GifFollowUp = GifFollowUp,
        WebhookEnabled = WebhookEnabled,
        WebhookUrl = WebhookUrl,
        WebhookInsecureTls = WebhookInsecureTls,
        WebhookTokenEnc = WebhookTokenEnc,
        WebhookMethod = WebhookMethod,
        WebhookBodyMode = WebhookBodyMode,
        WebhookBodyTemplate = WebhookBodyTemplate,
        WebhookHeaders = new(WebhookHeaders),
        WebhookPreset = WebhookPreset,
        WebhookServerAlerts = WebhookServerAlerts,
        PublicUrl = PublicUrl,
    };
}

/// <summary>
/// Loads/saves <see cref="NotificationSettings"/> (notifications.json next to the
/// other UI state, owner-only). The plaintext SMTP password never leaves this
/// class: it is encrypted on the way in and only decrypted for the mail sender.
/// </summary>
public sealed class NotificationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();
    private NotificationSettings _settings = new();

    public NotificationStore(string stateDir, SecretProtector protector)
    {
        _file = Path.Combine(stateDir, "notifications.json");
        _protector = protector;
        try
        {
            if (File.Exists(_file))
                _settings = JsonSerializer.Deserialize<NotificationSettings>(File.ReadAllText(_file), JsonOpts)
                            ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification settings unreadable ({ex.Message}); notifications start disabled.");
        }
    }

    /// <summary>A private copy of the current settings (password stays encrypted).</summary>
    public NotificationSettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    /// <summary>The decrypted SMTP password for the mail sender; "" when none/unreadable.</summary>
    public string SmtpPassword()
    {
        string enc;
        lock (_gate) enc = _settings.PasswordEnc;
        return _protector.Unprotect(enc) ?? "";
    }

    /// <summary>True once a password has been stored (so the UI can show "set").</summary>
    public bool HasPassword
    {
        get { lock (_gate) return _settings.PasswordEnc.Length > 0; }
    }

    /// <summary>The decrypted webhook access token; "" when none/unreadable.</summary>
    public string WebhookToken()
    {
        string enc;
        lock (_gate) enc = _settings.WebhookTokenEnc;
        return _protector.Unprotect(enc) ?? "";
    }

    public bool HasWebhookToken
    {
        get { lock (_gate) return _settings.WebhookTokenEnc.Length > 0; }
    }

    /// <summary>Replaces the settings. <paramref name="newPassword"/> and
    /// <paramref name="newWebhookToken"/> are write-only: null keeps the stored
    /// value, non-null re-encrypts (""=clear it). A pasted "Bearer " prefix is
    /// forgiven — the sender adds the scheme itself.</summary>
    public void Save(NotificationSettings incoming, string? newPassword, string? newWebhookToken = null)
    {
        lock (_gate)
        {
            incoming.PasswordEnc = newPassword switch
            {
                null => _settings.PasswordEnc,      // unchanged
                "" => "",                            // cleared
                _ => _protector.Protect(newPassword) // set
            };
            if (newWebhookToken != null && newWebhookToken.TrimStart()
                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                newWebhookToken = newWebhookToken.TrimStart()["Bearer ".Length..].Trim();
            incoming.WebhookTokenEnc = newWebhookToken switch
            {
                null => _settings.WebhookTokenEnc,
                "" => "",
                _ => _protector.Protect(newWebhookToken.Trim()),
            };
            _settings = incoming;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, JsonOpts));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot persist notification settings: {ex.Message}");
        }
    }
}
