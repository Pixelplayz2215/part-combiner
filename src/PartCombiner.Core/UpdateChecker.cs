using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PartCombiner.Core;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string PageUrl,
    string? DownloadUrl,
    long Size,
    string? Sha256,
    string Notes);

/// <summary>
/// Looks for a newer Part Combiner on the project's GitHub releases page and downloads it. The download is only
/// used if its SHA-256 matches the digest GitHub publishes for that release file.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "Pixelplayz2215/part-combiner";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public const string AssetName = "PartCombiner.exe";

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PartCombiner", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    /// <summary>The running app's version (PARTCOMBINER_VERSION_OVERRIDE pretends to be another one, for testing).</summary>
    public static Version CurrentVersion
    {
        get
        {
            if (Version.TryParse(Environment.GetEnvironmentVariable("PARTCOMBINER_VERSION_OVERRIDE"), out var forced))
                return forced;
            var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly;
            return asm.GetName().Version ?? new Version(0, 0);
        }
    }

    /// <summary>The latest release if it's newer than <paramref name="current"/>; null if not, or if GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            using var response = await Http.Value
                .GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var latest = ParseRelease(json);
            return latest is not null && latest.Version > Normalize(current) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Reads GitHub's "latest release" JSON. Drafts and pre-releases are ignored.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease"))
            return null;
        var tag = Str(root, "tag_name");
        if (tag is null || ParseTag(tag) is not { } version)
            return null;

        string? url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Str(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                url = Str(asset, "browser_download_url");
                size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                var digest = Str(asset, "digest");
                if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..].ToLowerInvariant();
                break;
            }
        }

        return new UpdateInfo(version, tag, Str(root, "html_url") ?? ReleasesPage, url, size, sha, Str(root, "body") ?? "");
    }

    /// <summary>"v1.2.0", "1.2", "V2.0.1" → a version with three parts; anything else → null.</summary>
    internal static Version? ParseTag(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// Downloads the release exe to <paramref name="destination"/> and checks it. Returns null on success or a
    /// message saying what went wrong (the partial file is removed).
    /// </summary>
    public static async Task<string?> DownloadAsync(UpdateInfo update, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (update.DownloadUrl is null)
            return "This release has no PartCombiner.exe to download";
        if (update.Sha256 is null)
            return "GitHub didn't publish a checksum for this download, so it can't be verified";

        try
        {
            using var response = await Http.Value
                .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return $"Download failed ({(int)response.StatusCode})";

            long total = response.Content.Headers.ContentLength ?? update.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (actual != update.Sha256)
            {
                TryDelete(destination);
                return "The download doesn't match GitHub's checksum, so it wasn't used";
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(destination);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDelete(destination);
            return "Download failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Puts a downloaded exe in place of <paramref name="exePath"/>. A running exe can't be overwritten but can be
    /// renamed, so the current one becomes "*.old" (deleted on the next start).
    /// </summary>
    public static void Swap(string exePath, string downloaded)
    {
        var old = exePath + ".old";
        TryDelete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(downloaded, exePath);
        }
        catch
        {
            File.Move(old, exePath); // put the working version back
            throw;
        }
    }

    /// <summary>Removes the "*.old" left behind by the last update.</summary>
    public static void CleanUpAfterUpdate(string exePath) => TryDelete(exePath + ".old");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
