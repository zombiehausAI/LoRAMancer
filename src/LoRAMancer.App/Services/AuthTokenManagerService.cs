using System.Security.Cryptography;
using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class AuthTokenManagerService {
    private readonly SettingsService _settingsService;
    private readonly string _tokensFilePath;
    private readonly object _lock = new();
    private readonly List<AuthToken> _tokens = new();

    public event Action? OnTokensChanged;

    public AuthTokenManagerService(SettingsService settingsService, string? customStoragePath = null) {
        _settingsService = settingsService;

        if (!string.IsNullOrWhiteSpace(customStoragePath)) {
            _tokensFilePath = customStoragePath;
        } else {
            string dir = _settingsService.SettingsDirectory;
            if (string.IsNullOrWhiteSpace(dir)) {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer");
            }
            if (!Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
            }
            _tokensFilePath = Path.Combine(dir, "auth_tokens.json");
        }

        LoadTokens();
    }

    public IReadOnlyList<AuthToken> GetAllTokens() {
        lock (_lock) {
            return _tokens.OrderByDescending(t => t.CreatedAt).ToList();
        }
    }

    public AuthToken? GetTokenById(string id) {
        lock (_lock) {
            return _tokens.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public AuthToken? GetTokenByValue(string tokenValue) {
        if (string.IsNullOrWhiteSpace(tokenValue)) {
            return null;
        }

        lock (_lock) {
            return _tokens.FirstOrDefault(t => string.Equals(t.Token, tokenValue.Trim(), StringComparison.Ordinal));
        }
    }

    public AuthToken CreateToken(string name, string? customToken = null, string role = "Admin", string description = "", DateTimeOffset? expiresAt = null) {
        lock (_lock) {
            string tokenStr = !string.IsNullOrWhiteSpace(customToken)
                ? customToken.Trim()
                : GenerateSecureToken();

            // Verify unique token string
            if (_tokens.Any(t => string.Equals(t.Token, tokenStr, StringComparison.Ordinal))) {
                throw new InvalidOperationException("An authentication token with this secret value already exists.");
            }

            AuthToken newToken = new() {
                Id = Guid.NewGuid().ToString("N"),
                Token = tokenStr,
                Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Token" : name.Trim(),
                Description = description?.Trim() ?? string.Empty,
                Role = string.IsNullOrWhiteSpace(role) ? "Admin" : role.Trim(),
                IsBlocked = false,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
                UsageCount = 0
            };

            _tokens.Add(newToken);
            SaveTokensInternal();

            OnTokensChanged?.Invoke();
            return newToken;
        }
    }

    public bool UpdateToken(string id, string? name = null, string? description = null, string? role = null, DateTimeOffset? expiresAt = null) {
        lock (_lock) {
            AuthToken? token = _tokens.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (token == null) {
                return false;
            }

            if (name != null) {
                token.Name = string.IsNullOrWhiteSpace(name) ? token.Name : name.Trim();
            }
            if (description != null) {
                token.Description = description.Trim();
            }
            if (role != null) {
                token.Role = string.IsNullOrWhiteSpace(role) ? token.Role : role.Trim();
            }
            if (expiresAt != null) {
                token.ExpiresAt = expiresAt;
            }

            SaveTokensInternal();
            OnTokensChanged?.Invoke();
            return true;
        }
    }

    public bool BlockToken(string idOrToken) {
        lock (_lock) {
            AuthToken? token = _tokens.FirstOrDefault(t =>
                string.Equals(t.Id, idOrToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Token, idOrToken, StringComparison.Ordinal));

            if (token == null) {
                return false;
            }

            token.IsBlocked = true;
            SaveTokensInternal();
            OnTokensChanged?.Invoke();
            return true;
        }
    }

    public bool UnblockToken(string idOrToken) {
        lock (_lock) {
            AuthToken? token = _tokens.FirstOrDefault(t =>
                string.Equals(t.Id, idOrToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Token, idOrToken, StringComparison.Ordinal));

            if (token == null) {
                return false;
            }

            token.IsBlocked = false;
            SaveTokensInternal();
            OnTokensChanged?.Invoke();
            return true;
        }
    }

    public bool DeleteToken(string idOrToken) {
        lock (_lock) {
            AuthToken? token = _tokens.FirstOrDefault(t =>
                string.Equals(t.Id, idOrToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Token, idOrToken, StringComparison.Ordinal));

            if (token == null) {
                return false;
            }

            _tokens.Remove(token);
            SaveTokensInternal();
            OnTokensChanged?.Invoke();
            return true;
        }
    }

    public bool ValidateToken(string? tokenValue, out AuthToken? matchedToken) {
        matchedToken = null;
        if (string.IsNullOrWhiteSpace(tokenValue)) {
            return false;
        }

        string trimmed = tokenValue.Trim();

        lock (_lock) {
            AuthToken? token = _tokens.FirstOrDefault(t => string.Equals(t.Token, trimmed, StringComparison.Ordinal));
            if (token == null) {
                // Check if matches legacy single server access token
                string legacyToken = _settingsService.Current.ServerAccessToken;
                if (!string.IsNullOrWhiteSpace(legacyToken) && string.Equals(trimmed, legacyToken.Trim(), StringComparison.Ordinal)) {
                    matchedToken = new AuthToken {
                        Id = "legacy-server-token",
                        Token = legacyToken,
                        Name = "Primary Server Token",
                        Role = "Admin",
                        IsBlocked = false
                    };
                    return true;
                }
                return false;
            }

            if (!token.IsActive) {
                return false;
            }

            // Record usage
            token.LastUsedAt = DateTimeOffset.UtcNow;
            token.UsageCount++;
            SaveTokensInternal();

            matchedToken = token;
            return true;
        }
    }

    public static string GenerateSecureToken(string prefix = "lrm_") {
        byte[] bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void LoadTokens() {
        lock (_lock) {
            _tokens.Clear();

            if (File.Exists(_tokensFilePath)) {
                try {
                    string json = File.ReadAllText(_tokensFilePath);
                    List<AuthToken>? loaded = JsonSerializer.Deserialize<List<AuthToken>>(json, new JsonSerializerOptions {
                        PropertyNameCaseInsensitive = true
                    });

                    if (loaded != null && loaded.Count > 0) {
                        _tokens.AddRange(loaded);
                        return;
                    }
                } catch {
                    // Ignore parse errors on load
                }
            }

            // Seed initial token from ServerAccessToken if available
            string serverToken = _settingsService.Current.ServerAccessToken;
            if (!string.IsNullOrWhiteSpace(serverToken)) {
                _tokens.Add(new AuthToken {
                    Id = Guid.NewGuid().ToString("N"),
                    Token = serverToken.Trim(),
                    Name = "Primary Admin Token",
                    Description = "Migrated automatically from initial ServerAccessToken configuration",
                    Role = "Admin",
                    IsBlocked = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UsageCount = 0
                });
                SaveTokensInternal();
            }
        }
    }

    private void SaveTokensInternal() {
        try {
            string? dir = Path.GetDirectoryName(_tokensFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(_tokens, new JsonSerializerOptions {
                WriteIndented = true
            });
            File.WriteAllText(_tokensFilePath, json);
        } catch {
            // Ignore persistence errors
        }
    }
}
