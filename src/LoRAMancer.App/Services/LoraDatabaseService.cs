using System.Text.Json;
using LoRAMancer.App.Models;
using Microsoft.Data.Sqlite;

namespace LoRAMancer.App.Services;

public sealed class LoraDatabaseService : IDisposable {
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public LoraDatabaseService() {
        string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".LoRAMancer");
        if (!Directory.Exists(appDir)) {
            Directory.CreateDirectory(appDir);
        }
        _dbPath = Path.Combine(appDir, "loras.db");
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task EnsureInitializedAsync() {
        if (_initialized) {
            return;
        }

        await _lock.WaitAsync();
        try {
            if (_initialized) {
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS Loras (
                    FilePath TEXT PRIMARY KEY,
                    FileName TEXT NOT NULL,
                    DirectoryPath TEXT NOT NULL,
                    BaseModel TEXT NOT NULL,
                    UserBaseModel TEXT,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    NetworkDim INTEGER,
                    NetworkAlpha REAL,
                    NetworkModule TEXT,
                    LearningRate REAL,
                    UnetLearningRate REAL,
                    TextEncoderLearningRate REAL,
                    Optimizer TEXT,
                    LrScheduler TEXT,
                    Epochs INTEGER,
                    TotalSteps INTEGER,
                    Resolution TEXT,
                    Precision TEXT,
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    LastModifiedUtc TEXT,
                    ThumbnailPath TEXT,
                    Sha256Hash TEXT,
                    TrainedWordsJson TEXT,
                    RawMetadataJson TEXT,
                    CivitaiModelId INTEGER,
                    CivitaiVersionId INTEGER,
                    CivitaiModelName TEXT,
                    CivitaiVersionName TEXT,
                    CivitaiBaseModel TEXT,
                    CivitaiDescription TEXT,
                    CivitaiDownloadUrl TEXT,
                    CivitaiUrl TEXT,
                    CivitaiPreviewImageUrl TEXT,
                    CivitaiSamplePromptsJson TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_loras_dir ON Loras(DirectoryPath);
                CREATE INDEX IF NOT EXISTS idx_loras_fav ON Loras(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_loras_base ON Loras(BaseModel);
            ";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = createTableSql;
            await cmd.ExecuteNonQueryAsync();

            _initialized = true;
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<LoraMetadata>> GetAllAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Loras ORDER BY FileName COLLATE NOCASE ASC;";

            var list = new List<LoraMetadata>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                list.Add(MapReaderToMetadata(reader));
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, (DateTime LastModified, long Size)>> GetFileSignaturesAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT FilePath, LastModifiedUtc, FileSizeBytes FROM Loras;";

            var dict = new Dictionary<string, (DateTime LastModified, long Size)>(StringComparer.OrdinalIgnoreCase);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                string path = reader.GetString(0);
                string? timeStr = reader.IsDBNull(1) ? null : reader.GetString(1);
                long size = reader.GetInt64(2);
                DateTime time = DateTime.TryParse(timeStr, out var dt) ? dt : DateTime.MinValue;
                dict[path] = (time, size);
            }
            return dict;
        } finally {
            _lock.Release();
        }
    }

    public async Task UpsertBatchAsync(IEnumerable<LoraMetadata> items) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            const string sql = @"
                INSERT INTO Loras (
                    FilePath, FileName, DirectoryPath, BaseModel, UserBaseModel, IsFavorite,
                    NetworkDim, NetworkAlpha, NetworkModule, LearningRate, UnetLearningRate, TextEncoderLearningRate,
                    Optimizer, LrScheduler, Epochs, TotalSteps, Resolution, Precision,
                    FileSizeBytes, LastModifiedUtc, ThumbnailPath, Sha256Hash, TrainedWordsJson, RawMetadataJson,
                    CivitaiModelId, CivitaiVersionId, CivitaiModelName, CivitaiVersionName, CivitaiBaseModel,
                    CivitaiDescription, CivitaiDownloadUrl, CivitaiUrl, CivitaiPreviewImageUrl, CivitaiSamplePromptsJson,
                    CreatedAtUtc, UpdatedAtUtc
                ) VALUES (
                    $FilePath, $FileName, $DirectoryPath, $BaseModel, $UserBaseModel, $IsFavorite,
                    $NetworkDim, $NetworkAlpha, $NetworkModule, $LearningRate, $UnetLearningRate, $TextEncoderLearningRate,
                    $Optimizer, $LrScheduler, $Epochs, $TotalSteps, $Resolution, $Precision,
                    $FileSizeBytes, $LastModifiedUtc, $ThumbnailPath, $Sha256Hash, $TrainedWordsJson, $RawMetadataJson,
                    $CivitaiModelId, $CivitaiVersionId, $CivitaiModelName, $CivitaiVersionName, $CivitaiBaseModel,
                    $CivitaiDescription, $CivitaiDownloadUrl, $CivitaiUrl, $CivitaiPreviewImageUrl, $CivitaiSamplePromptsJson,
                    $CreatedAtUtc, $UpdatedAtUtc
                ) ON CONFLICT(FilePath) DO UPDATE SET
                    FileName = excluded.FileName,
                    DirectoryPath = excluded.DirectoryPath,
                    BaseModel = excluded.BaseModel,
                    UserBaseModel = COALESCE(Loras.UserBaseModel, excluded.UserBaseModel),
                    IsFavorite = Loras.IsFavorite,
                    NetworkDim = excluded.NetworkDim,
                    NetworkAlpha = excluded.NetworkAlpha,
                    NetworkModule = excluded.NetworkModule,
                    LearningRate = excluded.LearningRate,
                    UnetLearningRate = excluded.UnetLearningRate,
                    TextEncoderLearningRate = excluded.TextEncoderLearningRate,
                    Optimizer = excluded.Optimizer,
                    LrScheduler = excluded.LrScheduler,
                    Epochs = excluded.Epochs,
                    TotalSteps = excluded.TotalSteps,
                    Resolution = excluded.Resolution,
                    Precision = excluded.Precision,
                    FileSizeBytes = excluded.FileSizeBytes,
                    LastModifiedUtc = excluded.LastModifiedUtc,
                    ThumbnailPath = COALESCE(excluded.ThumbnailPath, Loras.ThumbnailPath),
                    Sha256Hash = COALESCE(excluded.Sha256Hash, Loras.Sha256Hash),
                    TrainedWordsJson = excluded.TrainedWordsJson,
                    RawMetadataJson = excluded.RawMetadataJson,
                    CivitaiModelId = COALESCE(excluded.CivitaiModelId, Loras.CivitaiModelId),
                    CivitaiVersionId = COALESCE(excluded.CivitaiVersionId, Loras.CivitaiVersionId),
                    CivitaiModelName = COALESCE(excluded.CivitaiModelName, Loras.CivitaiModelName),
                    CivitaiVersionName = COALESCE(excluded.CivitaiVersionName, Loras.CivitaiVersionName),
                    CivitaiBaseModel = COALESCE(excluded.CivitaiBaseModel, Loras.CivitaiBaseModel),
                    CivitaiDescription = COALESCE(excluded.CivitaiDescription, Loras.CivitaiDescription),
                    CivitaiDownloadUrl = COALESCE(excluded.CivitaiDownloadUrl, Loras.CivitaiDownloadUrl),
                    CivitaiUrl = COALESCE(excluded.CivitaiUrl, Loras.CivitaiUrl),
                    CivitaiPreviewImageUrl = COALESCE(excluded.CivitaiPreviewImageUrl, Loras.CivitaiPreviewImageUrl),
                    CivitaiSamplePromptsJson = COALESCE(excluded.CivitaiSamplePromptsJson, Loras.CivitaiSamplePromptsJson),
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
            ";

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;

            var pFilePath = cmd.Parameters.Add("$FilePath", SqliteType.Text);
            var pFileName = cmd.Parameters.Add("$FileName", SqliteType.Text);
            var pDirectoryPath = cmd.Parameters.Add("$DirectoryPath", SqliteType.Text);
            var pBaseModel = cmd.Parameters.Add("$BaseModel", SqliteType.Text);
            var pUserBaseModel = cmd.Parameters.Add("$UserBaseModel", SqliteType.Text);
            var pIsFavorite = cmd.Parameters.Add("$IsFavorite", SqliteType.Integer);
            var pNetworkDim = cmd.Parameters.Add("$NetworkDim", SqliteType.Integer);
            var pNetworkAlpha = cmd.Parameters.Add("$NetworkAlpha", SqliteType.Real);
            var pNetworkModule = cmd.Parameters.Add("$NetworkModule", SqliteType.Text);
            var pLearningRate = cmd.Parameters.Add("$LearningRate", SqliteType.Real);
            var pUnetLearningRate = cmd.Parameters.Add("$UnetLearningRate", SqliteType.Real);
            var pTextEncoderLearningRate = cmd.Parameters.Add("$TextEncoderLearningRate", SqliteType.Real);
            var pOptimizer = cmd.Parameters.Add("$Optimizer", SqliteType.Text);
            var pLrScheduler = cmd.Parameters.Add("$LrScheduler", SqliteType.Text);
            var pEpochs = cmd.Parameters.Add("$Epochs", SqliteType.Integer);
            var pTotalSteps = cmd.Parameters.Add("$TotalSteps", SqliteType.Integer);
            var pResolution = cmd.Parameters.Add("$Resolution", SqliteType.Text);
            var pPrecision = cmd.Parameters.Add("$Precision", SqliteType.Text);
            var pFileSizeBytes = cmd.Parameters.Add("$FileSizeBytes", SqliteType.Integer);
            var pLastModifiedUtc = cmd.Parameters.Add("$LastModifiedUtc", SqliteType.Text);
            var pThumbnailPath = cmd.Parameters.Add("$ThumbnailPath", SqliteType.Text);
            var pSha256Hash = cmd.Parameters.Add("$Sha256Hash", SqliteType.Text);
            var pTrainedWordsJson = cmd.Parameters.Add("$TrainedWordsJson", SqliteType.Text);
            var pRawMetadataJson = cmd.Parameters.Add("$RawMetadataJson", SqliteType.Text);
            var pCivitaiModelId = cmd.Parameters.Add("$CivitaiModelId", SqliteType.Integer);
            var pCivitaiVersionId = cmd.Parameters.Add("$CivitaiVersionId", SqliteType.Integer);
            var pCivitaiModelName = cmd.Parameters.Add("$CivitaiModelName", SqliteType.Text);
            var pCivitaiVersionName = cmd.Parameters.Add("$CivitaiVersionName", SqliteType.Text);
            var pCivitaiBaseModel = cmd.Parameters.Add("$CivitaiBaseModel", SqliteType.Text);
            var pCivitaiDescription = cmd.Parameters.Add("$CivitaiDescription", SqliteType.Text);
            var pCivitaiDownloadUrl = cmd.Parameters.Add("$CivitaiDownloadUrl", SqliteType.Text);
            var pCivitaiUrl = cmd.Parameters.Add("$CivitaiUrl", SqliteType.Text);
            var pCivitaiPreviewImageUrl = cmd.Parameters.Add("$CivitaiPreviewImageUrl", SqliteType.Text);
            var pCivitaiSamplePromptsJson = cmd.Parameters.Add("$CivitaiSamplePromptsJson", SqliteType.Text);
            var pCreatedAtUtc = cmd.Parameters.Add("$CreatedAtUtc", SqliteType.Text);
            var pUpdatedAtUtc = cmd.Parameters.Add("$UpdatedAtUtc", SqliteType.Text);

            string nowStr = DateTime.UtcNow.ToString("O");

            foreach (var meta in items) {
                pFilePath.Value = meta.FilePath;
                pFileName.Value = meta.FileName;
                pDirectoryPath.Value = Path.GetDirectoryName(meta.FilePath) ?? string.Empty;
                pBaseModel.Value = meta.BaseModel;
                pUserBaseModel.Value = (object?)meta.UserBaseModel ?? DBNull.Value;
                pIsFavorite.Value = meta.IsFavorite ? 1 : 0;
                pNetworkDim.Value = (object?)meta.NetworkDim ?? DBNull.Value;
                pNetworkAlpha.Value = (object?)meta.NetworkAlpha ?? DBNull.Value;
                pNetworkModule.Value = (object?)meta.NetworkModule ?? DBNull.Value;
                pLearningRate.Value = (object?)meta.LearningRate ?? DBNull.Value;
                pUnetLearningRate.Value = (object?)meta.UnetLearningRate ?? DBNull.Value;
                pTextEncoderLearningRate.Value = (object?)meta.TextEncoderLearningRate ?? DBNull.Value;
                pOptimizer.Value = (object?)meta.Optimizer ?? DBNull.Value;
                pLrScheduler.Value = (object?)meta.LrScheduler ?? DBNull.Value;
                pEpochs.Value = (object?)meta.Epochs ?? DBNull.Value;
                pTotalSteps.Value = (object?)meta.TotalSteps ?? DBNull.Value;
                pResolution.Value = (object?)meta.Resolution ?? DBNull.Value;
                pPrecision.Value = (object?)meta.Precision ?? DBNull.Value;
                pFileSizeBytes.Value = meta.FileSizeBytes;
                pLastModifiedUtc.Value = (object?)meta.LastModifiedUtc?.ToString("O") ?? DBNull.Value;
                pThumbnailPath.Value = (object?)meta.ThumbnailPath ?? DBNull.Value;
                pSha256Hash.Value = (object?)meta.Sha256Hash ?? DBNull.Value;
                pTrainedWordsJson.Value = meta.TrainedWords != null ? JsonSerializer.Serialize(meta.TrainedWords) : DBNull.Value;
                pRawMetadataJson.Value = meta.RawHeaderMetadata != null ? JsonSerializer.Serialize(meta.RawHeaderMetadata) : DBNull.Value;

                if (meta.CivitaiInfo != null) {
                    pCivitaiModelId.Value = meta.CivitaiInfo.ModelId;
                    pCivitaiVersionId.Value = meta.CivitaiInfo.VersionId;
                    pCivitaiModelName.Value = (object?)meta.CivitaiInfo.ModelName ?? DBNull.Value;
                    pCivitaiVersionName.Value = (object?)meta.CivitaiInfo.VersionName ?? DBNull.Value;
                    pCivitaiBaseModel.Value = (object?)meta.CivitaiInfo.BaseModel ?? DBNull.Value;
                    pCivitaiDescription.Value = (object?)meta.CivitaiInfo.Description ?? DBNull.Value;
                    pCivitaiDownloadUrl.Value = (object?)meta.CivitaiInfo.DownloadUrl ?? DBNull.Value;
                    pCivitaiUrl.Value = (object?)meta.CivitaiInfo.CivitaiUrl ?? DBNull.Value;
                    pCivitaiPreviewImageUrl.Value = (object?)meta.CivitaiInfo.PreviewImageUrl ?? DBNull.Value;
                    pCivitaiSamplePromptsJson.Value = meta.CivitaiInfo.SamplePrompts != null ? JsonSerializer.Serialize(meta.CivitaiInfo.SamplePrompts) : DBNull.Value;
                } else {
                    pCivitaiModelId.Value = DBNull.Value;
                    pCivitaiVersionId.Value = DBNull.Value;
                    pCivitaiModelName.Value = DBNull.Value;
                    pCivitaiVersionName.Value = DBNull.Value;
                    pCivitaiBaseModel.Value = DBNull.Value;
                    pCivitaiDescription.Value = DBNull.Value;
                    pCivitaiDownloadUrl.Value = DBNull.Value;
                    pCivitaiUrl.Value = DBNull.Value;
                    pCivitaiPreviewImageUrl.Value = DBNull.Value;
                    pCivitaiSamplePromptsJson.Value = DBNull.Value;
                }

                pCreatedAtUtc.Value = nowStr;
                pUpdatedAtUtc.Value = nowStr;

                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        } finally {
            _lock.Release();
        }
    }

    public async Task UpsertSingleAsync(LoraMetadata meta) {
        await UpsertBatchAsync(new[] { meta });
    }

    public async Task SetFavoriteAsync(string filePath, bool isFavorite) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET IsFavorite = $IsFavorite, UpdatedAtUtc = $UpdatedAt WHERE FilePath = $FilePath;";
            cmd.Parameters.AddWithValue("$IsFavorite", isFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$UpdatedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task SetUserBaseModelAsync(string filePath, string? userBaseModel) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET UserBaseModel = $UserBaseModel, UpdatedAtUtc = $UpdatedAt WHERE FilePath = $FilePath;";
            cmd.Parameters.AddWithValue("$UserBaseModel", (object?)userBaseModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$UpdatedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string filePath) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Loras WHERE FilePath = $FilePath;";
            cmd.Parameters.AddWithValue("$FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteMissingInFolderAsync(string folderPath, IEnumerable<string> existingPaths) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var existingSet = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
            using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = "SELECT FilePath FROM Loras WHERE DirectoryPath LIKE $Prefix;";
            selectCmd.Parameters.AddWithValue("$Prefix", folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "%");

            var toDelete = new List<string>();
            using (var reader = await selectCmd.ExecuteReaderAsync()) {
                while (await reader.ReadAsync()) {
                    string path = reader.GetString(0);
                    if (!existingSet.Contains(path)) {
                        toDelete.Add(path);
                    }
                }
            }

            if (toDelete.Count > 0) {
                using var trans = connection.BeginTransaction();
                using var delCmd = connection.CreateCommand();
                delCmd.Transaction = trans;
                delCmd.CommandText = "DELETE FROM Loras WHERE FilePath = $FilePath;";
                var p = delCmd.Parameters.Add("$FilePath", SqliteType.Text);
                foreach (string path in toDelete) {
                    p.Value = path;
                    await delCmd.ExecuteNonQueryAsync();
                }
                trans.Commit();
            }
        } finally {
            _lock.Release();
        }
    }

    private static LoraMetadata MapReaderToMetadata(SqliteDataReader reader) {
        var meta = new LoraMetadata {
            FilePath = reader.GetString(reader.GetOrdinal("FilePath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            BaseModel = reader.GetString(reader.GetOrdinal("BaseModel")),
            UserBaseModel = reader.IsDBNull(reader.GetOrdinal("UserBaseModel")) ? null : reader.GetString(reader.GetOrdinal("UserBaseModel")),
            IsFavorite = reader.GetInt32(reader.GetOrdinal("IsFavorite")) == 1,
            FileSizeBytes = reader.GetInt64(reader.GetOrdinal("FileSizeBytes"))
        };

        int dimOrd = reader.GetOrdinal("NetworkDim");
        if (!reader.IsDBNull(dimOrd)) {
            meta.NetworkDim = reader.GetInt32(dimOrd);
        }

        int alphaOrd = reader.GetOrdinal("NetworkAlpha");
        if (!reader.IsDBNull(alphaOrd)) {
            meta.NetworkAlpha = reader.GetDouble(alphaOrd);
        }

        int modOrd = reader.GetOrdinal("NetworkModule");
        if (!reader.IsDBNull(modOrd)) {
            meta.NetworkModule = reader.GetString(modOrd);
        }

        int lrOrd = reader.GetOrdinal("LearningRate");
        if (!reader.IsDBNull(lrOrd)) {
            meta.LearningRate = reader.GetDouble(lrOrd);
        }

        int unetOrd = reader.GetOrdinal("UnetLearningRate");
        if (!reader.IsDBNull(unetOrd)) {
            meta.UnetLearningRate = reader.GetDouble(unetOrd);
        }

        int teOrd = reader.GetOrdinal("TextEncoderLearningRate");
        if (!reader.IsDBNull(teOrd)) {
            meta.TextEncoderLearningRate = reader.GetDouble(teOrd);
        }

        int optOrd = reader.GetOrdinal("Optimizer");
        if (!reader.IsDBNull(optOrd)) {
            meta.Optimizer = reader.GetString(optOrd);
        }

        int lrsOrd = reader.GetOrdinal("LrScheduler");
        if (!reader.IsDBNull(lrsOrd)) {
            meta.LrScheduler = reader.GetString(lrsOrd);
        }

        int epochsOrd = reader.GetOrdinal("Epochs");
        if (!reader.IsDBNull(epochsOrd)) {
            meta.Epochs = reader.GetInt32(epochsOrd);
        }

        int stepsOrd = reader.GetOrdinal("TotalSteps");
        if (!reader.IsDBNull(stepsOrd)) {
            meta.TotalSteps = reader.GetInt32(stepsOrd);
        }

        int resOrd = reader.GetOrdinal("Resolution");
        if (!reader.IsDBNull(resOrd)) {
            meta.Resolution = reader.GetString(resOrd);
        }

        int precOrd = reader.GetOrdinal("Precision");
        if (!reader.IsDBNull(precOrd)) {
            meta.Precision = reader.GetString(precOrd);
        }

        int thumbOrd = reader.GetOrdinal("ThumbnailPath");
        if (!reader.IsDBNull(thumbOrd)) {
            meta.ThumbnailPath = reader.GetString(thumbOrd);
        }

        int hashOrd = reader.GetOrdinal("Sha256Hash");
        if (!reader.IsDBNull(hashOrd)) {
            meta.Sha256Hash = reader.GetString(hashOrd);
        }

        int lmOrd = reader.GetOrdinal("LastModifiedUtc");
        if (!reader.IsDBNull(lmOrd)) {
            string s = reader.GetString(lmOrd);
            if (DateTime.TryParse(s, out var dt)) {
                meta.LastModifiedUtc = dt;
            }
        }

        int twOrd = reader.GetOrdinal("TrainedWordsJson");
        if (!reader.IsDBNull(twOrd)) {
            try {
                meta.TrainedWords = JsonSerializer.Deserialize<List<string>>(reader.GetString(twOrd)) ?? new();
            } catch {
                meta.TrainedWords = new();
            }
        }

        int rmOrd = reader.GetOrdinal("RawMetadataJson");
        if (!reader.IsDBNull(rmOrd)) {
            try {
                meta.RawHeaderMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(rmOrd)) ?? new();
            } catch {
                meta.RawHeaderMetadata = new Dictionary<string, string>();
            }
        }

        int civModelIdOrd = reader.GetOrdinal("CivitaiModelId");
        if (!reader.IsDBNull(civModelIdOrd)) {
            var civ = new CivitaiModelVersionInfo {
                ModelId = reader.GetInt64(civModelIdOrd),
                VersionId = reader.GetInt64(reader.GetOrdinal("CivitaiVersionId")),
                ModelName = reader.IsDBNull(reader.GetOrdinal("CivitaiModelName")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiModelName")),
                VersionName = reader.IsDBNull(reader.GetOrdinal("CivitaiVersionName")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiVersionName")),
                BaseModel = reader.IsDBNull(reader.GetOrdinal("CivitaiBaseModel")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiBaseModel")),
                Description = reader.IsDBNull(reader.GetOrdinal("CivitaiDescription")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiDescription")),
                DownloadUrl = reader.IsDBNull(reader.GetOrdinal("CivitaiDownloadUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiDownloadUrl")),
                CivitaiUrl = reader.IsDBNull(reader.GetOrdinal("CivitaiUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiUrl")),
                PreviewImageUrl = reader.IsDBNull(reader.GetOrdinal("CivitaiPreviewImageUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("CivitaiPreviewImageUrl"))
            };

            int civPromptsOrd = reader.GetOrdinal("CivitaiSamplePromptsJson");
            if (!reader.IsDBNull(civPromptsOrd)) {
                try {
                    civ.SamplePrompts = JsonSerializer.Deserialize<List<string>>(reader.GetString(civPromptsOrd)) ?? new();
                } catch {
                    civ.SamplePrompts = new();
                }
            }
            meta.CivitaiInfo = civ;
        }

        return meta;
    }

    public void Dispose() {
        _lock.Dispose();
    }
}
