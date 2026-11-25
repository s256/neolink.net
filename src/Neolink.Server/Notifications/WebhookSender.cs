// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neolink.Notifications;

/// <summary>
/// Delivers an alert to the user-configured HTTP endpoint. Body modes:
/// "json" (full alert, snapshots base64), "text" (rendered template),
/// "snapshot" (first image as body, text in headers — the ntfy shape),
/// "multipart" (payload_json plus image files — the Discord shape),
/// "telegram" (Bot API: sendAnimation for an event GIF, sendPhoto for a
/// snapshot, sendMessage for text only; the URL carries bot token and chat_id).
/// Placeholders render in the template and in header values.
/// </summary>
internal static class WebhookSender
{
    // The clients' own timeout is only a backstop — the real budget is
    // per-request (TimeoutFor), scaled to what the body carries.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // Homelab endpoints commonly sit behind an internal CA the server has no
    // root for; opt-in per settings, never the default.
    private static readonly HttpClient HttpInsecure = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    }) { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Per-request budget: a fixed window plus upload time by
    /// attachment size (base64 in json mode makes the payload 4/3 the raw
    /// bytes), sized for a ~256 kbit/s worst-case uplink, capped at ten
    /// minutes — a fixed window can never deliver a snapshot-heavy body on a
    /// slow uplink.</summary>
    internal static TimeSpan TimeoutFor(Alert alert)
    {
        long bytes = 0;
        if (alert.Attachments != null) foreach (var a in alert.Attachments) bytes += a.Data.LongLength;
        var total = TimeSpan.FromSeconds(15) + TimeSpan.FromSeconds(bytes * 4.0 / 3.0 / 32_000);
        return total > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : total;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal const string DefaultTextTemplate = "{title}\n{message}\n{link}";
    internal const string DefaultMultipartTemplate = "{\"content\":\"{title}\\n{message}\\n{link}\"}";
    internal const string DefaultTelegramTemplate = "{title}\n{message}\n{link}";

    // Telegram's own limits: captions on media, text on plain messages.
    private const int TelegramCaptionMax = 1024;
    private const int TelegramTextMax = 4096;

    public static async Task SendAsync(NotificationSettings s, string token, Alert alert,
        string serverName, CancellationToken ct)
    {
        using var req = BuildRequest(s, token, alert, serverName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutFor(alert));
        using var res = await (s.WebhookInsecureTls ? HttpInsecure : Http)
            .SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = "";
            try { body = (await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)).Trim(); }
            catch { }
            if (body.Length > 200) body = body[..200];
            throw new IOException($"HTTP {(int)res.StatusCode} {res.ReasonPhrase}" +
                                  (body.Length > 0 ? $": {body}" : ""));
        }
    }

    internal static HttpRequestMessage BuildRequest(NotificationSettings s, string token,
        Alert alert, string serverName)
    {
        if (s.WebhookBodyMode == "telegram")
            return TelegramRequest(s, alert, serverName);
        token = token.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();
        var method = string.Equals(s.WebhookMethod, "PUT", StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Put : HttpMethod.Post;
        var req = new HttpRequestMessage(method, s.WebhookUrl.Trim());

        var mode = s.WebhookBodyMode;
        var first = alert.Attachments is { Count: > 0 } att ? att[0] : null;
        bool snapshotFallback = mode == "snapshot" && first == null;
        if (snapshotFallback) mode = "text";
        // A GIF-only alert (the event-GIF follow-up) in snapshot mode: the
        // preset's X-Filename says snapshot.jpg, which would mislabel it.
        bool gifBody = mode == "snapshot" && first?.ContentType == EventGif.ContentType;

        // A JSON-shaped template needs its substituted values escaped, or one
        // quote in a camera name 400s every delivery. Text mode is JSON exactly
        // when the user's Content-Type header says so (the Slack/Gotify presets).
        bool jsonBody = ParseHeaderLines(s.WebhookHeaders).Any(h =>
            h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains("json", StringComparison.OrdinalIgnoreCase));
        var link = Link(s, alert);

        req.Content = mode switch
        {
            "text" => new StringContent(
                Render(Template(s.WebhookBodyTemplate, DefaultTextTemplate), alert, serverName, jsonBody, link, GifLink(s, alert)),
                Encoding.UTF8),
            "snapshot" => ImageContent(first!),
            "multipart" => MultipartContent(s, alert, serverName),
            _ => JsonPayload(s, alert, serverName),
        };

        bool hasAuthLine = false, hasClickLine = false;
        foreach (var (name, value) in ParseHeaderLines(s.WebhookHeaders))
        {
            // ntfy reads the message from X-Message only when the body is the
            // image; on the text fallback the body already carries it.
            if (snapshotFallback && name.Equals("X-Message", StringComparison.OrdinalIgnoreCase)) continue;
            if (snapshotFallback && name.Equals("X-Filename", StringComparison.OrdinalIgnoreCase)) continue;
            if (gifBody && name.Equals("X-Filename", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasAuthLine = true;
            if (name.Equals("Click", StringComparison.OrdinalIgnoreCase)
                || name.Equals("X-Click", StringComparison.OrdinalIgnoreCase)) hasClickLine = true;
            var v = HeaderValue(Render(value, alert, serverName, jsonEscape: false, link, GifLink(s, alert)));
            if (!req.Headers.TryAddWithoutValidation(name, v))
            {
                req.Content.Headers.Remove(name);
                req.Content.Headers.TryAddWithoutValidation(name, v);
            }
        }
        // The stored token is the easy path; an explicit Authorization line is
        // the escape hatch for other schemes and must win.
        if (!hasAuthLine && token.Length > 0)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + HeaderValue(token));
        // Snapshot mode is the ntfy shape: a tap should open the event, so the
        // deep link rides the native Click header (an explicit line wins).
        if (mode == "snapshot" && !hasClickLine && link.Length > 0)
            req.Headers.TryAddWithoutValidation("X-Click", HeaderValue(link));
        if (gifBody)
            req.Headers.TryAddWithoutValidation("X-Filename", HeaderValue(first!.Name));
        return req;
    }

    /// <summary>Telegram Bot API request. The configured URL is
    /// https://api.telegram.org/bot&lt;TOKEN&gt;/sendMessage?chat_id=&lt;CHAT&gt; (any
    /// send* method, or none, may end the path — it is replaced by the one the
    /// alert needs): sendAnimation when it carries a GIF, sendPhoto for a JPEG
    /// snapshot, sendMessage otherwise. Text rides as caption/text, plain (no
    /// parse_mode, so a camera name with _ or * can never 400 the delivery).</summary>
    internal static HttpRequestMessage TelegramRequest(NotificationSettings s, Alert alert, string serverName)
    {
        var gif = alert.Attachments?.FirstOrDefault(a => a.ContentType == EventGif.ContentType);
        var photo = alert.Attachments?.FirstOrDefault(a => a.ContentType == "image/jpeg");
        var (method, field, media) = gif != null ? ("sendAnimation", "animation", gif)
            : photo != null ? ("sendPhoto", "photo", photo)
            : ("sendMessage", "text", (EmailAttachment?)null);

        var url = TelegramUrl(s.WebhookUrl, method);
        var text = Render(Template(s.WebhookBodyTemplate, DefaultTelegramTemplate), alert, serverName,
            jsonEscape: false, Link(s, alert), GifLink(s, alert)).Trim();
        // chat_id lives in the query string, where Telegram reads it alongside
        // the multipart form; without it every send would 400.
        if (!url.Query.Contains("chat_id=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "the Telegram URL needs ?chat_id=<your chat id> at the end");
        var mp = new MultipartFormDataContent();
        if (media == null)
        {
            mp.Add(new StringContent(Clip(text.Length > 0 ? text : alert.Subject, TelegramTextMax),
                Encoding.UTF8), "text");
        }
        else
        {
            if (text.Length > 0)
                mp.Add(new StringContent(Clip(text, TelegramCaptionMax), Encoding.UTF8), "caption");
            var part = new ByteArrayContent(media.Data);
            part.Headers.ContentType = new MediaTypeHeaderValue(media.ContentType);
            mp.Add(part, field, SmtpSender.HeaderSafeName(media.Name));
        }
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = mp };
        // Extra header lines still apply (a proxy in front of a self-hosted Bot
        // API server may want auth); the bearer token is never sent here — the
        // bot token already rides in the URL.
        foreach (var (name, value) in ParseHeaderLines(s.WebhookHeaders))
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            var v = HeaderValue(Render(value, alert, serverName, jsonEscape: false, Link(s, alert), GifLink(s, alert)));
            req.Headers.TryAddWithoutValidation(name, v);
        }
        return req;
    }

    /// <summary>The configured Telegram URL with its last path segment set to
    /// <paramref name="method"/>: ".../bot123:ABC/sendPhoto?chat_id=1" and
    /// ".../bot123:ABC?chat_id=1" both become ".../bot123:ABC/{method}?chat_id=1".</summary>
    internal static Uri TelegramUrl(string configured, string method)
    {
        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var u))
            throw new InvalidOperationException("the Telegram URL is not a valid absolute URL");
        var segs = u.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segs.Count > 0 && segs[^1].StartsWith("send", StringComparison.OrdinalIgnoreCase))
            segs.RemoveAt(segs.Count - 1);
        if (segs.Count == 0 || !segs[^1].StartsWith("bot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "the Telegram URL must look like https://api.telegram.org/bot<TOKEN>/sendMessage?chat_id=<CHAT>");
        segs.Add(method);
        var b = new UriBuilder(u) { Path = "/" + string.Join('/', segs) };
        return b.Uri;
    }

    private static string Clip(string v, int max) => v.Length <= max ? v : v[..(max - 1)] + "…";

    /// <summary>The deep link that opens this event in the web UI, or "" when
    /// the server has no PublicUrl configured or the alert is not an event.</summary>
    internal static string Link(NotificationSettings s, Alert alert)
    {
        var baseUrl = s.PublicUrl.Trim().TrimEnd('/');
        return baseUrl.Length == 0 || alert.Event is not { Id.Length: > 0 } e
            ? ""
            : $"{baseUrl}/events?event={Uri.EscapeDataString(e.Id)}";
    }

    /// <summary>Where the event's GIF can be downloaded (needs PublicUrl and an
    /// event; the endpoint itself needs a login or RTSP Basic credentials), or "".</summary>
    internal static string GifLink(NotificationSettings s, Alert alert)
    {
        var baseUrl = s.PublicUrl.Trim().TrimEnd('/');
        return baseUrl.Length == 0 || alert.Event is not { Id.Length: > 0 } e || !s.EventGif
            ? ""
            : $"{baseUrl}/api/events/{Uri.EscapeDataString(e.Id)}/gif";
    }

    private static string Template(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    private static ByteArrayContent ImageContent(EmailAttachment a)
    {
        var content = new ByteArrayContent(a.Data);
        content.Headers.ContentType = new MediaTypeHeaderValue(a.ContentType);
        return content;
    }

    private static HttpContent JsonPayload(NotificationSettings s, Alert alert, string serverName)
    {
        var e = alert.Event;
        var payload = new
        {
            type = e != null ? "event" : "alert",
            server = serverName,
            title = alert.Headline,
            message = alert.Brief ?? alert.Body,
            detail = alert.Brief != null ? alert.Body : null,
            link = Link(s, alert) is { Length: > 0 } l ? l : null,
            camera = e?.Camera,
            labels = e?.Labels,
            time = (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            durationSeconds = e == null ? (double?)null : Math.Round(e.DurationSeconds),
            ongoing = e?.Ongoing,
            snapshots = alert.Attachments?.Where(a => a.ContentType != EventGif.ContentType)
                .Select(a => Convert.ToBase64String(a.Data)).ToList() is { Count: > 0 } shots
                ? shots
                : null,
            gif = alert.Attachments?.FirstOrDefault(a => a.ContentType == EventGif.ContentType) is { } g
                ? Convert.ToBase64String(g.Data)
                : null,
            gifUrl = GifLink(s, alert) is { Length: > 0 } gl ? gl : null,
        };
        return new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");
    }

    private static HttpContent MultipartContent(NotificationSettings s, Alert alert, string serverName)
    {
        var mp = new MultipartFormDataContent();
        mp.Add(new StringContent(
            Render(Template(s.WebhookBodyTemplate, DefaultMultipartTemplate), alert, serverName,
                jsonEscape: true, Link(s, alert), GifLink(s, alert)),
            Encoding.UTF8, "application/json"), "payload_json");
        if (alert.Attachments != null)
            for (int i = 0; i < alert.Attachments.Count; i++)
            {
                var a = alert.Attachments[i];
                var part = new ByteArrayContent(a.Data);
                part.Headers.ContentType = new MediaTypeHeaderValue(a.ContentType);
                mp.Add(part, $"file{i + 1}", SmtpSender.HeaderSafeName(a.Name));
            }
        return mp;
    }

    /// <summary>Substitutes the documented placeholders; anything else in the
    /// template — JSON braces included — passes through untouched. With
    /// <paramref name="jsonEscape"/> the substituted VALUES are JSON-string
    /// escaped (the template's own syntax is the user's business). {message} is
    /// the push-length Brief when the alert carries one; {detail} is always the
    /// full body.</summary>
    internal static string Render(string template, Alert alert, string serverName,
        bool jsonEscape = false, string link = "", string gifLink = "")
    {
        string V(string v) => jsonEscape ? JsonEncodedText.Encode(v).ToString() : v;
        var e = alert.Event;
        return template
            .Replace("{title}", V(alert.Headline))
            .Replace("{message}", V(alert.Brief ?? alert.Body))
            .Replace("{detail}", V(alert.Body))
            .Replace("{link}", V(link))
            .Replace("{gif}", V(gifLink))
            .Replace("{subject}", V(alert.Subject))
            .Replace("{camera}", V(e?.Camera ?? ""))
            .Replace("{labels}", V(e != null ? string.Join(" + ", e.Labels) : ""))
            .Replace("{time}", (e?.StartUtc ?? DateTime.UtcNow).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("{duration}", e != null
                ? Math.Round(e.DurationSeconds).ToString(CultureInfo.InvariantCulture) : "")
            .Replace("{status}", e == null ? "alert" : e.Ongoing ? "ongoing" : "ended")
            .Replace("{server}", V(serverName));
    }

    /// <summary>"Name: value" lines; blank lines, colon-less lines and lines
    /// whose name is not an RFC 7230 token are skipped — one saved typo like
    /// "X Title: x" must not abort every delivery at request-build time.</summary>
    internal static List<(string Name, string Value)> ParseHeaderLines(IEnumerable<string>? lines)
    {
        var result = new List<(string, string)>();
        foreach (var raw in lines ?? Enumerable.Empty<string>())
        {
            int i = raw.IndexOf(':');
            if (i <= 0) continue;
            var name = raw[..i].Trim();
            if (name.Length == 0 || !name.All(IsTokenChar)) continue;
            result.Add((name, raw[(i + 1)..].Trim()));
        }
        return result;
    }

    private static bool IsTokenChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
            or '^' or '_' or '`' or '|' or '~';

    /// <summary>Header values must be Latin-1-safe and free of control
    /// characters (injection); em-dashes fold to '-', other non-ASCII to '?'.</summary>
    internal static string HeaderValue(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (var c in v)
            sb.Append(c switch
            {
                '—' or '–' => '-',
                < ' ' => ' ',
                > '~' => '?',
                _ => c,
            });
        return sb.ToString();
    }
}
