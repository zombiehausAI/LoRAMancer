using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class AutoUpdateService {
    private readonly HttpClient _httpClient;
    private const string DefaultUpdateUrl = "https://raw.githubusercontent.com/loramancer/loramancer/main/installer/version.json";

    public event Action<int>? OnDownloadProgress;

    public AutoUpdateService(HttpClient? httpClient = null) {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string CurrentVersion {
        get {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Version? version = assembly.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string? updateUrl = null, CancellationToken cancellationToken = default) {
        string url = updateUrl ?? DefaultUpdateUrl;

        try {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return new UpdateCheckResult {
                    IsUpdateAvailable = false,
                    CurrentVersion = CurrentVersion,
                    LatestVersion = CurrentVersion
                };
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string latestVersionStr = root.GetProperty("version").GetString() ?? CurrentVersion;
            string downloadUrl = root.GetProperty("downloadUrl").GetString() ?? string.Empty;
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
            return new UpdateCheckResult {
                IsUpdateAvailable = false,
                CurrentVersion = CurrentVersion,
                LatestVersion = CurrentVersion
            };
        }
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

    private static int CompareVersions(string v1, string v2) {
        if (Version.TryParse(v1, out Version? ver1) && Version.TryParse(v2, out Version? ver2)) {
            return ver1.CompareTo(ver2);
        }
        return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
    }
}
