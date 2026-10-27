using System.Text.Json;

// A message for the Windows notification, received on <topicRoot>/<host>/notify.
sealed record Notification(string Title, string Message)
{
    // Windows cuts the notification title at 63 and the text at 255 characters.
    const int MaxTitle = 63, MaxMessage = 255;

    // Plain text is the message (what HA's MQTT notify entity sends); a JSON
    // object may also carry a title: {"title": "...", "message": "..."}.
    // Returns null for an empty payload.
    public static Notification? Parse(string payload, string defaultTitle)
    {
        string text = payload.Trim();
        string title = defaultTitle;
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    text = m.GetString()!.Trim();
                    if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                        && t.GetString()!.Trim() is { Length: > 0 } customTitle)
                        title = customTitle;
                }
            }
            catch (JsonException) { /* not JSON after all: show it as text */ }
        }
        if (text.Length == 0) return null;
        return new Notification(Cut(title, MaxTitle), Cut(text, MaxMessage));
    }

    static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
