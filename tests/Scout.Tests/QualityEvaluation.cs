using System.Text.Json;
using Scout.Core;

internal static class QualityEvaluation
{
    internal sealed record Case(string Id, string Query, string[] Expected, bool Ambiguous = false);
    public static int Export(string path, bool heldout = false)
    {
        var now = DateTimeOffset.Now;
        Candidate File(string name, int created, int modified, int? opened = null, int? downloaded = null) => new(name, ItemKind.File, name, @"C:\Documents\" + name)
        { Detail = "Documents", Source = "Files", CreatedAt = now.AddHours(-created), ModifiedAt = now.AddHours(-modified), OpenedAt = opened.HasValue ? now.AddHours(-opened.Value) : null, DownloadedAt = downloaded.HasValue ? now.AddHours(-downloaded.Value) : null, DownloadSource = "Chrome completed download" };
        Candidate App(string name, params string[] aliases) => new(name, ItemKind.App, name, @"C:\Apps\" + name + ".lnk") { Keywords = aliases, Source = "Windows" };
        var files = new[] {
            File("Quarterly operating review.pptx", 28, 2, 3), File("Board update.pptx", 2, 2, 1), File("Roadmap.pptx", 120, 1),
            File("Company strategy.pptx", 18, 18), File("Client onboarding.pptx", 36, 36), File("Sales forecast.xlsx", 50, 3),
            File("Household budget.xlsx", 20, 4), File("Expense reimbursement.xlsx", 72, 8), File("Design system.docx", 48, 12, 1),
            File("Curriculum vitae.pdf", 200, 200, 5), File("Customer billing.pdf", 24, 5, 1, 24), File("Product research notes.md", 100, 20, 4),
            File("Team meeting minutes.docx", 10, 10, 2), File("Employment agreement.pdf", 240, 240, null, 72),
            File("Vacation.jpg", 18, 18), File("Screenshot app.png", 1, 1), File("Conference recording.mp4", 40, 40),
            File("Revenue report.pdf", 6, 6, 2, 1), File("Network diagram.png", 70, 40), File("Tax return.pdf", 200, 100),
            File("Project proposal.pdf", 100, 2, 3, 48), File("API Reference.pdf", 600, 500), File("Engineering handbook.pdf", 300, 300)
        };
        var apps = new[] { App("Google Chrome", "chrome", "web browser", "browse internet"), App("Microsoft Edge", "edge", "web browser"), App("Visual Studio Code", "code", "vscode", "code editor", "write code"), App("Notepad", "text editor", "write notes"), App("Excel", "spreadsheets"), App("PowerPoint", "presentations"), App("Word", "word processor"), App("Spotify", "music"), App("Slack", "team chat"), App("Calculator", "calc", "math"), App("Terminal", "shell", "command line"), App("Outlook", "email") };
        var links = new[] { new Candidate("typesafe", ItemKind.Link, "System One — TypeSafe Docs", "https://typesafe.ai/docs") { Keywords = ["typesafe"], VisitedAt = now.AddMinutes(-5), Source = "Chrome" }, new Candidate("devin", ItemKind.Link, "Devin Ambassador Program", "https://devin.ai/ambassadors") { Keywords = ["devin"], VisitedAt = now.AddMinutes(-10), Source = "Chrome" }, new Candidate("old-devin", ItemKind.Link, "Devin Ambassador announcement", "https://devin.ai/announcement") { Keywords = ["devin"], VisitedAt = now.AddDays(-4), Source = "Chrome" } };
        var candidates = files.Concat(apps).Concat(links).Concat(SystemCommands.Items).ToArray();
        var catalog = new SearchCatalog(candidates); var memory = new Dictionary<string, LibraryItem>();
        var cases = new List<Case>();
        void Add(string query, params string[] expected) => cases.Add(new("q" + cases.Count, query, expected));
        foreach (var app in apps) Add(app.Title, app.Id);
        Add("vscode", "Visual Studio Code"); Add("notepd", "Notepad"); Add("chro", "Google Chrome");
        Add("the last presentation I created", "Board update.pptx");
        Add("the last presentation I modified", "Roadmap.pptx");
        Add("the last presentation I opened", "Board update.pptx");
        Add("the last pdf I downloaded", "Revenue report.pdf");
        Add("the oldest presentation I created", "Roadmap.pptx");
        Add("presentations I created in the past three hours", "Board update.pptx");
        Add("the latest photos I created", "Screenshot app.png");
        Add("the last file I opened", "Board update.pptx", "Customer billing.pdf", "Design system.docx");
        Add("files I opened in the past three hours", "Board update.pptx", "Customer billing.pdf", "Design system.docx", "Team meeting minutes.docx", "Revenue report.pdf", "Quarterly operating review.pptx", "Project proposal.pdf");
        Add("the typesafe page I read in the last hour", "typesafe");
        Add("devin links I visited in the last hour", "devin");
        Add("client invoices", "Customer billing.pdf");
        Add("my resume pdf", "Curriculum vitae.pdf");
        Add("household expenses spreadsheet", "Household budget.xlsx");
        Add("meeting notes document", "Team meeting minutes.docx");
        Add("open the code editor", "Visual Studio Code");
        Add("write notes app", "Notepad");
        Add("wifi off", "system:wifi-off"); Add("wifi on", "system:wifi-on"); Add("show hidden files", "system:hidden-show"); Add("hide hidden files", "system:hidden-hide");
        Add("empty trash", "system:recycle"); Add("dark", "system:appearance"); Add("lock screen", "system:lock"); Add("do not disturb", "system:focus");
        // Semantic challenges use descriptions absent from the literal filename.
        Add("presentation for the leadership team", "Board update.pptx");
        Add("slides about the direction of our company", "Company strategy.pptx");
        Add("slides for welcoming a new client", "Client onboarding.pptx");
        Add("pdf describing my professional background", "Curriculum vitae.pdf");
        Add("pdf with the terms of my job", "Employment agreement.pdf");
        Add("spreadsheet for getting work costs paid back", "Expense reimbursement.xlsx");
        Add("spreadsheet predicting future sales", "Sales forecast.xlsx");
        Add("the document with notes from the team discussion", "Team meeting minutes.docx");
        Add("pdf instructions for using the programming interface", "API Reference.pdf");
        Add("image showing how our network connects", "Network diagram.png");
        Add("pdf of money coming into the business", "Revenue report.pdf");
        Add("pdf explaining our proposed project", "Project proposal.pdf");
        Add("my passport pdf"); Add("blueprints for a moon colony pdf"); Add("zzzxqv nonexistent"); Add("presentations I opened in the last minute");
        cases.Add(new("q" + cases.Count, "wifi", [], true));
        cases.Add(new("q" + cases.Count, "web browser", [], true));
        if (heldout)
        {
            var names = new[] { "Flight itinerary.pdf", "Apartment lease.pdf", "Insurance policy.pdf", "Diploma.pdf", "Purchase receipt.pdf", "Annual financial statement.pdf", "Wedding invitation.pdf", "Food menu.pdf", "Backup procedures.pdf", "Product launch.pptx", "Security awareness.pptx", "Sprint retrospective.pptx", "Interview preparation.docx", "Customer retention.xlsx", "Inventory.xlsx", "Travel expenses.xlsx", "Course timetable.xlsx", "Garden.jpg", "Mountain sunset.png", "Training session.mp4" };
            catalog = new SearchCatalog(names.Select((name, i) => File(name, i + 1, names.Length - i, i % 3 == 0 ? i + 1 : null)).Concat(apps).Concat(SystemCommands.Items));
            cases.Clear();
            Add("pdf with my flight schedule", "Flight itinerary.pdf");
            Add("pdf of the agreement to rent my apartment", "Apartment lease.pdf");
            Add("pdf describing what my insurance covers", "Insurance policy.pdf");
            Add("pdf proof that I finished university", "Diploma.pdf");
            Add("pdf showing what I paid for a purchase", "Purchase receipt.pdf");
            Add("pdf summarizing the company's finances for the year", "Annual financial statement.pdf");
            Add("pdf inviting people to our wedding", "Wedding invitation.pdf");
            Add("pdf listing dishes at the restaurant", "Food menu.pdf");
            Add("pdf explaining how to back up our data", "Backup procedures.pdf");
            Add("slides announcing a new product", "Product launch.pptx");
            Add("slides teaching staff about security threats", "Security awareness.pptx");
            Add("presentation reviewing how the sprint went", "Sprint retrospective.pptx");
            Add("document to help get ready for an interview", "Interview preparation.docx");
            Add("spreadsheet showing which customers stay with us", "Customer retention.xlsx");
            Add("spreadsheet showing stock levels", "Inventory.xlsx");
            Add("spreadsheet tracking money spent on trips", "Travel expenses.xlsx");
            Add("spreadsheet with class times", "Course timetable.xlsx");
            Add("photo of the garden", "Garden.jpg");
            Add("picture of the sun going down in the mountains", "Mountain sunset.png");
            Add("video of the training", "Training session.mp4");
            Add("the last pdf I created", "Flight itinerary.pdf");
            Add("the oldest pdf I created", "Backup procedures.pdf");
            Add("the last pdf I opened", "Flight itinerary.pdf");
            Add("the last presentation I modified", "Sprint retrospective.pptx");
            Add("the last spreadsheet I created", "Customer retention.xlsx");
            Add("open the text editor", "Notepad");
            Add("open the command line", "Terminal");
            Add("chrome", "Google Chrome");
            Add("ppt app", "PowerPoint");
            Add("enable wifi", "system:wifi-on");
            Add("disable wifi", "system:wifi-off");
            Add("reveal hidden files", "system:hidden-show");
            Add("notifications", "system:focus");
            Add("pdf containing my medical prescription");
            Add("pdf with rocket engine blueprints");
            Add("presentation about ocean currents");
            Add("spreadsheet of football scores");
            Add("photos of my cat");
            Add("pdf I downloaded in the last minute");
            Add("files I created in the past five minutes");
            cases.Add(new("q" + cases.Count, "wifi", [], true));
            cases.Add(new("q" + cases.Count, "browser", [], true));
        }
        var rows = cases.Select(c =>
        {
            var result = SearchEngine.Search(c.Query, Scope.All, catalog, memory, now);
            return new { id = c.Id, query = c.Query, expected = c.Expected, ambiguous = c.Ambiguous, plural = result.Intent.Plural, local_top = result.Hits.FirstOrDefault()?.Candidate.Id, local_ready = RankingPolicy.Ready(result, result.Hits), search = result,
                candidates = result.ModelShortlist.Select(h => new { id = h.Candidate.Id, title = h.Candidate.Title, kind = h.Candidate.Kind.ToString().ToLowerInvariant(), detail = h.Candidate.Detail, basis = result.Intent.Recency.ToString().ToLowerInvariant(), recency = h.Candidate.Date(result.Intent.Recency)?.ToUnixTimeSeconds(), evidence = h.Candidate.Evidence(result.Intent.Recency) }) };
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new { generated = now, cases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Exported {cases.Count} quality cases"); return 0;
    }
    public static int Assess(string fixtures, string predictions, string output)
    {
        using var dataset = JsonDocument.Parse(System.IO.File.ReadAllText(fixtures));
        using var answers = JsonDocument.Parse(System.IO.File.ReadAllText(predictions));
        var reports = new List<object>();
        foreach (var checkpoint in answers.RootElement.GetProperty("models").EnumerateObject())
        {
            var checks = new List<object>(); int localCorrect = 0, hybridCorrect = 0, count = 0, ambiguousCorrect = 0, ambiguousCount = 0;
            foreach (var row in dataset.RootElement.GetProperty("cases").EnumerateArray())
            {
                var result = row.GetProperty("search").Deserialize<SearchResult>()!;
                var response = checkpoint.Value.GetProperty("answers").GetProperty(row.GetProperty("id").GetString()!);
                var judgment = LayaRanker.Parse(response, result.ModelShortlist.Select(h => h.Candidate).ToArray());
                var effective = result.Hits.FirstOrDefault()?.Score >= 1000 ? result.Hits : RankingPolicy.Refine(result, judgment);
                var expected = row.GetProperty("expected").Deserialize<string[]>()!;
                var local = result.Hits.FirstOrDefault()?.Candidate.Id; var hybrid = effective.FirstOrDefault()?.Candidate.Id;
                var ambiguous = row.GetProperty("ambiguous").GetBoolean(); var ready = RankingPolicy.Ready(result, effective, judgment);
                bool Correct(string? id) => expected.Length == 0 ? id == null : id != null && expected.Contains(id);
                if (ambiguous) { ambiguousCount++; if (!ready) ambiguousCorrect++; }
                else { count++; if (Correct(local)) localCorrect++; if (Correct(hybrid)) hybridCorrect++; }
                checks.Add(new { query = result.Query, expected, local, hybrid, correct = ambiguous ? !ready : Correct(hybrid), ambiguous, ready, modelMs = judgment.ElapsedMs });
            }
            reports.Add(new { model = checkpoint.Name, count, localCorrect, hybridCorrect, ambiguousCorrect, ambiguousCount, metrics = checkpoint.Value.GetProperty("metrics").Clone(), checks });
            Console.WriteLine($"{checkpoint.Name}: local {localCorrect}/{count}, hybrid {hybridCorrect}/{count}, ambiguity {ambiguousCorrect}/{ambiguousCount}");
        }
        System.IO.File.WriteAllText(output, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
}
