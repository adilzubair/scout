namespace Scout.Core;

/// <summary>Only these fixed identifiers can reach the Windows command executor.</summary>
public static class SystemCommands
{
    public static readonly Candidate[] Items =
    [
        Command("appearance", "Toggle dark mode", "Switch Windows and app appearance", "dark", "dark mode", "toggle dark mode", "appearance", "theme", "toggle theme"),
        Command("wifi-on", "Turn Wi-Fi on", "Enable the wireless radio", "wifi on", "wi fi on", "turn wifi on", "enable wifi", "enable wi fi", "turn wi fi on", "wireless on"),
        Command("wifi-off", "Turn Wi-Fi off", "Disable the wireless radio", "wifi off", "wi fi off", "turn wifi off", "disable wifi", "disable wi fi", "turn wi fi off", "wireless off"),
        Command("bluetooth-on", "Turn Bluetooth on", "Enable the Bluetooth radio", "bluetooth on", "bt on", "enable bluetooth", "turn bluetooth on", "turn on bluetooth", "switch bluetooth on", "switch on bluetooth"),
        Command("bluetooth-off", "Turn Bluetooth off", "Disable the Bluetooth radio", "bluetooth off", "bt off", "disable bluetooth", "turn bluetooth off", "turn off bluetooth", "switch bluetooth off", "switch off bluetooth"),
        Command("focus", "Do not disturb", "Open Windows notification settings", "dnd", "focus", "do not disturb", "notifications", "silence notifications"),
        Command("sleep", "Sleep", "Put this PC to sleep", "sleep", "sleep pc", "sleep computer", "put pc to sleep"),
        Command("lock", "Lock screen", "Lock this Windows session", "lock", "lock screen", "lock pc", "lock computer"),
        Command("recycle", "Empty Recycle Bin", "Permanently delete recycled items after confirmation", "empty trash", "empty recycle bin", "empty bin"),
        Command("hidden-show", "Show hidden files", "Show hidden items in File Explorer", "show hidden files", "show hidden", "reveal hidden files"),
        Command("hidden-hide", "Hide hidden files", "Hide hidden items in File Explorer", "hide hidden files", "hide hidden"),
    ];
    private static Candidate Command(string id, string title, string detail, params string[] keywords) => new("system:" + id, ItemKind.System, title, id) { Detail = detail, Source = "Windows", Keywords = keywords };
    public static bool LooksLikeCommand(string query) => Items.Any(c => c.Title.Equals(query, StringComparison.OrdinalIgnoreCase) || c.Keywords.Contains(query, StringComparer.OrdinalIgnoreCase)) || query is "wifi" or "wi fi" or "wireless" or "bluetooth" or "bt" or "hidden files";
    public static bool IsKnown(string id) => Items.Any(c => c.Target == id);
}

public static class SyntheticResults
{
    public static Candidate? Calculation(string query)
    {
        var value = Calculator.Evaluate(query);
        return value == null ? null : new("calculation:" + value, ItemKind.Calculation, "= " + value, value) { Detail = query.Trim() + "  ·  Enter to copy", Source = "Calculator" };
    }
    public static Candidate? Web(string query)
    {
        query = query.Trim();
        if (query.Length == 0 || query.Length > 512) return null;
        if (Uri.TryCreate(query.Contains("://", StringComparison.Ordinal) ? query : "https://" + query, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.Host.Contains('.') && !query.Any(char.IsWhiteSpace) && !query.Contains('\\'))
            return new("url:" + uri.AbsoluteUri, ItemKind.WebSearch, "Open " + uri.Host, uri.AbsoluteUri) { Detail = uri.AbsoluteUri, Source = "Web address" };
        return new("web:" + query, ItemKind.WebSearch, "Search the web for “" + query + "”", "https://www.google.com/search?q=" + Uri.EscapeDataString(query)) { Detail = "Open in your default browser", Source = "Web search" };
    }
}
