using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class AuthTokenManagerServiceTests {
    [Fact]
    public void CreateToken_GeneratesSecurePrefixAndStoresInPlainText() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);

            AuthToken token = manager.CreateToken("Test Client", role: "Trainer", description: "Worker Node 1");

            Assert.NotNull(token);
            Assert.StartsWith("lrm_", token.Token);
            Assert.Equal("Test Client", token.Name);
            Assert.Equal("Trainer", token.Role);
            Assert.Equal("Worker Node 1", token.Description);
            Assert.False(token.IsBlocked);
            Assert.True(token.IsActive);
            Assert.Equal(0, token.UsageCount);

            // Verify admin can see plain text token in full list
            var all = manager.GetAllTokens();
            Assert.Contains(all, t => t.Token == token.Token && !string.IsNullOrWhiteSpace(t.Token));
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void CreateToken_SupportsCustomTokenString() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);

            AuthToken token = manager.CreateToken("Custom Token Test", customToken: "my_custom_secret_key_999", role: "Admin");

            Assert.Equal("my_custom_secret_key_999", token.Token);
            Assert.True(manager.ValidateToken("my_custom_secret_key_999", out var matched));
            Assert.NotNull(matched);
            Assert.Equal("Custom Token Test", matched.Name);
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void Persistence_LoadsTokensAcrossInstances() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager1 = new(settings, tempFile);
            AuthToken t1 = manager1.CreateToken("Laptop Token", customToken: "persistent_secret_123");

            AuthTokenManagerService manager2 = new(settings, tempFile);
            var reloadedTokens = manager2.GetAllTokens();

            Assert.Single(reloadedTokens);
            Assert.Equal("persistent_secret_123", reloadedTokens[0].Token);
            Assert.Equal("Laptop Token", reloadedTokens[0].Name);
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void BlockAndUnblock_TogglesTokenValidity() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);
            AuthToken token = manager.CreateToken("Revocable Token", customToken: "token_to_block");

            // Initially valid
            Assert.True(manager.ValidateToken("token_to_block", out _));

            // Block token
            bool blocked = manager.BlockToken(token.Id);
            Assert.True(blocked);
            Assert.False(manager.ValidateToken("token_to_block", out _));

            // Unblock token
            bool unblocked = manager.UnblockToken(token.Id);
            Assert.True(unblocked);
            Assert.True(manager.ValidateToken("token_to_block", out _));
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void DeleteToken_RemovesPermanently() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);
            AuthToken token = manager.CreateToken("Temporary Token", customToken: "token_to_delete");

            Assert.Single(manager.GetAllTokens());

            bool deleted = manager.DeleteToken(token.Id);
            Assert.True(deleted);
            Assert.Empty(manager.GetAllTokens());
            Assert.False(manager.ValidateToken("token_to_delete", out _));
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void ValidateToken_RecordsUsageCountAndLastUsedTimestamp() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);
            AuthToken token = manager.CreateToken("Usage Tracker", customToken: "tracking_secret");

            Assert.Equal(0, token.UsageCount);
            Assert.Null(token.LastUsedAt);

            bool valid1 = manager.ValidateToken("tracking_secret", out var matched1);
            Assert.True(valid1);
            Assert.Equal(1, matched1!.UsageCount);
            Assert.NotNull(matched1.LastUsedAt);

            bool valid2 = manager.ValidateToken("tracking_secret", out var matched2);
            Assert.True(valid2);
            Assert.Equal(2, matched2!.UsageCount);
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void ValidateToken_RejectsExpiredTokens() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);

            AuthToken token = manager.CreateToken(
                "Expired Token",
                customToken: "expired_secret",
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)
            );

            Assert.True(token.IsExpired);
            Assert.False(token.IsActive);
            Assert.False(manager.ValidateToken("expired_secret", out _));
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }

    [Fact]
    public void ValidateToken_FallsBackToLegacyServerAccessToken() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"tokens_{Guid.NewGuid():N}.json");
        string tempSettings = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");

        try {
            File.WriteAllText(tempSettings, "{\"ServerAccessToken\": \"legacy_pin_4455\"}");
            SettingsService settings = new(null, tempSettings);
            AuthTokenManagerService manager = new(settings, tempFile);

            // Legacy token validates correctly
            Assert.True(manager.ValidateToken("legacy_pin_4455", out var matched));
            Assert.NotNull(matched);
            Assert.Equal("Primary Admin Token", matched.Name);
        } finally {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(tempSettings)) File.Delete(tempSettings);
        }
    }
}
