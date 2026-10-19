namespace Scout.Core;

internal static class SubsequenceMatcher
{
    internal static bool Contains(string name, string term)
    {
        var j = 0; foreach (var c in name) { if (j < term.Length && char.ToLowerInvariant(c) == term[j]) j++; } return j == term.Length;
    }
    internal static double Score(string name, string term)
    {
        var first = -1; var last = -1; var j = 0; var consecutive = 0; var run = 0; var boundary = 0;
        for (var i = 0; i < name.Length && j < term.Length; i++)
        {
            if (char.ToLowerInvariant(name[i]) != term[j]) continue;
            if (first < 0) { first = i; if (i > 0 && char.IsLetterOrDigit(name[i - 1]) && !(char.IsUpper(name[i]) && char.IsLower(name[i - 1]))) return 0; }
            run = i == last + 1 ? run + 1 : 1; consecutive = Math.Max(consecutive, run);
            if (i == 0 || !char.IsLetterOrDigit(name[i - 1]) || char.IsUpper(name[i]) && char.IsLower(name[i - 1])) boundary++;
            last = i; j++;
        }
        if (j != term.Length || consecutive < 2 || last - first + 1 > term.Length * 2.5) return 0;
        return Math.Clamp(15 + consecutive * 3 + boundary * 4 - (last - first + 1 - term.Length) * 2, 1, 40);
    }
}
