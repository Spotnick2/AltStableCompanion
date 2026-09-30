using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AltStableCompanion.Core;

/// <summary>What one generation is asked for.</summary>
public sealed record CodexJob(string Prompt, string ReferencePng, string Model, string Effort, TimeSpan Timeout);

/// <summary>
/// What came back: the PNG's bytes, or why there are none. <see cref="Failure"/> is a short
/// reason for the attempt record; <see cref="Detail"/> is the tail of what Codex printed,
/// for the log.
/// </summary>
public sealed record CodexResult(byte[]? Png, string? Failure, string Detail)
{
    public bool Ok => Png is not null;
}

/// <summary>
/// Runs the Codex CLI for one picture, and nothing else. The invocation is fixed (measured
/// on codex-cli 0.158.0, 2026-09-30: with these flags what still reaches the agent is the
/// player's own global instructions; no MCP servers, no web search):
/// <c>codex exec --ignore-user-config --ignore-rules --ephemeral --skip-git-repo-check
/// --sandbox workspace-write -m model -c model_reasoning_effort=effort -c web_search=disabled
/// -c project_doc_max_bytes=0 -C jobDir -i reference.png -o verdict</c>, the prompt on stdin.
///
/// Attribution is strict: Codex writes into its own <c>generated_images</c>; the folder is
/// listed before and after, exactly one new PNG must appear, and its path must be the one the
/// answer reports as <c>ARTIFACT_PATH:</c>. Zero, two, a wrong path, a wrong magic: the job
/// fails without guessing, and that folder is never cleaned. The job folder - the reference,
/// the verdict, a copy of the picture - is the caller's to delete.
/// </summary>
public sealed partial class CodexImageGen(string executable, string codexHome)
{
    public const string ArtifactPrefix = "ARTIFACT_PATH:";

    public string Executable { get; } = executable;
    public string CodexHome { get; } = codexHome;
    public string GeneratedImagesDir => Path.Combine(CodexHome, "generated_images");

    /// <summary>The user's Codex home: <c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    public static string DefaultHome()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : home;
    }

    /// <summary>
    /// The codex command on PATH - the npm shim <c>codex.cmd</c> on Windows - or null. Looked
    /// for the way the shell would: each PATH folder, each PATHEXT extension.
    /// </summary>
    public static string? Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim(), "codex" + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// What <c>codex login status</c> says, in a few words ("Logged in using ChatGPT"), or null
    /// when the command fails or says nothing. Local and quick; nothing is sent.
    /// </summary>
    public static async Task<string?> LoginStatusAsync(string executable, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(StartInfo(executable, ["login", "status"], null, null));
            if (p is null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            var error = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var text = (output + "\n" + error).Trim();
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return p.ExitCode == 0 && line is not null ? line : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The arguments, as words: what a test can assert on, and what the shell gets.</summary>
    public static IReadOnlyList<string> Arguments(CodexJob job, string jobDir, string verdict)
    {
        if (!Token().IsMatch(job.Model)) throw new ArgumentException("a model name is letters, digits, dots and dashes", nameof(job));
        if (!Token().IsMatch(job.Effort)) throw new ArgumentException("an effort is letters, digits, dots and dashes", nameof(job));
        return
        [
            "exec", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check",
            "--sandbox", "workspace-write",
            "-m", job.Model,
            "-c", "model_reasoning_effort=" + job.Effort,
            "-c", "web_search=disabled",
            "-c", "project_doc_max_bytes=0",
            "-C", jobDir,
            "-i", job.ReferencePng,
            "-o", verdict,
        ];
    }

    /// <summary>
    /// Generate one picture. <paramref name="jobDir"/> exists and holds the reference; the
    /// verdict is written into it. Cancellation and the job's timeout both kill the child
    /// tree. Never throws for what Codex does; throws for what the caller got wrong.
    /// </summary>
    public async Task<CodexResult> GenerateAsync(CodexJob job, string jobDir, CancellationToken ct)
    {
        var verdict = Path.Combine(jobDir, "verdict.md");
        var args = Arguments(job, jobDir, verdict);
        var before = Snapshot();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(job.Timeout);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        int exit;
        using (var p = new Process { StartInfo = StartInfo(Executable, args, jobDir, CodexHome) })
        {
            p.OutputDataReceived += (_, e) => Append(stdout, e.Data);
            p.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
            try
            {
                p.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new CodexResult(null, "codex could not be started: " + ex.Message, "");
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.StandardInput.WriteAsync(job.Prompt.AsMemory(), timeout.Token);
                p.StandardInput.Close();
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                return new CodexResult(null, ct.IsCancellationRequested ? "cancelled" : $"timed out after {job.Timeout.TotalSeconds:0} s", Tail(stdout, stderr));
            }
            exit = p.ExitCode;
        }
        if (exit != 0) return new CodexResult(null, $"codex exited with {exit}", Tail(stdout, stderr));

        // Attribution.
        var reported = ReportedPath(verdict);
        var fresh = Snapshot().Except(before, StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh.Count != 1)
        {
            return new CodexResult(null, fresh.Count == 0
                ? "no new picture appeared in Codex's generated_images"
                : $"{fresh.Count} new pictures appeared in Codex's generated_images; only one can be this character's", Tail(stdout, stderr));
        }
        var picture = fresh[0];
        if (reported is null || !SamePath(reported, picture))
        {
            return new CodexResult(null, reported is null ? "the answer names no picture" : "the answer names a picture that is not the one that appeared", Tail(stdout, stderr));
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CodexResult(null, "the picture could not be read: " + ex.Message, Tail(stdout, stderr));
        }
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            return new CodexResult(null, "the picture is not a PNG", Tail(stdout, stderr));
        }
        return new CodexResult(bytes, null, Tail(stdout, stderr));
    }

    // Every PNG under generated_images, by full path. A folder that is not there yet is empty.
    private HashSet<string> Snapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(GeneratedImagesDir)) return set;
        try
        {
            foreach (var f in Directory.EnumerateFiles(GeneratedImagesDir, "*.png", SearchOption.AllDirectories)) set.Add(Path.GetFullPath(f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanished mid-list: what was seen is what was seen.
        }
        return set;
    }

    private static string? ReportedPath(string verdict)
    {
        if (!File.Exists(verdict)) return null;
        string? last = null;
        foreach (var line in File.ReadLines(verdict))
        {
            var i = line.IndexOf(ArtifactPrefix, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) last = line[(i + ArtifactPrefix.Length)..].Trim().Trim('"', '`', '\'', ' ');
        }
        return string.IsNullOrWhiteSpace(last) ? null : last;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    // The launch. A .cmd or .bat (npm's shim) needs the command processor, run with no
    // AutoRun and the whole line quoted for it; anything else is started as it is.
    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> args, string? workingDir, string? codexHome)
    {
        ProcessStartInfo psi;
        var ext = Path.GetExtension(executable);
        if (OperatingSystem.IsWindows() && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(string.Join(' ', new[] { executable }.Concat(args).Select(Quote)));
        }
        else
        {
            psi = new ProcessStartInfo(executable);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        if (codexHome is not null) psi.Environment["CODEX_HOME"] = codexHome;
        return psi;
    }

    // cmd.exe quoting: the argument in double quotes, with the characters cmd itself acts on
    // escaped; a double quote inside is not something a path or our arguments ever hold.
    private static string Quote(string arg)
    {
        if (arg.Contains('"')) throw new ArgumentException("a double quote cannot be passed through cmd.exe", nameof(arg));
        var needs = arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || "&|<>^%()!".Contains(c));
        return needs ? "\"" + arg + "\"" : arg;
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            sb.AppendLine(line);
            // Bounded: the log wants the tail, not a transcript.
            if (sb.Length > 16 * 1024) sb.Remove(0, sb.Length - 12 * 1024);
        }
    }

    private static string Tail(StringBuilder stdout, StringBuilder stderr)
    {
        string a, b;
        lock (stdout) a = stdout.ToString();
        lock (stderr) b = stderr.ToString();
        var text = (b.Length > 0 ? b : a).Trim();
        return text.Length > 600 ? text[^600..] : text;
    }

    [GeneratedRegex(@"^[a-z0-9.-]+\z", RegexOptions.IgnoreCase)]
    private static partial Regex Token();
}
