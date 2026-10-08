using System.Globalization;
using System.Text.RegularExpressions;

namespace Scout.Core;

public static partial class QueryParser
{
    private static readonly HashSet<string> Filler = new("the a an i my me please find show open launch run all every of in on at from that which just last latest newest oldest first recent recently ago downloaded download added modified edited changed opened used visited read created made authored saved was have has had been did worked with about for called named file files app apps application applications page pages link links website websites pdf pdfs document documents deck decks presentation presentations slide slides spreadsheet spreadsheets image images photo photos picture pictures video videos audio music archive archives today yesterday this morning afternoon evening tonight week month year past hour hours minute minutes workspace workspaces mode preview reveal locate copy path folder explorer to it again can you".Split(' '));
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)] private static partial Regex Words();
    [GeneratedRegex(@"\b(?:past|last)\s+(?:(\d+|one|two|three|four|five|six|seven|eight|nine|ten|twelve|twenty four|half an|an?|couple of)\s+)?(minute|hour|day|week)s?\b", RegexOptions.IgnoreCase)] private static partial Regex Relative();
    public static string[] Tokenize(string text) => Words().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();

    public static QueryIntent Parse(string text, DateTimeOffset now, TimeZoneInfo? zone = null, Scope scope = Scope.All)
    {
        zone ??= TimeZoneInfo.Local;
        var advanced = AdvancedQuery.Parse(text, now);
        var q = advanced.Text.Trim().ToLowerInvariant();
        var actionText = q;
        var explorer = q.Contains("in explorer", StringComparison.Ordinal);
        var location = FolderQuery.Extract(explorer ? q.Replace("in explorer", "") : q);
        q = NormalizeRequest(location.Text);
        int? count = null;
        const string numberPattern = @"(?<number>\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|twenty)";
        const string ordering = @"(?:last|latest|newest|oldest|first|top|most recent)";
        var counted = Regex.Match(q, @"\b(?:" + ordering + @"\s+" + numberPattern + "|" + numberPattern + @"\s+" + ordering + @")\s+(?:files|documents|pdfs|photos|images|screenshots|presentations|spreadsheets|archives|videos|links|pages)\b");
        if (counted.Success)
        {
            var number = counted.Groups["number"].Value;
            count = number switch { "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10, "eleven" => 11, "twelve" => 12, "twenty" => 20, _ => int.Parse(number, CultureInfo.InvariantCulture) };
            count = Math.Clamp(count.Value, 1, 25);
            q = q.Remove(counted.Groups["number"].Index, counted.Groups["number"].Length);
        }
        var stripped = q;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = local.Date;
        DateTimeOffset At(DateTime d) => new(d, zone.GetUtcOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified)));
        TimeWindow? window = null;
        var relative = Relative().Match(q);
        if (relative.Success && relative.Value != "last week")
        {
            var number = relative.Groups[1].Value;
            double n = number switch { "two" or "couple of" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10, "twelve" => 12, "twenty four" => 24, "half an" => .5, _ => double.TryParse(number, out var parsed) ? Math.Clamp(parsed, 1, 3650) : 1 };
            var unit = relative.Groups[2].Value;
            var duration = TimeSpan.FromMinutes(n * (unit == "minute" ? 1 : unit == "hour" ? 60 : unit == "day" ? 1440 : 10080));
            window = new(now - duration, now, relative.Value);
            stripped = stripped.Replace(relative.Value, " ");
        }
        else
        {
            // Calendar windows, in the user's timezone rather than UTC-day arithmetic.
            var phrases = new[] { "a few days ago", "day before yesterday", "earlier today", "this morning", "this afternoon", "this evening", "last night", "last month", "this month", "last week", "this week", "last year", "this year", "yesterday", "today" };
            foreach (var phrase in phrases)
            {
                if (!Regex.IsMatch(q, @"\b" + Regex.Escape(phrase) + @"\b")) continue;
                var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                var month = new DateTime(today.Year, today.Month, 1);
                (DateTimeOffset since, DateTimeOffset until) = phrase switch
                {
                    "yesterday" => (At(today.AddDays(-1)), At(today)),
                    "this morning" => (At(today), local.Hour < 12 ? now : At(today.AddHours(12))),
                    "day before yesterday" => (At(today.AddDays(-2)), At(today.AddDays(-1))),
                    "this afternoon" => (At(today.AddHours(12)), local.Hour < 18 ? now : At(today.AddHours(18))),
                    "this evening" => (At(today.AddHours(18)), now),
                    "last night" => (At(today.AddDays(-1).AddHours(18)), At(today)),
                    "last week" => (At(week.AddDays(-7)), At(week)),
                    "this week" => (At(week), now),
                    "last month" => (At(month.AddMonths(-1)), At(month)),
                    "this month" => (At(month), now),
                    "last year" => (At(new DateTime(today.Year - 1, 1, 1)), At(new DateTime(today.Year, 1, 1))),
                    "this year" => (At(new DateTime(today.Year, 1, 1)), now),
                    "a few days ago" => (At(today.AddDays(-3)), At(today)),
                    _ => (At(today), now)
                };
                window = new(since, until, phrase);
                stripped = stripped.Replace(phrase, " ");
                break;
            }
        }
        var words = Tokenize(q).ToHashSet();
        if (words.Contains("arrived")) { words.Add("added"); stripped = Regex.Replace(stripped, @"\barrived\b", ""); }
        bool Has(params string[] values) => values.Any(words.Contains);
        var appRole = scope == Scope.All && location.Folder == null && !Has("file", "files", "created", "modified", "downloaded", "opened") && Regex.IsMatch(q, @"\b(?:text|pdf|json|xml|image|photo|video|audio|music|web)\s+(?:editor|viewer|player|browser)\b");
        var fileDescription = Regex.IsMatch(q, @"^(?:(?:open|preview|reveal|copy)\s+)?(?:(?:the|my|a|an)\s+)?(?:pdfs?|documents?|files?|markdown|json|slides?|presentations?|spreadsheets?)\s+(?:about|with|for|that|which|showing|explaining|describing|teaching|containing|listing|recording|defining)\b");
        var appIntent = scope == Scope.Apps || !fileDescription && (Has("app", "apps", "application", "applications") || appRole) && location.Folder == null;
        bool DescribedOfficeFile(string name) => Has(name) && (scope == Scope.Files || Has("file", "files", "document", "documents", "created", "made", "opened", "used", "edited", "modified", "downloaded") || words.Any(w => !Filler.Contains(w) && w != name && w != "microsoft"));
        string? extension = appIntent ? null : Has("pdf", "pdfs") ? ".pdf" : Has("deck", "decks", "slide", "slides", "presentation", "presentations") || DescribedOfficeFile("powerpoint") ? ".pptx" : Has("spreadsheet", "spreadsheets") || DescribedOfficeFile("excel") ? ".xlsx" : DescribedOfficeFile("word") ? ".docx" : Has("image", "images", "photo", "photos", "picture", "pictures", "screenshot", "screenshots") ? "image" : Has("video", "videos", "movie", "movies", "recording", "recordings") ? "video" : Has("music", "audio", "song", "songs") ? "audio" : Has("archive", "archives", "zip") ? "archive" : null;
        // In a document description, "recording what was discussed" is a verb,
        // and "about video" is a topic. Neither requests a media file.
        if (Regex.IsMatch(q, @"^(?:(?:open|find|show|preview|reveal)\s+)?(?:(?:the|a|my)\s+)?documents?\b") && extension is "image" or "video" or "audio" or "archive") extension = null;
        var typed = appIntent ? Match.Empty : Regex.Match(q, @"(?<![\p{L}\p{N}])\.?(pdf|pptx?|pptm|ppsx|xlsx?|xlsm|csv|tsv|docx?|docm|odt|ods|odp|rtf|txt|md|json|ya?ml|xml|html|png|jpe?g|gif|webp|svg|bmp|tiff?|heic|mp4|mkv|mov|avi|webm|mp3|wav|m4a|flac|zip|7z|rar|tar|gz)\b");
        if (!appIntent && typed.Success) extension = "." + typed.Groups[1].Value;
        // An explicit Markdown format outranks "recording" used as a verb in a
        // description, just as an explicit file extension outranks media words.
        if (!appIntent && Has("markdown", "md") && !typed.Success) extension = ".md";
        if (!appIntent && extension == null && Has("text", "txt")) extension = ".txt";
        ItemKind? kind = Has("link", "links", "page", "pages", "visited", "website", "websites") ? ItemKind.Link : appIntent ? ItemKind.App : extension != null || Has("file", "files", "document", "documents", "downloaded", "modified", "edited", "created", "authored") ? ItemKind.File : Has("workspace", "workspaces") ? ItemKind.Workspace : null;
        if (location.Folder != null && !appIntent) kind = q.Length == 0 ? ItemKind.Folder : ItemKind.File;
        if (scope != Scope.All) kind = scope switch { Scope.Files => kind == ItemKind.Folder ? ItemKind.Folder : ItemKind.File, Scope.Apps => ItemKind.App, Scope.Links => ItemKind.Link, Scope.Workspaces => ItemKind.Workspace, _ => kind };
        var madeFile = Has("made") && (!fileDescription || Regex.IsMatch(q, @"\b(?:files?|documents?|pdfs?|markdown|presentations?|slides?|spreadsheets?)\s+(?:(?:that|which)\s+)?(?:(?:i|we)\s+)?made\b"));
        var basis = Has("created", "authored") || madeFile ? RecencyBasis.Created : Has("modified", "edited", "changed", "saved") ? RecencyBasis.Modified : Has("downloaded", "download") ? RecencyBasis.Downloaded : Has("added") ? RecencyBasis.Added : Has("visited", "read") && kind == ItemKind.Link ? RecencyBasis.Visited : Has("opened", "used", "worked", "working") || Has("read") && kind == ItemKind.File ? RecencyBasis.Opened : kind == ItemKind.Link && window != null ? RecencyBasis.Visited : RecencyBasis.None;
        var latest = Has("last", "latest", "newest", "recent", "recently", "just");
        if (basis == RecencyBasis.None && (window != null || latest || Has("oldest", "first"))) basis = kind == ItemKind.Link ? RecencyBasis.Visited : RecencyBasis.Modified;
        var typeWords = extension == "image" ? new[] { "screenshot", "screenshots" } : extension == "video" ? ["movie", "movies", "recording", "recordings"] : extension == "audio" ? ["song", "songs"] : extension == "archive" ? ["zip"] : Array.Empty<string>();
        var terms = Tokenize(stripped).Where(w => w is not ("most" or "working" or "top") && (!Filler.Contains(w) || appIntent && w is "pdf" or "file" or "image" or "images") && !(extension == ".pptx" && w == "powerpoint") && !(extension == ".xlsx" && w == "excel") && !(extension == ".docx" && w == "word") && !(extension == ".md" && w is "markdown" or "md") && !(extension == ".txt" && w is "text" or "txt") && !typeWords.Contains(w) && !(typed.Success && w == typed.Groups[1].Value)).ToArray();
        // Command names such as "show hidden files" and "copy" must retain their discriminating words.
        if (SystemCommands.LooksLikeCommand(q)) { terms = Tokenize(q).Where(w => w is not ("the" or "please" or "can" or "you")).ToArray(); kind = scope == Scope.All ? ItemKind.System : kind; basis = RecencyBasis.None; }
        if (advanced.Pattern != null) kind = ItemKind.File;
        return new(text.Trim(), terms, extension, kind, basis, window, count > 1 || count == null && Has("all", "every", "files", "links", "pages", "apps", "documents", "pdfs", "presentations", "decks", "spreadsheets", "photos", "images", "videos") && !Has("last", "latest", "newest", "oldest", "first"))
        {
            ContentPattern = advanced.Pattern, ContentRegex = advanced.Regex, ExplicitExtensions = advanced.Extensions ?? [],
            MinimumSize = advanced.MinimumSize, MaximumSize = advanced.MaximumSize, ModifiedWindow = advanced.ModifiedWindow, QueryError = advanced.Error ?? "",
            Oldest = Has("oldest", "first"), Latest = latest,
            Folder = advanced.Folder != null ? new FolderFilter(advanced.Folder) : location.Folder, Count = count, Screenshots = extension == "image" && Has("screenshot", "screenshots"),
            Action = Regex.IsMatch(actionText, @"^(?:please\s+)?preview\b") ? ItemAction.Preview : Regex.IsMatch(actionText, @"^(?:please\s+)?(?:reveal|locate)\b") || explorer ? ItemAction.Reveal : Regex.IsMatch(actionText, @"^(?:please\s+)?copy\b") ? ItemAction.Copy : ItemAction.Open
        };
    }

    // Remove request grammar at clause boundaries, without discarding useful
    // filename words everywhere (for example a file actually named "Give").
    internal static string NormalizeRequest(string text)
    {
        var q = text.Trim().Trim(',', '?', '!');
        q = Regex.Replace(q, @"^(?:(?:please|can you|could you|would you)\s+)*(?:(?:give|show|get)\s+me\s+|(?:what|which)\s+(?:(?:is|was|are|were)\s+)?|(?:find|search for|look for)\s+)", "", RegexOptions.IgnoreCase);
        return q.Trim();
    }
}
