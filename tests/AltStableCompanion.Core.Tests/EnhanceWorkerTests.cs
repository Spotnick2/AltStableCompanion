using AltStableCompanion.Core;

namespace AltStableCompanion.Core.Tests;

/// <summary>The runner against a shim that plays the Codex CLI, never the real one.</summary>
public class CodexImageGenTests
{
    private static readonly string Pwsh = OperatingSystem.IsWindows() ? "pwsh" : "pwsh";

    // A codex.cmd on disk that runs a PowerShell script; the script reads what to do from a
    // file beside it, so one shim plays every part. It records the arguments and the prompt.
    private static (string Exe, string Home, string Control, string Seen) Shim(TempInstall t)
    {
        var dir = Path.Combine(t.Root, "codex shim");
        Directory.CreateDirectory(dir);
        var home = Path.Combine(t.Root, "codex home");
        Directory.CreateDirectory(Path.Combine(home, "generated_images"));
        var control = Path.Combine(dir, "behaviour.txt");
        var seen = Path.Combine(dir, "seen.txt");
        var script = Path.Combine(dir, "shim.ps1");
        File.WriteAllText(script, """
            $ErrorActionPreference = 'Stop'
            $Args = @($args)
            $here = Split-Path -Parent $MyInvocation.MyCommand.Path
            $behaviour = (Get-Content -LiteralPath (Join-Path $here 'behaviour.txt') -Raw).Trim()
            if ($Args -contains 'login') { if ($behaviour -eq 'fail') { exit 3 }; Write-Output 'Logged in using ChatGPT'; exit 0 }
            if ($behaviour -eq 'earlyexit') { [Console]::Error.WriteLine('left early'); exit 3 }
            [Console]::InputEncoding = [Text.Encoding]::UTF8
            $prompt = [Console]::In.ReadToEnd()
            $verdict = $null; $jobDir = $null
            for ($i = 0; $i -lt $Args.Count; $i++) {
                if ($Args[$i] -eq '-o') { $verdict = $Args[$i + 1] }
                if ($Args[$i] -eq '-C') { $jobDir = $Args[$i + 1] }
            }
            Set-Content -LiteralPath (Join-Path $here 'seen.txt') -Value (($Args -join "`n") + "`n--prompt--`n" + $prompt) -NoNewline
            $png = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,1,2,3,4)
            $images = Join-Path $env:CODEX_HOME 'generated_images'
            function Put($name, $bytes) {
                $folder = Join-Path $images ([guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $folder | Out-Null
                $p = Join-Path $folder $name
                [IO.File]::WriteAllBytes($p, $bytes)
                return $p
            }
            switch ($behaviour) {
                'one'       { $p = Put 'a.png' $png; Set-Content -LiteralPath $verdict -Value "done`nARTIFACT_PATH: $p" }
                'zero'      { Set-Content -LiteralPath $verdict -Value "nothing`nARTIFACT_PATH: C:\nowhere\x.png" }
                'two'       { $p = Put 'a.png' $png; $q = Put 'b.png' $png; Set-Content -LiteralPath $verdict -Value "ARTIFACT_PATH: $p" }
                'wrongpath' { $p = Put 'a.png' $png; Set-Content -LiteralPath $verdict -Value "ARTIFACT_PATH: $($p + '.other.png')" }
                'noreport'  { $p = Put 'a.png' $png; Set-Content -LiteralPath $verdict -Value "done, no path" }
                'badmagic'  { $p = Put 'a.png' ([byte[]](1,2,3,4,5,6,7,8,9)); Set-Content -LiteralPath $verdict -Value "ARTIFACT_PATH: $p" }
                'huge'      { $big = New-Object byte[] (33 * 1024 * 1024); [Array]::Copy($png, $big, 8); $p = Put 'a.png' $big; Set-Content -LiteralPath $verdict -Value "ARTIFACT_PATH: $p" }
                'hang'      { Start-Sleep -Seconds 60 }
                'fail'      { [Console]::Error.WriteLine('no auth'); exit 3 }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "codex.cmd"), $"@echo off\r\n\"{Pwsh}\" -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -- %*\r\n");
        return (Path.Combine(dir, "codex.cmd"), home, control, seen);
    }

    private static CodexJob Job(TempInstall t, TimeSpan? timeout = null)
    {
        var reference = Path.Combine(t.Root, "reference.png");
        PngCodec.Write(reference, new RgbaImage(2, 2));
        return new CodexJob("Draw a troll with two toes - élan, Ünterwelt.", reference, "gpt-6-astra", "low", timeout ?? TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task The_invocation_is_the_measured_one_and_one_picture_comes_back()
    {
        using var t = new TempInstall();
        var (exe, home, control, seen) = Shim(t);
        File.WriteAllText(control, "one");
        var gen = new CodexImageGen(exe, home);
        // A folder shaped like an environment variable, with a space: it must arrive as it is.
        var jobDir = Path.Combine(t.Root, "job %OS% %PATH% dir");
        Directory.CreateDirectory(jobDir);
        var result = await gen.GenerateAsync(Job(t), jobDir, CancellationToken.None);
        Assert.True(result.Ok, result.Failure);
        Assert.Equal(0x89, result.Png![0]);
        var args = File.ReadAllText(seen);
        foreach (var flag in new[] { "exec", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check",
                     "--sandbox\nworkspace-write", "-m\ngpt-6-astra", "model_reasoning_effort=low", "web_search=disabled",
                     "project_doc_max_bytes=0", "-C\n" + jobDir, "-i\n" + Path.Combine(t.Root, "reference.png"), "-o\n" + Path.Combine(jobDir, "verdict.md") })
        {
            Assert.Contains(flag, args);
        }
        // The prompt arrives whole, in UTF-8 - and through a folder with a space in its name.
        Assert.EndsWith("--prompt--\nDraw a troll with two toes - élan, Ünterwelt.", args.Replace("\r\n", "\n"));
        Assert.Contains(' ', Path.GetDirectoryName(exe)!);
        Assert.Equal("Logged in using ChatGPT", await CodexImageGen.LoginStatusAsync(exe, CancellationToken.None));
        File.WriteAllText(control, "fail");
        Assert.Null(await CodexImageGen.LoginStatusAsync(exe, CancellationToken.None));
        // Codex's own folder is left as it was found, picture included.
        Assert.Single(Directory.GetFiles(Path.Combine(home, "generated_images"), "*.png", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("zero", "no new picture")]
    [InlineData("two", "2 new pictures")]
    [InlineData("wrongpath", "not the one that appeared")]
    [InlineData("noreport", "names no picture")]
    [InlineData("badmagic", "not a PNG")]
    [InlineData("huge", "the most this app reads is 32 MB")]
    [InlineData("fail", "exited with 3")]
    [InlineData("earlyexit", "exited with 3")]
    public async Task Anything_but_exactly_the_reported_picture_fails_without_guessing(string behaviour, string reason)
    {
        using var t = new TempInstall();
        var (exe, home, control, _) = Shim(t);
        File.WriteAllText(control, behaviour);
        var gen = new CodexImageGen(exe, home);
        var jobDir = Path.Combine(t.Root, "job");
        Directory.CreateDirectory(jobDir);
        // "earlyexit" leaves before reading stdin, with a prompt larger than the pipe: the
        // broken pipe is not the story, the exit code and stderr are.
        var job = behaviour == "earlyexit" ? Job(t) with { Prompt = new string('x', 400 * 1024) } : Job(t);
        var result = await gen.GenerateAsync(job, jobDir, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains(reason, result.Failure);
        if (behaviour == "fail") Assert.Contains("no auth", result.Detail);
        if (behaviour == "earlyexit") Assert.Contains("left early", result.Detail);
    }

    [Fact]
    public async Task A_timeout_and_a_cancellation_kill_the_child_and_say_which()
    {
        using var t = new TempInstall();
        var (exe, home, control, _) = Shim(t);
        File.WriteAllText(control, "hang");
        var gen = new CodexImageGen(exe, home);
        var jobDir = Path.Combine(t.Root, "job");
        Directory.CreateDirectory(jobDir);
        var timedOut = await gen.GenerateAsync(Job(t, TimeSpan.FromSeconds(3)), jobDir, CancellationToken.None);
        Assert.Contains("timed out", timedOut.Failure);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var cancelled = await gen.GenerateAsync(Job(t), jobDir, cts.Token);
        Assert.Equal("cancelled", cancelled.Failure);
    }

    [Fact]
    public void The_arguments_refuse_a_model_or_effort_that_is_not_a_token_and_the_shim_is_found_on_PATH()
    {
        var job = new CodexJob("p", "r.png", "gpt-6-astra", "low", TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentException>(() => CodexImageGen.Arguments(job with { Model = "bad model" }, "d", "v"));
        Assert.Throws<ArgumentException>(() => CodexImageGen.Arguments(job with { Effort = "low&del" }, "d", "v"));
        Assert.Equal(["exec", "--ignore-user-config"], CodexImageGen.Arguments(job, "d", "v").Take(2));
        // cmd.exe's line: raw, under /s, no word on it - each one comes from the child's environment.
        Assert.Equal(@"/d /s /c """"%ALTSTABLE_ARG0%"" ""%ALTSTABLE_ARG1%"" ""%ALTSTABLE_ARG2%"" ""%ALTSTABLE_ARG3%""""",
            CodexImageGen.CommandLine(@"C:\Users\John Smith\npm\codex.cmd", ["exec", "-C", @"C:\Users\John Smith\job-%OS%"]));
        Assert.Throws<ArgumentException>(() => CodexImageGen.CommandLine("codex.cmd", ["exec", ""]));
        Assert.Throws<ArgumentException>(() => CodexImageGen.CommandLine("codex.cmd", ["exec", "a\"b"]));

        using var t = new TempInstall();
        var (exe, _, _, _) = Shim(t);
        var path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(exe));
            Assert.Equal(exe, CodexImageGen.Find(), StringComparer.OrdinalIgnoreCase);
            Environment.SetEnvironmentVariable("PATH", t.Root);
            Assert.Null(CodexImageGen.Find());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }
}

/// <summary>The worker in the controller, with a fake way to Codex: no CLI, no credits.</summary>
public class EnhanceWorkerTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0);
    private const string Guid1 = "Player-1-0000AAAA";

    // A capture of a level-20 troll, with the roster tables the enhancement reads.
    private static void Roster(TempInstall t, string account, string name, string guid, DateTime at, int level = 20, string race = "Troll")
    {
        var (b, w) = TestData.Pair(400, 1200, 150, 250, 100, 700);
        t.WriteShot(at, b);
        t.WriteShot(at.AddSeconds(1), w);
        var db = "AltStableDB = {\n[\"" + guid + "\"] = {\n[\"name\"] = \"" + name + "\",\n[\"class\"] = \"WARLOCK\",\n[\"race\"] = \"" + race + "\",\n[\"gender\"] = \"Female\",\n[\"level\"] = " + level + ",\n[\"lastUpdate\"] = 1790000000,\n},\n}\nAltStableConfig = {\n[\"hiddenCharacters\"] = {\n},\n}\n";
        var store = "AltStablePortraits = {\n[\"version\"] = 1,\n[\"renders\"] = {\n" + TestData.Record(name, guid, 1, at) + ",\n" + TestData.Record(name, guid, 2, at.AddSeconds(1)) + ",\n},\n}\n";
        t.WriteStore(account, db + store);
    }

    private static void CapableRoster(TempInstall t)
    {
        var addon = Path.Combine(t.Install.AddOnsDir, "AltStable", "AltStable.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(addon)!);
        File.WriteAllText(addon, "## Title: AltStable\n");
        var toc = Path.Combine(t.Install.AddOnsDir, "AltStableRoster", "AltStableRoster.toc");
        Directory.CreateDirectory(Path.GetDirectoryName(toc)!);
        File.WriteAllText(toc, "## Title: AltStable Roster\n## X-AltStable-Enhanced: 1\n");
    }

    // A picture Codex would be proud of: transparent, a standing figure, feet on the bottom.
    private static byte[] GoodPng()
    {
        var img = new RgbaImage(200, 400);
        for (var y = 40; y < 400; y++)
            for (var x = 60; x < 140; x++)
                img[x, y] = (150, 60, 200, 255);
        return PngCodec.Write(img);
    }

    private sealed class Fake
    {
        public int Calls;
        public byte[]? Png = GoodPng();
        public string? Failure;
        public TaskCompletionSource<bool>? Hold;
        public bool FinishAnyway;                      // a cancellation arrives too late: the picture comes back
        public string? Status = "Logged in using ChatGPT";
        public readonly List<string> Prompts = [];

        public EnhanceHooks Hooks => new(async (job, dir, ct) =>
        {
            Interlocked.Increment(ref Calls);
            Prompts.Add(job.Prompt);
            Assert.True(File.Exists(job.ReferencePng));
            if (Hold is { } hold)
            {
                using var reg = ct.Register(() => hold.TrySetCanceled());
                try { await hold.Task; }
                catch (OperationCanceledException) { if (!FinishAnyway) return new CodexResult(null, "cancelled", ""); }
            }
            return Failure is { } f ? new CodexResult(null, f, "") : new CodexResult(Png, null, "");
        }, _ => Task.FromResult(Status));
    }

    private static string Data(TempInstall t) => Path.Combine(t.Root, "data");

    private static Controller Started(TempInstall t, Fake fake, bool enhance = true)
    {
        new Settings { Started = true, WowFlavorDir = t.Install.FlavorDir, Enhance = enhance }.Save(Data(t));
        var c = new Controller(new StartupOptions(DataDir: Data(t)), () => throw new InvalidOperationException("detection was used"), enhance: fake.Hooks);
        c.Start();
        return c;
    }

    private static void Until(Func<bool> happened, string what, int seconds = 15)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (!happened())
        {
            Assert.True(DateTime.UtcNow < limit, "never happened: " + what);
            Thread.Sleep(50);
        }
    }

    private static void Settle(int ms = 1200) => Thread.Sleep(ms);

    [Fact]
    public void Off_by_default_nothing_is_sent_even_with_everything_in_place()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake();
        using var c = Started(t, fake, enhance: false);
        Until(() => c.Current.Shell.Report is not null && !c.Current.Shell.Converting, "the first pass");
        Settle();
        Assert.Equal(0, fake.Calls);
        Assert.False(Directory.Exists(new CutoutFolder(t.Install.CutoutAddonDir).EnhancedDir));
    }

    [Fact]
    public void On_a_portrait_becomes_an_enhanced_picture_once_and_the_window_is_told()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake();
        using var c = Started(t, fake);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => folder.Inventory().SingleOrDefault()?.Enhanced is not null, "the enhanced picture");
        Assert.Equal(1, fake.Calls);
        Assert.Contains("two toes", fake.Prompts.Single());

        // The files, the contract's way; the record; the manifest; the row; the notice.
        var meta = folder.ReadEnhancedMeta("kaleid-sumner")!;
        Assert.Equal(Guid1, meta.Guid);
        Assert.Equal("wow-like", meta.Enhancement!.Style);
        Assert.Equal(EnhancementSignature.HashOf(Path.Combine(folder.CutoutsDir, "kaleid-sumner.tga")), meta.Enhancement.SourceHash);
        Assert.Equal(EnhancementSignature.HashOf(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga")), meta.Enhancement.OutputHash);
        var history = AttemptHistory.Load(folder.EnhancedDir, Guid1);
        Assert.Equal(Attempt.Written, history.Last!.Outcome);
        Assert.Equal(meta.Enhancement.OutputHash, history.Last.OutputHash);
        Assert.Contains("enhanced = {", File.ReadAllText(folder.ManifestPath));
        Until(() => c.Current.Shell.Report?.Portraits?.Single().Enhanced is not null, "the row");
        var row = c.Current.Shell.Report!.Portraits!.Single();
        Assert.Equal("enhanced (wow-like)", row.EnhanceNote);
        Assert.Equal(Path.Combine("Enhanced", "kaleid-sumner.tga"), row.ThumbnailFile);
        Assert.NotNull(row.EnhancedModified);
        Assert.Equal(row.EnhancedModified, row.LastActivity);
        // Beside the pass's own note, not instead of it: the status line keeps "Last written".
        Assert.Equal(["Kaleid Sumner"], c.Current.Shell.LastEnhanced!.Names);
        Assert.Equal(["Kaleid Sumner"], c.Current.Shell.LastWritten!.Written);
        Assert.Contains("Last written: Kaleid Sumner", PassText.Activity(c.Current.Shell with { LastPass = new PassNote(T0, []) }, T0));
        Assert.StartsWith("Portrait enhanced: Kaleid Sumner", PassText.Headline(c.Current.Shell).Title);
        c.AcknowledgeUpdate();
        Assert.DoesNotContain("enhanced", PassText.Headline(c.Current.Shell).Title);
        Assert.Null(c.Current.Shell.Enhancing);
        Assert.Equal("Logged in using ChatGPT", c.Current.CodexStatus);

        // A later pass, a later wake: the same signature is never launched again.
        c.ConvertNow();
        Settle(2000);
        Assert.Equal(1, fake.Calls);
        // The job folder is gone.
        Assert.False(Directory.Exists(Path.Combine(Data(t), "enhance")) && Directory.GetDirectories(Path.Combine(Data(t), "enhance")).Length > 0);
    }

    [Fact]
    public void A_character_with_an_earlier_picture_still_says_Enhancing_while_the_next_is_made()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake();
        using var c = Started(t, fake);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => folder.Inventory().SingleOrDefault()?.Enhanced is not null, "the first picture");
        Until(() => c.Current.Shell.Enhancing is null, "the first job over");
        // Another style, held in flight: the note says both, the row says Enhancing, and sits first.
        fake.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.SetEnhance(true, 10, EnhanceStyles.Realistic);
        Until(() => fake.Calls == 2, "the second launch");
        Until(() => c.Current.Shell.Report?.Portraits?.Single().Enhancing == true, "the row in flight");
        var row = c.Current.Shell.Report!.Portraits!.Single();
        Assert.Equal("enhanced (wow-like); realistic enhancing", row.EnhanceNote);
        Assert.Equal("Enhancing", PassText.RowState(row));
        Assert.Equal(("enhanced (wow-like); realistic enhancing", true), folder.EnhanceState(Guid1));
        fake.Hold.SetResult(true);
        Until(() => c.Current.Shell.Report?.Portraits?.Single() is { Enhancing: false, EnhanceNote: "enhanced (realistic)" }, "the second picture");
    }

    [Fact]
    public void A_picture_that_cannot_be_written_is_recorded_as_failed_not_written()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        // A folder where the file should go: the replace cannot succeed.
        Directory.CreateDirectory(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga"));
        var fake = new Fake();
        using var c = Started(t, fake);
        Until(() => fake.Calls == 1 && AttemptHistoryReady(folder), "the attempt");
        Until(() => !AttemptHistory.Load(folder.EnhancedDir, Guid1).Last!.Outcome.StartsWith("written"), "the record corrected", 5);
        var last = AttemptHistory.Load(folder.EnhancedDir, Guid1).Last!;
        Assert.StartsWith("failed: could not be written", last.Outcome);
        Assert.Null(last.OutputHash);
        Assert.Null(c.Current.Shell.LastEnhanced);
    }

    [Fact]
    public async Task A_picture_that_comes_back_as_the_app_stops_or_the_game_changes_is_still_written()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake { Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously), FinishAnyway = true };
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        using (var c = Started(t, fake))
        {
            Until(() => fake.Calls == 1, "the launch");
            await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Assert.True(File.Exists(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga")));
        Assert.Equal(Attempt.Written, AttemptHistory.Load(folder.EnhancedDir, Guid1).Last!.Outcome);

        // Browse to another game while a picture is being made: the job is given up at once.
        using var t2 = new TempInstall();
        Roster(t2, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t2);
        using var other = new TempInstall();
        var fake2 = new Fake { Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var c2 = Started(t2, fake2);
        Until(() => fake2.Calls == 1, "the launch");
        Assert.True(c2.Browse(other.Install.FlavorDir));
        var folder2 = new CutoutFolder(t2.Install.CutoutAddonDir);
        Until(() => AttemptHistoryReady(folder2), "the record");
        Assert.Equal(Attempt.Cancelled, AttemptHistory.Load(folder2.EnhancedDir, Guid1).Last!.Outcome);
        Assert.Null(c2.Current.Shell.Enhancing);
    }

    [Fact]
    public void Stale_job_folders_and_odd_records_do_no_harm()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var stale = Path.Combine(Data(t), "enhance", "stale");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "reference.png"), "left by a run that died");
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Directory.CreateDirectory(folder.EnhancedDir);
        // An outcome with no reason, as a hand edit or a later version might leave: a note, not a crash.
        File.WriteAllText(AttemptHistory.PathFor(folder.EnhancedDir, Guid1),
            "{\"Guid\":\"" + Guid1 + "\",\"Attempts\":[{\"Signature\":\"abc\",\"Started\":\"2026-09-29T12:00:00\",\"Ended\":\"2026-09-29T12:01:00\",\"Outcome\":\"failed\"}]}");
        Assert.Equal("enhancement failed: (no reason given)", folder.EnhanceNote(Guid1));
        var fake = new Fake();
        using var c = Started(t, fake, enhance: false);
        Until(() => c.Current.Shell.Report is not null && !c.Current.Shell.Converting, "the first pass");
        Assert.False(Directory.Exists(stale));
        Assert.Equal("enhancement failed: (no reason given)", c.Current.Shell.Report!.Portraits!.Single().EnhanceNote);
    }

    [Fact]
    public void A_refused_picture_and_a_failed_run_are_recorded_once_and_never_retried()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var opaque = new RgbaImage(100, 200);
        for (var i = 3; i < opaque.Pixels.Length; i += 4) opaque.Pixels[i] = 255;
        var fake = new Fake { Png = PngCodec.Write(opaque) };
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        using (var c = Started(t, fake))
        {
            Until(() => fake.Calls == 1 && AttemptHistoryReady(folder), "the refusal");
            var history = AttemptHistory.Load(folder.EnhancedDir, Guid1);
            Assert.Equal("refused: " + Enhancement.TransparentBorder, history.Last!.Outcome);
            Assert.False(File.Exists(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga")));
            Until(() => c.Current.Shell.Report?.Portraits?.Single().EnhanceNote is not null, "the row's note");
            Assert.Equal("enhancement refused: " + Enhancement.TransparentBorder, c.Current.Shell.Report!.Portraits!.Single().EnhanceNote);
            c.ConvertNow();
            Settle(2000);
            Assert.Equal(1, fake.Calls);
        }
        // A new controller, the same install: still not retried.
        using (var again = Started(t, fake))
        {
            Until(() => again.Current.Shell.Report is not null && !again.Current.Shell.Converting, "the first pass");
            Settle(2000);
            Assert.Equal(1, fake.Calls);
        }
        // A failure is likewise once.
        using var t2 = new TempInstall();
        Roster(t2, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t2);
        var failing = new Fake { Failure = "no auth" };
        using var c2 = Started(t2, failing);
        var folder2 = new CutoutFolder(t2.Install.CutoutAddonDir);
        Until(() => failing.Calls == 1 && AttemptHistoryReady(folder2), "the failure");
        Assert.Equal("failed: no auth", AttemptHistory.Load(folder2.EnhancedDir, Guid1).Last!.Outcome);
        c2.ConvertNow();
        Settle(2000);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public void A_picture_whose_data_will_not_inflate_is_a_failed_attempt_and_the_next_candidate_is_not_kept_waiting()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[3] = 2; ihdr[7] = 2; ihdr[8] = 8; ihdr[9] = 6;
        PngCodec.Chunk(ms, "IHDR", ihdr);
        PngCodec.Chunk(ms, "IDAT", new byte[] { 0, 0, 0, 0 });
        PngCodec.Chunk(ms, "IEND", []);
        var fake = new Fake { Png = ms.ToArray() };
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        using var c = Started(t, fake);
        Until(() => fake.Calls == 1 && AttemptHistoryReady(folder), "the failure");
        Assert.StartsWith("failed: the image data is not valid zlib", AttemptHistory.Load(folder.EnhancedDir, Guid1).Last!.Outcome);
        Until(() => c.Current.Shell.Enhancing is null, "the activity line cleared");
        Assert.False(Directory.Exists(Path.Combine(Data(t), "enhance")) && Directory.GetDirectories(Path.Combine(Data(t), "enhance")).Length > 0);
    }

    private static bool AttemptHistoryReady(CutoutFolder folder)
    {
        try { return AttemptHistory.Load(folder.EnhancedDir, Guid1).Last?.Ended is not null; }
        catch (AttemptHistoryException) { return false; }
    }

    [Fact]
    public void Below_the_level_hidden_or_without_a_capable_roster_nobody_is_sent()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0, level: 9);
        CapableRoster(t);
        var fake = new Fake();
        using (var c = Started(t, fake))
        {
            Until(() => c.Current.Shell.Report is not null && !c.Current.Shell.Converting, "the first pass");
            Settle(1500);
            Assert.Equal(0, fake.Calls);
            // The level lowered from the window: now eligible, at once.
            c.SetEnhance(true, 5, EnhanceStyles.WowLike);
            Until(() => fake.Calls == 1, "the generation after the level change");
        }
        using var t2 = new TempInstall();
        Roster(t2, "1#1", "Kaleid Sumner", Guid1, T0);
        // No marker: the addon cannot draw it, so nothing is spent.
        var fake2 = new Fake();
        using var c2 = Started(t2, fake2);
        Until(() => c2.Current.Shell.Report is not null && !c2.Current.Shell.Converting, "the first pass");
        Settle(1500);
        Assert.Equal(0, fake2.Calls);
        Assert.NotNull(PassText.EnhanceUnavailable(codexFound: true, rosterCapable: false));
    }

    [Fact]
    public void Turning_it_off_cancels_the_picture_in_flight_and_records_it()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake { Hold = new TaskCompletionSource<bool>() };
        // Off until the first pass has built the rows: the launch alone must then make the row say so.
        using var c = Started(t, fake, enhance: false);
        Until(() => c.Current.Shell.Report is not null && !c.Current.Shell.Converting, "the first pass");
        Settle(2500);  // and the rerun the pass's own deletions queue: no pass may rebuild the rows after the launch
        c.SetEnhance(true, 10, EnhanceStyles.WowLike);
        Until(() => fake.Calls == 1, "the launch");
        Until(() => c.Current.Shell.Enhancing is not null, "the activity");
        Assert.Contains("Kaleid Sumner (1 of 1)", c.Current.Shell.Enhancing);
        Assert.StartsWith("Enhancing Kaleid Sumner", PassText.Activity(c.Current.Shell, T0));
        // The row says so from the launch, and the tray.
        Until(() => c.Current.Shell.Report?.Portraits?.Single().Enhancing == true, "the row's state");
        Assert.Equal("Enhancing", PassText.RowState(c.Current.Shell.Report!.Portraits!.Single()));
        Assert.EndsWith("enhancing Kaleid Sumner (1 of 1)", PassText.TrayTip(c.Current.Shell));
        var ended = new List<EnhanceResult>();
        c.EnhanceCompleted += ended.Add;
        c.SetEnhance(false, 10, EnhanceStyles.WowLike);
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        Until(() => AttemptHistoryReady(folder), "the record");
        Assert.Equal(Attempt.Cancelled, AttemptHistory.Load(folder.EnhancedDir, Guid1).Last!.Outcome);
        Assert.False(File.Exists(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga")));
        Until(() => c.Current.Shell.Enhancing is null, "the activity line cleared");
        // The end is announced once, and stays under the switch.
        Until(() => ended.Count == 1, "the event");
        Assert.Equal(("Kaleid Sumner", Attempt.Cancelled), (ended[0].Name, ended[0].Outcome));
        Assert.Equal("Last: Kaleid Sumner cancelled, " + PassText.EnhanceStatus(c.Current.Shell, DateTime.Now)!.Split(", ")[1], PassText.EnhanceStatus(c.Current.Shell, DateTime.Now));
        Assert.False(c.Current.Shell.Report!.Portraits!.Single().Enhancing);
        // On again: the cancelled picture is tried once more - and, cancelled again, no more.
        fake.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.SetEnhance(true, 10, EnhanceStyles.WowLike);
        Until(() => fake.Calls == 2, "the second try");
        c.SetEnhance(false, 10, EnhanceStyles.WowLike);
        Until(() => AttemptHistory.Load(folder.EnhancedDir, Guid1).Attempts.Count(a => a.Ended is not null) == 2, "the second record");
        c.SetEnhance(true, 10, EnhanceStyles.WowLike);
        Settle(1500);
        Assert.Equal(2, fake.Calls);
        // Another style is another combination - and a picture in flight, of the old style,
        // is given up for it rather than spent to the end.
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Hold = second;
        c.SetEnhance(true, 10, EnhanceStyles.Realistic);
        Until(() => fake.Calls == 3, "the second style");
        var third = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Hold = third;
        c.SetEnhance(true, 10, EnhanceStyles.Cartoonish);
        Until(() => second.Task.IsCanceled, "the realistic picture given up");
        Until(() => fake.Calls == 4, "the third style");
        third.SetResult(true);
        Until(() => File.Exists(Path.Combine(folder.EnhancedDir, "kaleid-sumner.tga")), "the cartoon written");
        var attempts = AttemptHistory.Load(folder.EnhancedDir, Guid1).Attempts;
        Assert.Equal([Attempt.Cancelled, Attempt.Cancelled, Attempt.Cancelled, Attempt.Written], attempts.Select(a => a.Outcome));
        Assert.Equal([EnhanceStyles.WowLike, EnhanceStyles.WowLike, EnhanceStyles.Realistic, EnhanceStyles.Cartoonish], attempts.Select(a => a.Style));
    }

    [Fact]
    public void An_attempt_the_app_did_not_live_to_finish_is_closed_and_made_once_more()
    {
        using var t = new TempInstall();
        Roster(t, "1#1", "Kaleid Sumner", Guid1, T0);
        CapableRoster(t);
        var fake = new Fake();
        // First: a plain run, so the portrait exists and the signature is knowable.
        using (var c = Started(t, fake, enhance: false))
        {
            Until(() => c.Current.Shell.Report is not null && !c.Current.Shell.Converting, "the first pass");
        }
        var folder = new CutoutFolder(t.Install.CutoutAddonDir);
        var meta = folder.ReadMeta("kaleid-sumner")!;
        var character = new RosterCharacter(Guid1, "Kaleid Sumner", "WARLOCK", "Troll", "Female", 20, 1790000000);
        var signature = EnhancementSignature.Compute(Guid1, meta.Epoch, EnhancementSignature.HashOf(Path.Combine(folder.CutoutsDir, "kaleid-sumner.tga")),
            character, EnhanceStyles.WowLike, Settings.DefaultEnhanceModel, EnhanceEfforts.Low);
        // An "unknown": launched, never finished - as after a crash.
        AttemptHistory.Load(folder.EnhancedDir, Guid1).Begin(new Attempt { Signature = signature, Started = T0 });
        using var again = Started(t, fake);
        // Closed - the row must not say "enhancing" about a run that is dead - and, a cancel
        // being nobody's fault, tried once more.
        Until(() => folder.Inventory().SingleOrDefault()?.Enhanced is not null, "the picture, on the second try");
        Assert.Equal(1, fake.Calls);
        var attempts = AttemptHistory.Load(folder.EnhancedDir, Guid1).Attempts;
        Assert.Equal(["cancelled: the app did not live to finish it", Attempt.Written], attempts.Select(a => a.Outcome));
        Assert.All(attempts, a => Assert.NotNull(a.Ended));
        Assert.Equal(signature, attempts[1].Signature);
    }
}
