using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PartCombiner.Core;

public readonly record struct ExtractProgress(int Percent, string? CurrentFile);

public sealed record ExtractResult(bool Success, string? Error, bool PasswordProblem, string? Warning);

/// <summary>Runs the 7-Zip console (7z.exe) to unpack RAR, 7z, ZIP, TAR and split archives.</summary>
public sealed partial class SevenZipEngine
{
    public string ExecutablePath { get; }

    public string Version { get; }

    private SevenZipEngine(string path)
    {
        ExecutablePath = path;
        string? version = null;
        try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim(); }
        catch (FileNotFoundException) { }
        Version = string.IsNullOrWhiteSpace(version) ? "" : version!;
    }

    /// <summary>Find an installed 7-Zip. <paramref name="preferredPath"/> wins when it points at a real file.</summary>
    public static SevenZipEngine? Locate(string? preferredPath = null)
    {
        foreach (var candidate in Candidates(preferredPath))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return new SevenZipEngine(Path.GetFullPath(candidate));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Wrap a specific 7z.exe chosen by the user. Returns null if it isn't a file.</summary>
    public static SevenZipEngine? FromPath(string path) => File.Exists(path) ? new SevenZipEngine(Path.GetFullPath(path)) : null;

    private static IEnumerable<string?> Candidates(string? preferredPath)
    {
        yield return preferredPath;

        var appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, "7z.exe");
        yield return Path.Combine(appDir, "7-Zip", "7z.exe");

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? dir64 = null, dir = null;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"Software\7-Zip");
                    dir64 = key?.GetValue("Path64") as string;
                    dir = key?.GetValue("Path") as string;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                if (dir64 is not null) yield return Path.Combine(dir64, "7z.exe");
                if (dir is not null) yield return Path.Combine(dir, "7z.exe");
            }
        }

        foreach (var env in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf))
                yield return Path.Combine(pf, "7-Zip", "7z.exe");
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var exe in new[] { "7z.exe", "7zz.exe", "NanaZipC.exe" })
        {
            foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? full = null;
                try { full = Path.Combine(dir.Trim('"'), exe); }
                catch (ArgumentException) { }
                if (full is not null) yield return full;
            }
        }
    }

    /// <summary>
    /// Extract <paramref name="archivePath"/> (the first volume for multi-part archives) into
    /// <paramref name="outputDir"/>. The password goes through stdin, never the command line.
    /// </summary>
    public Task<ExtractResult> ExtractAsync(
        string archivePath,
        string outputDir,
        string? password,
        IProgress<ExtractProgress>? progress,
        CancellationToken ct) =>
        RunAsync(Path.GetDirectoryName(archivePath),
            ["x", "-o" + outputDir, "-y", "-aou", "-bso0", "-bsp1", "-bse2", "-sccUTF-8", "--", archivePath],
            // 7-Zip only reads this line if the archive turns out to be encrypted.
            // An empty line makes it fail with "Wrong password" instead of waiting forever.
            [password ?? ""], progress, ct);

    /// <summary>
    /// Pack <paramref name="source"/> (a file or a whole folder) into a new archive. <paramref name="type"/> is
    /// "7z" or "zip". A folder keeps its name inside the archive, so extracting gives the folder back.
    /// With a <paramref name="password"/>, the contents are AES-256 encrypted (for 7z the file names too).
    /// The password goes through stdin, never the command line.
    /// </summary>
    public Task<ExtractResult> CreateArchiveAsync(
        string source,
        string archivePath,
        string type,
        string? password,
        IProgress<ExtractProgress>? progress,
        CancellationToken ct)
    {
        var args = new List<string> { "a", "-t" + type, "-mx5", "-ssw", "-bso0", "-bsp1", "-bse2", "-sccUTF-8" };
        string[] input = [];
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p"); // no value: 7-Zip asks for it (twice: enter + verify), which we answer on stdin
            args.Add(type == "7z" ? "-mhe=on" : "-mem=AES256");
            input = [password, password];
        }
        args.AddRange(["--", archivePath, source]);
        return RunAsync(Path.GetDirectoryName(Path.GetFullPath(source).TrimEnd('\\')), [.. args], input, progress, ct);
    }

    private async Task<ExtractResult> RunAsync(
        string? workingDirectory,
        string[] args,
        string[] stdinLines,
        IProgress<ExtractProgress>? progress,
        CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ExtractResult(false, "Couldn't start 7-Zip: " + ex.Message, false, null);
        }

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        // Answers to 7-Zip's password prompts. Closing stdin afterwards means any further prompt fails
        // instead of waiting forever.
        try
        {
            foreach (var line in stdinLines)
                await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { /* 7-Zip already exited; the exit code tells the story */ }

        var stdoutTask = PumpProgressAsync(process.StandardOutput, progress);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return Interpret(process.ExitCode, stderr);
    }

    /// <summary>
    /// Total size of everything in the archive once unpacked, read from its listing (headers only, so it's quick).
    /// Null when 7-Zip can't list it (e.g. encrypted headers and no or wrong password) or doesn't know every size.
    /// </summary>
    public async Task<long?> GetUnpackedSizeAsync(string archivePath, string? password, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in new[] { "l", "-slt", "-sccUTF-8", "--", archivePath })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            await process.StandardInput.WriteLineAsync(password ?? "").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { }

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var parser = new ListingSizeParser();
        try
        {
            // Read line by line: listings of archives with many entries can be several MB.
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                parser.Add(line);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await stderrTask.ConfigureAwait(false);

        return process.ExitCode == 0 ? parser.Result : null;
    }

    /// <summary>Sums "Size = n" over the entries of a <c>7z l -slt</c> listing.</summary>
    internal sealed class ListingSizeParser
    {
        private bool _inEntries;
        private bool _unknown;
        private int _entries;
        private long _total;

        public void Add(string line)
        {
            // Archive properties come first (including a "Size" for split/nested containers);
            // the entries start after a line of ten dashes.
            if (!_inEntries)
            {
                _inEntries = line == "----------";
                return;
            }
            if (!line.StartsWith("Size =", StringComparison.Ordinal))
                return;

            _entries++;
            var value = line.AsSpan(6).Trim();
            if (value.IsEmpty)
                _unknown = true; // e.g. some .xz/.bz2 streams don't record their unpacked size
            else if (long.TryParse(value, out long size) && size >= 0)
                _total = size > long.MaxValue - _total ? long.MaxValue : _total + size;
            else
                _unknown = true;
        }

        public long? Result => _inEntries && _entries > 0 && !_unknown ? _total : null;
    }

    [GeneratedRegex(@"^\s*(?<p>\d{1,3})%(?:\s+\d+)?(?:\s+[-+=U]\s+(?<f>.+?))?\s*$")]
    private static partial Regex ProgressRx();

    private static async Task PumpProgressAsync(StreamReader reader, IProgress<ExtractProgress>? progress)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int lastPercent = -1;
        string? lastFile = null;

        void Flush()
        {
            if (line.Length == 0) return;
            var m = ProgressRx().Match(line.ToString());
            line.Clear();
            if (!m.Success) return;

            int percent = Math.Clamp(int.Parse(m.Groups["p"].ValueSpan), 0, 100);
            string? file = m.Groups["f"].Success ? m.Groups["f"].Value : lastFile;
            if (percent == lastPercent && file == lastFile) return;
            lastPercent = percent;
            lastFile = file;
            progress?.Report(new ExtractProgress(percent, file));
        }

        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\r' or '\n' or '\b')
                    Flush();
                else
                    line.Append(c);
            }
        }
        Flush();
    }

    internal static ExtractResult Interpret(int exitCode, string stderr)
    {
        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();
        bool Has(string text) => lines.Any(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));

        bool passwordProblem = Has("Wrong password") || Has("Can not open encrypted archive") || Has("Cannot open encrypted archive");

        if (exitCode == 0)
            return new ExtractResult(true, null, false, null);

        if (exitCode == 1 && !passwordProblem)
        {
            // Warnings usually mean something didn't line up (e.g. extra data after the archive end).
            // Treat them as a failure so a half-right result is never saved.
            var warning = lines.FirstOrDefault(l => !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) && !l.EndsWith(':'))
                          ?? "warnings while extracting";
            return new ExtractResult(false, $"7-Zip reported a problem: {warning}", false, null);
        }

        string error;
        if (passwordProblem)
            error = "Wrong or missing password";
        else if (Has("Missing volume"))
            error = "A volume is missing: " + (lines.First(l => l.Contains("Missing volume", StringComparison.OrdinalIgnoreCase)).Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "");
        else if (Has("Unexpected end of archive"))
            error = "The archive ends unexpectedly — a part is probably missing or incomplete";
        else if (Has("Can not open the file as") || Has("Cannot open the file as") || Has("is not archive"))
            error = "This doesn't look like a valid archive — the first part may be missing or damaged";
        else if (Has("CRC Failed") || Has("Data Error") || Has("Headers Error"))
            error = "The data is corrupt (checksum error) — re-download the damaged part";
        else if (Has("not enough space") || Has("disk full") || Has("There is not enough space"))
            error = "Not enough disk space";
        else if (Has("Unsupported"))
            error = "This archive uses a method 7-Zip can't unpack";
        else if (exitCode == 8)
            error = "7-Zip ran out of memory";
        else
        {
            var detail = lines.FirstOrDefault(l => !l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) && !l.EndsWith(':'));
            error = detail ?? $"7-Zip failed (exit code {exitCode})";
        }

        return new ExtractResult(false, error, passwordProblem, null);
    }
}
