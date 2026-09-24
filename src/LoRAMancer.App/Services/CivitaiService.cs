using System.Security.Cryptography;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class CivitaiService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public CivitaiService(SettingsService settingsService, HttpClient? httpClient = null) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string> ComputeFileSha256Async(string filePath, Action<int>? onProgress = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) {
            throw new FileNotFoundException("Model file not found", filePath);
        }

        FileInfo fileInfo = new(filePath);
        long totalBytes = fileInfo.Length;

        await using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[65536];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0) {
            hasher.AppendData(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalBytes > 0 && onProgress != null) {
                int percent = (int)((double)totalRead / totalBytes * 100);
                onProgress(percent);
            }
        }

        byte[] hashBytes = hasher.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToUpperInvariant();
    }

    public async Task<CivitaiModelVersionInfo?> LookupByHashAsync(string hash, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        string url = $"https://civitai.com/api/v1/model-versions/by-hash/{hash}";
        using HttpRequestMessage request = new(HttpMethod.Get, url);

        string apiKey = _settingsService.Current.CivitaiApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey)) {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        try {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            long modelId = root.TryGetProperty("modelId", out JsonElement mIdElem) ? mIdElem.GetInt64() : 0;
            long versionId = root.TryGetProperty("id", out JsonElement vIdElem) ? vIdElem.GetInt64() : 0;
            string versionName = root.TryGetProperty("name", out JsonElement vnElem) ? (vnElem.GetString() ?? string.Empty) : string.Empty;
            string baseModel = root.TryGetProperty("baseModel", out JsonElement bmElem) ? (bmElem.GetString() ?? string.Empty) : string.Empty;
            string description = root.TryGetProperty("description", out JsonElement descElem) ? (descElem.GetString() ?? string.Empty) : string.Empty;
            string downloadUrl = root.TryGetProperty("downloadUrl", out JsonElement dlElem) ? (dlElem.GetString() ?? string.Empty) : string.Empty;

            string modelName = string.Empty;
            if (root.TryGetProperty("model", out JsonElement modelElem) && modelElem.TryGetProperty("name", out JsonElement mnElem)) {
                modelName = mnElem.GetString() ?? string.Empty;
            }

            List<string> trainedWords = new();
            if (root.TryGetProperty("trainedWords", out JsonElement twElem) && twElem.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement word in twElem.EnumerateArray()) {
                    string? w = word.GetString();
                    if (!string.IsNullOrWhiteSpace(w)) {
                        trainedWords.Add(w);
                    }
                }
            }

            string previewImage = string.Empty;
            List<string> samplePrompts = new();
            if (root.TryGetProperty("images", out JsonElement imgArr) && imgArr.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement img in imgArr.EnumerateArray()) {
                    if (string.IsNullOrEmpty(previewImage) && img.TryGetProperty("url", out JsonElement urlElem)) {
                        previewImage = urlElem.GetString() ?? string.Empty;
                    }
                    if (img.TryGetProperty("meta", out JsonElement metaElem) && metaElem.TryGetProperty("prompt", out JsonElement pElem)) {
                        string? prompt = pElem.GetString();
                        if (!string.IsNullOrWhiteSpace(prompt)) {
                            samplePrompts.Add(prompt);
                        }
                    }
                }
            }

            return new CivitaiModelVersionInfo {
                ModelId = modelId,
                VersionId = versionId,
                ModelName = modelName,
                VersionName = versionName,
                BaseModel = baseModel,
                TrainedWords = trainedWords,
                PreviewImageUrl = previewImage,
                DownloadUrl = downloadUrl,
                CivitaiUrl = modelId > 0 ? $"https://civitai.com/models/{modelId}" : string.Empty,
                Description = description,
                SamplePrompts = samplePrompts
            };
        } catch {
            return null;
        }
    }
}
