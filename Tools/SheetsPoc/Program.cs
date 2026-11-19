// Proof of concept for #82: every account's AltStableDB into a Google Sheet.
//
//   dotnet run --project Tools/SheetsPoc -- --accounts <WTF\Account or a copy> --data-dir <dir>
//       [--client <Desktop app client JSON>] [--dry-run] [--new]
//
// --dry-run prints the tabs and touches no network. The sign-in and the sheet's id are kept in
// --data-dir (google-token.bin with DPAPI, google-sheet.txt); --new makes a fresh sheet. The
// app's tabs are found by the tag on them, so nothing else is kept here.
using System.Diagnostics;
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
if (!roster.Complete)
{
    // Writing now would drop that account's rows from the sheet - or every row, for a wrong path.
    if (roster.NoAccounts) Console.Error.WriteLine($"no account using AltStable under {accounts}; nothing written");
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

var idFile = Path.Combine(dataDir!, "google-sheet.txt");
var id = !fresh && File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
if (string.IsNullOrEmpty(id))
{
    (id, _) = await google.CreateAsync(access, "AltStable", tables.Select(t => t.Title), cts.Token);
    File.WriteAllText(idFile, id);
    Console.WriteLine("Created a new spreadsheet.");
}
var watch = Stopwatch.StartNew();
var added = await google.WriteAsync(access, id, tables, cts.Token);
if (added.Count > 0) Console.WriteLine($"Added tabs: {string.Join(", ", added)}");
Console.WriteLine($"Written in {watch.ElapsedMilliseconds} ms: https://docs.google.com/spreadsheets/d/{id}/edit");
return 0;
