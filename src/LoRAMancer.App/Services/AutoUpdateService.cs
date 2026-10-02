using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed partial class AutoUpdateService {
    private readonly HttpClient _httpClient;
    private const string GitHubApiBase = "https://api.github.com/repos/zombiehausAI/LoRAMancer/releases";
    private const string FallbackStaticUrl = "https://raw.githubusercontent.com/zombiehausAI/LoRAMancer/main/version.json";

    public event Action<int>? OnDownloadProgress;

    public UpdateChannel CurrentChannel { get; set; }

    public AutoUpdateService(HttpClient? httpClient = null) {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent")) {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LoRAMancer-DesktopApp");
        }
        CurrentChannel = DefaultChannel;
    }

    public string CurrentVersion {
        get {
            try {
                string[] searchPaths = [
                    Path.Combine(AppContext.BaseDirectory, "version.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "version.json")
                ];
                foreach (string path in searchPaths) {
                    if (File.Exists(path)) {
                        string json = File.ReadAllText(path);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("version", out JsonElement vElem) && !string.IsNullOrWhiteSpace(vElem.GetString())) {
                            return vElem.GetString()!.Trim();
                        }
                    }
                }
            } catch { }

            Assembly assembly = Assembly.GetExecutingAssembly();
            string? infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer)) {
                int plusIndex = infoVer.IndexOf('+');
                return plusIndex >= 0 ? infoVer[..plusIndex] : infoVer;
            }
            Version? version = assembly.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
    }

    public bool IsDevBuild =>
        CurrentVersion.Contains("-dev", StringComparison.OrdinalIgnoreCase) ||
        CurrentVersion.Contains("-preview", StringComparison.OrdinalIgnoreCase) ||
        CurrentVersion.Contains("-alpha", StringComparison.OrdinalIgnoreCase);

    public UpdateChannel DefaultChannel => IsDevBuild ? UpdateChannel.Dev : UpdateChannel.Stable;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateChannel? channel = null, CancellationToken cancellationToken = default) {
        UpdateChannel targetChannel = channel ?? CurrentChannel;
        string endpoint = targetChannel == UpdateChannel.Dev
            ? $"{GitHubApiBase}/tags/dev-preview"
            : $"{GitHubApiBase}/latest";

        try {
            using HttpResponseMessage response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (response.IsSuccessStatusCode) {
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string releaseTitle = root.TryGetProperty("name", out JsonElement nameElem) ? (nameElem.GetString() ?? string.Empty) : string.Empty;
                string tagName = root.TryGetProperty("tag_name", out JsonElement tagElem) ? (tagElem.GetString() ?? string.Empty) : string.Empty;
                string changelog = root.TryGetProperty("body", out JsonElement bodyElem) ? (bodyElem.GetString() ?? string.Empty) : string.Empty;

                string latestVersionStr = ExtractVersionFromRelease(releaseTitle, tagName);
                string downloadUrl = string.Empty;
                string checksumUrl = string.Empty;

                if (root.TryGetProperty("assets", out JsonElement assetsElem) && assetsElem.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement asset in assetsElem.EnumerateArray()) {
                        string assetName = asset.TryGetProperty("name", out JsonElement aName) ? (aName.GetString() ?? string.Empty) : string.Empty;
                        string assetDownload = asset.TryGetProperty("browser_download_url", out JsonElement aUrl) ? (aUrl.GetString() ?? string.Empty) : string.Empty;

                        if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
                            if (string.IsNullOrEmpty(downloadUrl) || assetName.Contains(latestVersionStr, StringComparison.OrdinalIgnoreCase)) {
                                downloadUrl = assetDownload;
                            }
                        } else if (assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) {
                            checksumUrl = assetDownload;
                        }
                    }
                }

                string sha256 = string.Empty;
                if (!string.IsNullOrWhiteSpace(checksumUrl)) {
                    try {
                        string checksumContent = await _httpClient.GetStringAsync(checksumUrl, cancellationToken);
                        sha256 = ParseSha256Checksum(checksumContent);
                    } catch { }
                }

                bool isNewer = CompareVersions(latestVersionStr, CurrentVersion) > 0;

                return new UpdateCheckResult {
                    IsUpdateAvailable = isNewer,
                    CurrentVersion = CurrentVersion,
                    LatestVersion = latestVersionStr,
                    DownloadUrl = downloadUrl,
                    Sha256Checksum = sha256,
                    Changelog = changelog,
                    IsMandatory = false
                };
            }
        } catch { }

        // Fallback to static version.json if API rate-limited or unreachable
        return await CheckStaticFallbackAsync(cancellationToken);
    }

    private async Task<UpdateCheckResult> CheckStaticFallbackAsync(CancellationToken cancellationToken) {
        try {
            using HttpResponseMessage response = await _httpClient.GetAsync(FallbackStaticUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return DefaultResult();
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string latestVersionStr = root.TryGetProperty("version", out JsonElement vElem) ? (vElem.GetString() ?? CurrentVersion) : CurrentVersion;
            string downloadUrl = root.TryGetProperty("downloadUrl", out JsonElement dElem) ? (dElem.GetString() ?? string.Empty) : string.Empty;
            string sha256 = root.TryGetProperty("sha256", out JsonElement shaElem) ? (shaElem.GetString() ?? string.Empty) : string.Empty;
            string changelog = root.TryGetProperty("changelog", out JsonElement chElem) ? (chElem.GetString() ?? string.Empty) : string.Empty;
            bool isMandatory = root.TryGetProperty("mandatory", out JsonElement mandElem) && mandElem.GetBoolean();

            bool isNewer = CompareVersions(latestVersionStr, CurrentVersion) > 0;

            return new UpdateCheckResult {
                IsUpdateAvailable = isNewer,
                CurrentVersion = CurrentVersion,
                LatestVersion = latestVersionStr,
                DownloadUrl = downloadUrl,
                Sha256Checksum = sha256,
                Changelog = changelog,
                IsMandatory = isMandatory
            };
        } catch {
            return DefaultResult();
        }
    }

    private UpdateCheckResult DefaultResult() {
        return new UpdateCheckResult {
            IsUpdateAvailable = false,
            CurrentVersion = CurrentVersion,
            LatestVersion = CurrentVersion
        };
    }

    public async Task<string> DownloadUpdateAsync(string downloadUrl, string expectedSha256, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);

        string tempFile = Path.Combine(Path.GetTempPath(), $"LoRAMancer-Update-{Guid.NewGuid():N}.exe");

        using HttpResponseMessage response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream fileStream = new(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        byte[] buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0) {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0) {
                int progress = (int)((double)totalRead / totalBytes.Value * 100);
                OnDownloadProgress?.Invoke(progress);
            }
        }

        await fileStream.FlushAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(expectedSha256)) {
            fileStream.Position = 0;
            using SHA256 sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fileStream, cancellationToken);
            string calculatedHash = Convert.ToHexString(hash);
            if (!string.Equals(calculatedHash, expectedSha256, StringComparison.OrdinalIgnoreCase)) {
                File.Delete(tempFile);
                throw new InvalidOperationException("Downloaded update failed SHA256 checksum verification.");
            }
        }

        return tempFile;
    }

    public void LaunchInstallerAndExit(string installerPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (!File.Exists(installerPath)) {
            throw new FileNotFoundException("Installer executable not found", installerPath);
        }

        Process.Start(new ProcessStartInfo {
            FileName = installerPath,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        });

        Environment.Exit(0);
    }

    private static string ExtractVersionFromRelease(string title, string tag) {
        Match match = DevVersionRegex().Match(title);
        if (match.Success) {
            return match.Groups[1].Value;
        }

        Match vMatch = TitleVersionRegex().Match(title);
        if (vMatch.Success) {
            return vMatch.Groups[1].Value;
        }

        return tag.TrimStart('v', 'V');
    }

    private static string ParseSha256Checksum(string content) {
        if (string.IsNullOrWhiteSpace(content)) {
            return string.Empty;
        }
        string firstLine = content.Split('\n', StringSplitOptions.TrimEntries)[0];
        string[] parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    public static int CompareVersions(string v1, string v2) {
        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase)) {
            return 0;
        }

        string base1 = SplitVersion(v1, out string suffix1);
        string base2 = SplitVersion(v2, out string suffix2);

        if (Version.TryParse(base1, out Version? ver1) && Version.TryParse(base2, out Version? ver2)) {
            int baseComparison = ver1.CompareTo(ver2);
            if (baseComparison != 0) {
                return baseComparison;
            }
        }

        if (suffix1.StartsWith("dev.", StringComparison.OrdinalIgnoreCase) &&
            suffix2.StartsWith("dev.", StringComparison.OrdinalIgnoreCase)) {
            if (int.TryParse(suffix1[4..], out int run1) && int.TryParse(suffix2[4..], out int run2)) {
                return run1.CompareTo(run2);
            }
        }

        if (string.IsNullOrEmpty(suffix1) && !string.IsNullOrEmpty(suffix2)) {
            return 1;
        }
        if (!string.IsNullOrEmpty(suffix1) && string.IsNullOrEmpty(suffix2)) {
            return -1;
        }

        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
    }

    private static string SplitVersion(string fullVersion, out string suffix) {
        int dashIdx = fullVersion.IndexOf('-');
        if (dashIdx >= 0) {
            suffix = fullVersion[(dashIdx + 1)..];
            return fullVersion[..dashIdx];
        }
        suffix = string.Empty;
        return fullVersion;
    }

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex DevVersionRegex();

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)")]
    private static partial Regex TitleVersionRegex();
}
