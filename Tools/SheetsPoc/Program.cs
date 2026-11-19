// Proof of concept for #82: every account's AltStableDB into a Google Sheet.
//
//   dotnet run --project Tools/SheetsPoc -- --accounts <WTF\Account or a copy> --data-dir <dir>
//       [--client <Desktop app client JSON>] [--dry-run] [--new]
//
// --dry-run prints the tabs and touches no network. The sign-in and the sheet are kept in
// --data-dir (google-token.bin with DPAPI; google-sheet.json, the sheet's id and its tabs'
// ids); --new makes a fresh sheet.
using System.Diagnostics;
using System.Text.Json;
using AltStableCompanion.Core;

string? accounts = null, dataDir = null, clientPath = null;
bool dryRun = false, fresh = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--accounts": accounts = args[++i]; break;
        case "--data-dir": dataDir = args[++i]; break;
        case "--client": clientPath = args[++i]; break;
        case "--dry-run": dryRun = true; break;
        case "--new": fresh = true; break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}
if (accounts is null || (!dryRun && dataDir is null))
{
    Console.Error.WriteLine("--accounts is required, and --data-dir unless --dry-run");
    return 2;
}

var roster = SheetExport.Load(accounts);
if (roster.Skipped.Count > 0)
{
    // Writing now would drop that account's rows from the sheet.
    foreach (var s in roster.Skipped) Console.Error.WriteLine($"could not read {s}; nothing written");
    return 1;
}
var tables = SheetExport.Build(roster.Characters, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
Console.WriteLine($"{roster.Characters.Count} characters");

if (dryRun)
{
    foreach (var t in tables)
    {
        Console.WriteLine($"== {t.Title}");
        foreach (var row in t.Rows) Console.WriteLine(string.Join(" | ", row.Select(c => c.Value?.ToString() ?? "")));
    }
    return 0;
}

clientPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".secrets", "google-oauth-client.json");
var google = new GoogleSheets(new HttpClient(), GoogleClient.FromJson(File.ReadAllText(clientPath)));
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

var refresh = GoogleTokenStore.Load(dataDir!);
if (refresh is null)
{
    Console.WriteLine("Opening the browser to sign in to Google...");
    refresh = await google.SignInAsync(uri => Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }), cts.Token);
    GoogleTokenStore.Save(dataDir!, refresh);
    Console.WriteLine("Signed in.");
}
var access = await google.AccessTokenAsync(refresh, cts.Token);

var sheetFile = Path.Combine(dataDir!, "google-sheet.json");
var sheet = !fresh && File.Exists(sheetFile) ? JsonSerializer.Deserialize<Sheet>(File.ReadAllText(sheetFile)) : null;
if (sheet is null)
{
    var (newId, _, tabs) = await google.CreateAsync(access, "AltStable", tables.Select(t => t.Title), cts.Token);
    sheet = new Sheet(newId, new Dictionary<string, long>(tabs));
    File.WriteAllText(sheetFile, JsonSerializer.Serialize(sheet));
    Console.WriteLine("Created a new spreadsheet.");
}
var watch = Stopwatch.StartNew();
var owned = await google.WriteAsync(access, sheet.Id, tables, sheet.Tabs, cts.Token);
File.WriteAllText(sheetFile, JsonSerializer.Serialize(sheet with { Tabs = new Dictionary<string, long>(owned) }));
Console.WriteLine($"Written in {watch.ElapsedMilliseconds} ms: https://docs.google.com/spreadsheets/d/{sheet.Id}/edit");
return 0;

internal sealed record Sheet(string Id, Dictionary<string, long> Tabs);
