namespace Scout.Core;

public static class FileTypes
{
    private static readonly Dictionary<string, string[]> Families = new()
    {
        [".pptx"] = [".pptx", ".ppt", ".pptm", ".ppsx", ".odp"],
        [".xlsx"] = [".xlsx", ".xls", ".xlsm", ".csv", ".ods"],
        [".docx"] = [".docx", ".doc", ".docm", ".odt", ".rtf"],
        ["image"] = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".svg"],
        ["video"] = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv"],
        ["audio"] = [".mp3", ".wav", ".m4a", ".ogg", ".flac", ".aac"],
        ["archive"] = [".zip", ".7z", ".rar", ".tar", ".gz"]
    };
    public static string[] Extensions(string family) => Families.GetValueOrDefault(family) ?? [family];
    public static bool Matches(string extension, string family) => extension == family || Families.TryGetValue(family, out var values) && values.Contains(extension);
}

/// <summary>Small, explicit vocabulary shared by retrieval. No speculative ancestor-path matches.</summary>
public static class SearchVocabulary
{
    private static readonly Dictionary<string, int> Concepts = Build();
    private static Dictionary<string, int> Build()
    {
        var groups = new[] { "invoice invoices billing bill bills", "budget budgets spending expenses expense", "resume résumé cv curriculum", "meeting meetings minutes", "roadmap roadmaps planning plan plans", "contract contracts agreement agreements", "salary salaries payroll payslip payslips", "photo photos picture pictures image images", "presentation presentations deck decks slide slides", "report reports", "note notes notebook", "research study studies", "guide guides tutorial tutorials manual manuals", "customer customers client clients", "company companies corporate corporation", "design designs mockup mockups", "browser browsing internet", "code coding programming development", "email mail emails", "terminal console shell", "password passwords credentials", "wifi wireless", "write writing", "edit editing editor", "chat messaging messenger", "music songs audio", "screenshot screenshots capture captures" };
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < groups.Length; i++) foreach (var word in groups[i].Split(' ')) values[word] = i;
        return values;
    }
    public static bool Related(string a, string b) => Concepts.TryGetValue(a, out var x) && Concepts.TryGetValue(b, out var y) && x == y;
}
