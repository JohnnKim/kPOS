using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Kpos;

internal readonly record struct ReleaseVersion(Version Number, int Preview) : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.TrimStart('v').Split('+')[0].Split('-');
        if (parts.Length > 2 || !Version.TryParse(parts[0], out Version? number) ||
            number.Build < 0 || number.Revision >= 0)
            return false;

        int preview = int.MaxValue;
        if (parts.Length == 2 && (!parts[1].StartsWith("preview.", StringComparison.Ordinal) ||
            !int.TryParse(parts[1].AsSpan(8), out preview) || preview <= 0 || preview == int.MaxValue))
            return false;

        version = new ReleaseVersion(number, preview);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int comparison = Number.CompareTo(other.Number);
        return comparison == 0 ? Preview.CompareTo(other.Preview) : comparison;
    }

    public static string RunningVersion => (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static bool IsNewerExecutable(string source, string target) =>
        TryParse(FileVersionInfo.GetVersionInfo(source).ProductVersion, out ReleaseVersion incoming) &&
        TryParse(FileVersionInfo.GetVersionInfo(target).ProductVersion, out ReleaseVersion installed) &&
        incoming.CompareTo(installed) > 0;
}

internal sealed record ReleasePackage(string Tag, string FileName, Uri Url, string Digest, long Size);

internal static class ReleaseUpdates
{
    private const string Repository = "JohnnKim/kPOS";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };

    static ReleaseUpdates()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("kPOS-Windows");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static async Task<ReleasePackage?> FindNewer(CancellationToken cancellationToken)
    {
        if (!ReleaseVersion.TryParse(ReleaseVersion.RunningVersion, out ReleaseVersion running))
            throw new InvalidDataException("The installed kPOS version could not be identified.");

        var releases = await Client.GetFromJsonAsync<GitHubRelease[]>(
            $"https://api.github.com/repos/{Repository}/releases?per_page=30", cancellationToken).ConfigureAwait(false);
        ReleasePackage? newest = null;
        ReleaseVersion selected = running;
        foreach (var release in releases ?? [])
        {
            if (release.Draft || release.Tag is not string tag || !tag.StartsWith('v') ||
                !ReleaseVersion.TryParse(tag, out ReleaseVersion candidate) || candidate.CompareTo(selected) <= 0)
                continue;

            string name = $"kPOS-{tag[1..]}-win-x64.zip";
            var asset = release.Assets?.FirstOrDefault(entry => entry.Name == name && entry.State == "uploaded");
            string url = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (asset is null || asset.DownloadUrl != url || asset.Size is <= 0 or > 1_000_000_000 ||
                asset.Digest is not string digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
                continue;

            newest = new ReleasePackage(tag, name, new Uri(url), digest[7..], asset.Size);
            selected = candidate;
        }

        return newest;
    }

    public static async Task<string> Download(ReleasePackage release, string directory, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        string destination = Path.Combine(directory, release.FileName);
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            byte[] hash = await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false);
            if (existing.Length != release.Size || !Convert.ToHexString(hash).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A different file with the same name exists. Choose another download folder.");
            progress.Report(100);
            return destination;
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[128 * 1024];
            long received = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, useAsync: true))
            {
                int lastPercent = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                        break;

                    received += count;
                    if (received > release.Size)
                        throw new InvalidDataException("The download is larger than the published file.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, count);
                    int percent = (int)(received * 100 / release.Size);
                    if (percent != lastPercent)
                        progress.Report(lastPercent = percent);
                }
            }

            if (received != release.Size ||
                !Convert.ToHexString(digest.GetHashAndReset()).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file did not match the release checksum.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
