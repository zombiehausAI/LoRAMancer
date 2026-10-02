using System.Data.Common;
using System.Text.Json;
using LoRAMancer.App.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace LoRAMancer.App.Services;

public sealed class LoraDatabaseService : IDisposable {
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SettingsService? _settingsService;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public bool IsPostgreSql => _settingsService != null && 
        string.Equals(_settingsService.Current.DatabaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    private static void AddParam(DbCommand cmd, string name, object? value) {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public string DatabasePath => _dbPath;

    public LoraDatabaseService(SettingsService? settingsService = null) {
        _settingsService = settingsService;
        string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".LoRAMancer");
        if (!Directory.Exists(appDir)) {
            Directory.CreateDirectory(appDir);
        }
        _dbPath = Path.Combine(appDir, "loramancer_studio.db");

        // Automatic Migration: detect legacy loras.db or lora.db and migrate to loramancer_studio.db
        string legacyLorasDb = Path.Combine(appDir, "loras.db");
        string legacyLoraDb = Path.Combine(appDir, "lora.db");
        string? legacySource = File.Exists(legacyLorasDb) ? legacyLorasDb : (File.Exists(legacyLoraDb) ? legacyLoraDb : null);

        if (!File.Exists(_dbPath) && legacySource != null) {
            try {
                File.Copy(legacySource, _dbPath, overwrite: false);
                if (File.Exists(legacySource + "-wal")) {
                    File.Copy(legacySource + "-wal", _dbPath + "-wal", overwrite: true);
                }
                if (File.Exists(legacySource + "-shm")) {
                    File.Copy(legacySource + "-shm", _dbPath + "-shm", overwrite: true);
                }
                try {
                    File.Delete(legacySource);
                    if (File.Exists(legacySource + "-wal")) {
                        File.Delete(legacySource + "-wal");
                    }
                    if (File.Exists(legacySource + "-shm")) {
                        File.Delete(legacySource + "-shm");
                    }
                } catch {
                    try {
                        File.Move(legacySource, legacySource + ".bak", overwrite: true);
                    } catch { }
                }
            } catch {
                // If migration fails, proceed to clean database initialization
            }
        }

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

            if (IsPostgreSql) {
                try {
                    var pgConfig = GetCurrentPostgreSqlConfig();
                    await EnsurePostgreSqlDatabaseAndSchemaAsync(pgConfig);
                    _initialized = true;
                    return;
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"[PostgreSQL] Schema initialization failed: {ex.Message}. Falling back to SQLite.");
                }
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using (var walCmd = connection.CreateCommand()) {
                walCmd.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA busy_timeout = 5000;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA temp_store = MEMORY;
                    PRAGMA cache_size = -64000;
                    PRAGMA mmap_size = 268435456;
                    PRAGMA page_size = 4096;
                ";
                await walCmd.ExecuteNonQueryAsync();
            }

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
                    LibraryId TEXT,
                    Category TEXT,
                    TagsJson TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_loras_dir ON Loras(DirectoryPath);
                CREATE INDEX IF NOT EXISTS idx_loras_fav ON Loras(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_loras_base ON Loras(BaseModel);
                CREATE INDEX IF NOT EXISTS idx_loras_lib ON Loras(LibraryId);
                CREATE INDEX IF NOT EXISTS idx_loras_cat ON Loras(Category);

                CREATE TABLE IF NOT EXISTS Libraries (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    FolderPath TEXT NOT NULL,
                    Description TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_libraries_path ON Libraries(FolderPath);
                CREATE TABLE IF NOT EXISTS Categories (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    Color TEXT NOT NULL,
                    Icon TEXT,
                    Description TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_categories_name ON Categories(Name);

                CREATE TABLE IF NOT EXISTS Collections (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL COLLATE NOCASE,
                    Description TEXT,
                    Color TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_collections_name ON Collections(Name);

                CREATE TABLE IF NOT EXISTS CollectionItems (
                    CollectionId TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    AddedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (CollectionId, FilePath)
                );
                CREATE INDEX IF NOT EXISTS idx_collection_items_path ON CollectionItems(FilePath);
                CREATE INDEX IF NOT EXISTS idx_collection_items_col ON CollectionItems(CollectionId);

                CREATE TABLE IF NOT EXISTS GalleryMedia (
                    FilePath TEXT PRIMARY KEY,
                    FileName TEXT NOT NULL,
                    FileExtension TEXT NOT NULL,
                    MediaType INTEGER NOT NULL DEFAULT 0,
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    FormattedSize TEXT,
                    CreatedDate TEXT NOT NULL,
                    FolderPath TEXT NOT NULL,
                    FolderName TEXT,
                    FileUrl TEXT,
                    IsAiGenerated INTEGER NOT NULL DEFAULT 0,
                    AiGenerator TEXT,
                    Prompt TEXT,
                    NegativePrompt TEXT,
                    Seed INTEGER,
                    Steps INTEGER,
                    Sampler TEXT,
                    Scheduler TEXT,
                    CfgScale REAL,
                    ModelName TEXT,
                    UsedLorasJson TEXT,
                    Width INTEGER,
                    Height INTEGER,
                    DurationSeconds REAL,
                    RawMetadataJson TEXT,
                    Category TEXT,
                    UserTagsJson TEXT,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    UserNotes TEXT,
                    AssociatedLoraNamesJson TEXT,
                    AssociatedLoraFilePathsJson TEXT,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_gallery_folder ON GalleryMedia(FolderPath);
                CREATE INDEX IF NOT EXISTS idx_gallery_fav ON GalleryMedia(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_gallery_ai ON GalleryMedia(IsAiGenerated);
                CREATE INDEX IF NOT EXISTS idx_gallery_created ON GalleryMedia(CreatedDate);
            ";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = createTableSql;
            await cmd.ExecuteNonQueryAsync();

            // Seed default categories if empty
            using (var countCmd = connection.CreateCommand()) {
                countCmd.CommandText = "SELECT COUNT(*) FROM Categories;";
                long catCount = 0;
                var countObj = await countCmd.ExecuteScalarAsync();
                if (countObj != null && countObj != DBNull.Value) {
                    catCount = Convert.ToInt64(countObj);
                }
                if (catCount == 0) {
                    foreach (var defCat in LoraCategory.GetDefaultCategories()) {
                        using var insCmd = connection.CreateCommand();
                        insCmd.CommandText = @"
                            INSERT OR IGNORE INTO Categories (Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc)
                            VALUES ($Id, $Name, $Color, $Icon, $Description, $CreatedAtUtc, $UpdatedAtUtc);
                        ";
                        insCmd.Parameters.AddWithValue("$Id", defCat.Id);
                        insCmd.Parameters.AddWithValue("$Name", defCat.Name);
                        insCmd.Parameters.AddWithValue("$Color", defCat.Color);
                        insCmd.Parameters.AddWithValue("$Icon", (object?)defCat.Icon ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$Description", (object?)defCat.Description ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("$CreatedAtUtc", defCat.CreatedAtUtc.ToString("O"));
                        insCmd.Parameters.AddWithValue("$UpdatedAtUtc", defCat.UpdatedAtUtc.ToString("O"));
                        await insCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            // Migrate Loras table to add LibraryId, Category, and TagsJson columns if not yet present
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info(Loras);";
            bool hasLibraryId = false;
            bool hasCategory = false;
            bool hasTagsJson = false;
            using (var reader = await pragmaCmd.ExecuteReaderAsync()) {
                while (await reader.ReadAsync()) {
                    string col = reader.GetString(1);
                    if (string.Equals(col, "LibraryId", StringComparison.OrdinalIgnoreCase)) hasLibraryId = true;
                    if (string.Equals(col, "Category", StringComparison.OrdinalIgnoreCase)) hasCategory = true;
                    if (string.Equals(col, "TagsJson", StringComparison.OrdinalIgnoreCase)) hasTagsJson = true;
                }
            }
            if (!hasLibraryId) {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Loras ADD COLUMN LibraryId TEXT;";
                await alterCmd.ExecuteNonQueryAsync();
            }
            if (!hasCategory) {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Loras ADD COLUMN Category TEXT;";
                await alterCmd.ExecuteNonQueryAsync();
            }
            if (!hasTagsJson) {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE Loras ADD COLUMN TagsJson TEXT;";
                await alterCmd.ExecuteNonQueryAsync();
            }

            using (var idxCmd = connection.CreateCommand()) {
                idxCmd.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_loras_lib ON Loras(LibraryId);
                    CREATE INDEX IF NOT EXISTS idx_loras_cat ON Loras(Category);
                    CREATE INDEX IF NOT EXISTS idx_loras_composite ON Loras(LibraryId, DirectoryPath, IsFavorite, BaseModel);
                ";
                await idxCmd.ExecuteNonQueryAsync();
            }

            _initialized = true;
        } finally {
            _lock.Release();
        }
    }

    private async Task<DbConnection> OpenConnectionAsync() {
        if (IsPostgreSql) {
            try {
                var config = GetCurrentPostgreSqlConfig();
                var conn = new NpgsqlConnection(config.BuildConnectionString());
                await conn.OpenAsync();
                return conn;
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine($"[PostgreSQL] Connection failed: {ex.Message}. Falling back to SQLite.");
            }
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;
        ";
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    public async Task<List<LoraMetadata>> GetAllAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Loras ORDER BY LOWER(FileName) ASC;";

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

    public async Task<List<LoraLibrary>> GetLibrariesAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT l.Id, l.Name, l.FolderPath, l.Description, l.CreatedAtUtc, l.UpdatedAtUtc,
                       COUNT(DISTINCT m.FilePath) as ModelCount
                FROM Libraries l
                LEFT JOIN Loras m ON (
                    m.LibraryId = l.Id 
                    OR (
                        m.DirectoryPath IS NOT NULL 
                        AND l.FolderPath IS NOT NULL 
                        AND (
                            REPLACE(RTRIM(m.DirectoryPath, '/\'), '\', '/') = REPLACE(RTRIM(l.FolderPath, '/\'), '\', '/')
                            OR REPLACE(RTRIM(m.DirectoryPath, '/\'), '\', '/') LIKE REPLACE(RTRIM(l.FolderPath, '/\'), '\', '/') || '/%'
                        )
                    )
                )
                GROUP BY l.Id, l.Name, l.FolderPath, l.Description, l.CreatedAtUtc, l.UpdatedAtUtc
                ORDER BY LOWER(l.Name) ASC;
            ";

            var list = new List<LoraLibrary>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                var lib = new LoraLibrary {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    FolderPath = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAtUtc = !reader.IsDBNull(4) && DateTime.TryParse(reader.GetString(4), out var cat) ? cat : DateTime.UtcNow,
                    UpdatedAtUtc = !reader.IsDBNull(5) && DateTime.TryParse(reader.GetString(5), out var uat) ? uat : DateTime.UtcNow,
                    ModelCount = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6))
                };
                list.Add(lib);
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task<LoraLibrary?> GetLibraryAsync(string id) {
        var libs = await GetLibrariesAsync();
        return libs.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task UpsertLibraryAsync(LoraLibrary library) {
        ArgumentNullException.ThrowIfNull(library);
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            if (connection is NpgsqlConnection) {
                cmd.CommandText = @"
                    INSERT INTO Libraries (
                        Id, Name, FolderPath, Description, CreatedAtUtc, UpdatedAtUtc
                    ) VALUES (
                        @Id, @Name, @FolderPath, @Description, @CreatedAtUtc, @UpdatedAtUtc
                    ) ON CONFLICT (Id) DO UPDATE SET
                        Name = EXCLUDED.Name,
                        FolderPath = EXCLUDED.FolderPath,
                        Description = EXCLUDED.Description,
                        UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
                ";
            } else {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO Libraries (
                        Id, Name, FolderPath, Description, CreatedAtUtc, UpdatedAtUtc
                    ) VALUES (
                        @Id, @Name, @FolderPath, @Description, @CreatedAtUtc, @UpdatedAtUtc
                    );
                ";
            }
            AddParam(cmd, "@Id", library.Id);
            AddParam(cmd, "@Name", library.Name);
            AddParam(cmd, "@FolderPath", library.FolderPath);
            AddParam(cmd, "@Description", (object?)library.Description ?? DBNull.Value);
            AddParam(cmd, "@CreatedAtUtc", library.CreatedAtUtc.ToString("O"));
            AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteLibraryAsync(string id) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Libraries WHERE Id = @Id;";
            AddParam(cmd, "@Id", id);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<LoraCategory>> GetCategoriesAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Categories ORDER BY LOWER(Name) ASC;";
            var list = new List<LoraCategory>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                list.Add(new LoraCategory {
                    Id = reader.GetString(reader.GetOrdinal("Id")),
                    Name = reader.GetString(reader.GetOrdinal("Name")),
                    Color = reader.IsDBNull(reader.GetOrdinal("Color")) ? "#89b4fa" : reader.GetString(reader.GetOrdinal("Color")),
                    Icon = reader.IsDBNull(reader.GetOrdinal("Icon")) ? null : reader.GetString(reader.GetOrdinal("Icon")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    CreatedAtUtc = !reader.IsDBNull(reader.GetOrdinal("CreatedAtUtc")) && DateTime.TryParse(reader.GetString(reader.GetOrdinal("CreatedAtUtc")), out var c) ? c : DateTime.UtcNow,
                    UpdatedAtUtc = !reader.IsDBNull(reader.GetOrdinal("UpdatedAtUtc")) && DateTime.TryParse(reader.GetString(reader.GetOrdinal("UpdatedAtUtc")), out var u) ? u : DateTime.UtcNow
                });
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task SaveCategoryAsync(LoraCategory cat) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            if (connection is NpgsqlConnection) {
                cmd.CommandText = @"
                    INSERT INTO Categories (Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Id, @Name, @Color, @Icon, @Description, @CreatedAtUtc, @UpdatedAtUtc)
                    ON CONFLICT (Id) DO UPDATE SET
                        Name = EXCLUDED.Name,
                        Color = EXCLUDED.Color,
                        Icon = EXCLUDED.Icon,
                        Description = EXCLUDED.Description,
                        UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
                ";
            } else {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO Categories (Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Id, @Name, @Color, @Icon, @Description, @CreatedAtUtc, @UpdatedAtUtc);
                ";
            }
            AddParam(cmd, "@Id", cat.Id);
            AddParam(cmd, "@Name", cat.Name);
            AddParam(cmd, "@Color", string.IsNullOrWhiteSpace(cat.Color) ? "#89b4fa" : cat.Color);
            AddParam(cmd, "@Icon", (object?)cat.Icon ?? DBNull.Value);
            AddParam(cmd, "@Description", (object?)cat.Description ?? DBNull.Value);
            AddParam(cmd, "@CreatedAtUtc", cat.CreatedAtUtc.ToString("O"));
            AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteCategoryAsync(string id, string? reassignTo = null) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var trans = connection.BeginTransaction();

            string? catName = null;
            using (var selectCmd = connection.CreateCommand()) {
                selectCmd.Transaction = trans;
                selectCmd.CommandText = "SELECT Name FROM Categories WHERE Id = @Id;";
                AddParam(selectCmd, "@Id", id);
                var obj = await selectCmd.ExecuteScalarAsync();
                if (obj != null && obj != DBNull.Value) {
                    catName = obj.ToString();
                }
            }

            if (!string.IsNullOrWhiteSpace(catName)) {
                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = trans;
                updateCmd.CommandText = "UPDATE Loras SET Category = @Reassign WHERE LOWER(Category) = LOWER(@OldName);";
                AddParam(updateCmd, "@Reassign", (object?)reassignTo ?? DBNull.Value);
                AddParam(updateCmd, "@OldName", catName);
                await updateCmd.ExecuteNonQueryAsync();
            }

            using (var delCmd = connection.CreateCommand()) {
                delCmd.Transaction = trans;
                delCmd.CommandText = "DELETE FROM Categories WHERE Id = @Id;";
                AddParam(delCmd, "@Id", id);
                await delCmd.ExecuteNonQueryAsync();
            }

            trans.Commit();
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<LoraCollection>> GetCollectionsAsync() {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT c.Id, c.Name, c.Description, c.Color, c.CreatedAtUtc, c.UpdatedAtUtc,
                       COUNT(ci.FilePath) as ModelCount
                FROM Collections c
                LEFT JOIN CollectionItems ci ON ci.CollectionId = c.Id
                GROUP BY c.Id, c.Name, c.Description, c.Color, c.CreatedAtUtc, c.UpdatedAtUtc
                ORDER BY LOWER(c.Name) ASC;
            ";
            var list = new List<LoraCollection>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                list.Add(new LoraCollection {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Color = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAtUtc = !reader.IsDBNull(4) && DateTime.TryParse(reader.GetString(4), out var cat) ? cat : DateTime.UtcNow,
                    UpdatedAtUtc = !reader.IsDBNull(5) && DateTime.TryParse(reader.GetString(5), out var uat) ? uat : DateTime.UtcNow,
                    ModelCount = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6))
                });
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task UpsertCollectionAsync(LoraCollection collection) {
        ArgumentNullException.ThrowIfNull(collection);
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            if (connection is NpgsqlConnection) {
                cmd.CommandText = @"
                    INSERT INTO Collections (Id, Name, Description, Color, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Id, @Name, @Description, @Color, @CreatedAtUtc, @UpdatedAtUtc)
                    ON CONFLICT (Id) DO UPDATE SET
                        Name = EXCLUDED.Name,
                        Description = EXCLUDED.Description,
                        Color = EXCLUDED.Color,
                        UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
                ";
            } else {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO Collections (Id, Name, Description, Color, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Id, @Name, @Description, @Color, @CreatedAtUtc, @UpdatedAtUtc);
                ";
            }
            AddParam(cmd, "@Id", collection.Id);
            AddParam(cmd, "@Name", collection.Name);
            AddParam(cmd, "@Description", (object?)collection.Description ?? DBNull.Value);
            AddParam(cmd, "@Color", (object?)collection.Color ?? DBNull.Value);
            AddParam(cmd, "@CreatedAtUtc", collection.CreatedAtUtc.ToString("O"));
            AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteCollectionAsync(string id) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var trans = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = @"
                DELETE FROM CollectionItems WHERE CollectionId = @Id;
                DELETE FROM Collections WHERE Id = @Id;
            ";
            AddParam(cmd, "@Id", id);
            await cmd.ExecuteNonQueryAsync();
            trans.Commit();
        } finally {
            _lock.Release();
        }
    }

    public async Task AddToCollectionAsync(string collectionId, string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            if (connection is NpgsqlConnection) {
                cmd.CommandText = @"
                    INSERT INTO CollectionItems (CollectionId, FilePath, AddedAtUtc)
                    VALUES (@CollectionId, @FilePath, @AddedAtUtc)
                    ON CONFLICT (CollectionId, FilePath) DO NOTHING;
                ";
            } else {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO CollectionItems (CollectionId, FilePath, AddedAtUtc)
                    VALUES (@CollectionId, @FilePath, @AddedAtUtc);
                ";
            }
            AddParam(cmd, "@CollectionId", collectionId);
            AddParam(cmd, "@FilePath", filePath);
            AddParam(cmd, "@AddedAtUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task RemoveFromCollectionAsync(string collectionId, string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CollectionItems WHERE CollectionId = @CollectionId AND FilePath = @FilePath;";
            AddParam(cmd, "@CollectionId", collectionId);
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<string>> GetCollectionsForLoraAsync(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return new List<string>();
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT CollectionId FROM CollectionItems WHERE FilePath = @FilePath;";
            AddParam(cmd, "@FilePath", filePath);
            var list = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                list.Add(reader.GetString(0));
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<LoraMetadata>> GetCollectionLorasAsync(string collectionId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT m.* FROM Loras m
                INNER JOIN CollectionItems ci ON ci.FilePath = m.FilePath
                WHERE ci.CollectionId = @CollectionId
                ORDER BY LOWER(m.FileName) ASC;
            ";
            AddParam(cmd, "@CollectionId", collectionId);
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

    public async Task SetLoraCategoryAsync(string filePath, string? category) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET Category = @Category, UpdatedAtUtc = @UpdatedAt WHERE FilePath = @FilePath;";
            AddParam(cmd, "@Category", (object?)category ?? DBNull.Value);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task SetLoraTagsAsync(string filePath, IEnumerable<string> tags) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET TagsJson = @TagsJson, UpdatedAtUtc = @UpdatedAt WHERE FilePath = @FilePath;";
            var list = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            AddParam(cmd, "@TagsJson", list.Count > 0 ? JsonSerializer.Serialize(list) : (object)DBNull.Value);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task SetLoraCategoryAndTagsAsync(string filePath, string? category, IEnumerable<string> tags) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET Category = @Category, TagsJson = @TagsJson, UpdatedAtUtc = @UpdatedAt WHERE FilePath = @FilePath;";
            AddParam(cmd, "@Category", (object?)category ?? DBNull.Value);
            var list = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            AddParam(cmd, "@TagsJson", list.Count > 0 ? JsonSerializer.Serialize(list) : (object)DBNull.Value);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task<List<LoraMetadata>> GetByLibraryAsync(string? libraryId = null, string? folderPath = null) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(libraryId) && !string.IsNullOrWhiteSpace(folderPath)) {
                string normFolder = folderPath.Trim().TrimEnd('/', '\\').Replace('\\', '/');
                cmd.CommandText = @"
                    SELECT * FROM Loras 
                    WHERE LibraryId = @LibraryId 
                       OR REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') = @NormFolder
                       OR REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') LIKE @NormFolderPrefix
                    ORDER BY LOWER(FileName) ASC;
                ";
                AddParam(cmd, "@LibraryId", libraryId);
                AddParam(cmd, "@NormFolder", normFolder);
                AddParam(cmd, "@NormFolderPrefix", normFolder + "/%");
            } else if (!string.IsNullOrWhiteSpace(libraryId)) {
                cmd.CommandText = @"
                    SELECT * FROM Loras 
                    WHERE LibraryId = @LibraryId 
                       OR (DirectoryPath IS NOT NULL AND EXISTS (
                           SELECT 1 FROM Libraries l 
                           WHERE l.Id = @LibraryId 
                             AND (
                                 REPLACE(RTRIM(Loras.DirectoryPath, '/\'), '\', '/') = REPLACE(RTRIM(l.FolderPath, '/\'), '\', '/')
                                 OR REPLACE(RTRIM(Loras.DirectoryPath, '/\'), '\', '/') LIKE REPLACE(RTRIM(l.FolderPath, '/\'), '\', '/') || '/%'
                             )
                       ))
                    ORDER BY LOWER(FileName) ASC;
                ";
                AddParam(cmd, "@LibraryId", libraryId);
            } else if (!string.IsNullOrWhiteSpace(folderPath)) {
                string normFolder = folderPath.Trim().TrimEnd('/', '\\').Replace('\\', '/');
                cmd.CommandText = @"
                    SELECT * FROM Loras 
                    WHERE REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') = @NormFolder
                       OR REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') LIKE @NormFolderPrefix
                    ORDER BY LOWER(FileName) ASC;
                ";
                AddParam(cmd, "@NormFolder", normFolder);
                AddParam(cmd, "@NormFolderPrefix", normFolder + "/%");
            } else {
                cmd.CommandText = "SELECT * FROM Loras ORDER BY LOWER(FileName) ASC;";
            }

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
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT FilePath, LastModifiedUtc, FileSizeBytes FROM Loras;";

            var dict = new Dictionary<string, (DateTime LastModified, long Size)>(StringComparer.OrdinalIgnoreCase);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                string path = reader.GetString(0);
                string? timeStr = reader.IsDBNull(1) ? null : reader.GetString(1);
                long size = reader.GetInt64(2);
                DateTime time = DateTime.TryParse(timeStr, null, System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt
                    : (DateTime.TryParse(timeStr, out var fallback) ? fallback.ToUniversalTime() : DateTime.MinValue);
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
            using var connection = await OpenConnectionAsync();

            if (connection is NpgsqlConnection npgsqlConn) {
                const string pgSql = @"
                    INSERT INTO Loras (
                        FilePath, FileName, DirectoryPath, BaseModel, UserBaseModel, IsFavorite,
                        NetworkDim, NetworkAlpha, NetworkModule, LearningRate, UnetLearningRate, TextEncoderLearningRate,
                        Optimizer, LrScheduler, Epochs, TotalSteps, Resolution, Precision,
                        FileSizeBytes, LastModifiedUtc, ThumbnailPath, Sha256Hash, TrainedWordsJson, RawMetadataJson,
                        CivitaiModelId, CivitaiVersionId, CivitaiModelName, CivitaiVersionName, CivitaiBaseModel,
                        CivitaiDescription, CivitaiDownloadUrl, CivitaiUrl, CivitaiPreviewImageUrl, CivitaiSamplePromptsJson,
                        LibraryId, Category, TagsJson, CreatedAtUtc, UpdatedAtUtc
                    ) VALUES (
                        @FilePath, @FileName, @DirectoryPath, @BaseModel, @UserBaseModel, @IsFavorite,
                        @NetworkDim, @NetworkAlpha, @NetworkModule, @LearningRate, @UnetLearningRate, @TextEncoderLearningRate,
                        @Optimizer, @LrScheduler, @Epochs, @TotalSteps, @Resolution, @Precision,
                        @FileSizeBytes, @LastModifiedUtc, @ThumbnailPath, @Sha256Hash, @TrainedWordsJson, @RawMetadataJson,
                        @CivitaiModelId, @CivitaiVersionId, @CivitaiModelName, @CivitaiVersionName, @CivitaiBaseModel,
                        @CivitaiDescription, @CivitaiDownloadUrl, @CivitaiUrl, @CivitaiPreviewImageUrl, @CivitaiSamplePromptsJson,
                        @LibraryId, @Category, @TagsJson, @CreatedAtUtc, @UpdatedAtUtc
                    )
                    ON CONFLICT (FilePath) DO UPDATE SET
                        FileName = EXCLUDED.FileName,
                        DirectoryPath = EXCLUDED.DirectoryPath,
                        BaseModel = EXCLUDED.BaseModel,
                        UserBaseModel = COALESCE(Loras.UserBaseModel, EXCLUDED.UserBaseModel),
                        IsFavorite = Loras.IsFavorite,
                        NetworkDim = EXCLUDED.NetworkDim,
                        NetworkAlpha = EXCLUDED.NetworkAlpha,
                        NetworkModule = EXCLUDED.NetworkModule,
                        LearningRate = EXCLUDED.LearningRate,
                        UnetLearningRate = EXCLUDED.UnetLearningRate,
                        TextEncoderLearningRate = EXCLUDED.TextEncoderLearningRate,
                        Optimizer = EXCLUDED.Optimizer,
                        LrScheduler = EXCLUDED.LrScheduler,
                        Epochs = EXCLUDED.Epochs,
                        TotalSteps = EXCLUDED.TotalSteps,
                        Resolution = EXCLUDED.Resolution,
                        Precision = EXCLUDED.Precision,
                        FileSizeBytes = EXCLUDED.FileSizeBytes,
                        LastModifiedUtc = EXCLUDED.LastModifiedUtc,
                        ThumbnailPath = COALESCE(EXCLUDED.ThumbnailPath, Loras.ThumbnailPath),
                        Sha256Hash = COALESCE(EXCLUDED.Sha256Hash, Loras.Sha256Hash),
                        TrainedWordsJson = EXCLUDED.TrainedWordsJson,
                        RawMetadataJson = EXCLUDED.RawMetadataJson,
                        CivitaiModelId = COALESCE(EXCLUDED.CivitaiModelId, Loras.CivitaiModelId),
                        CivitaiVersionId = COALESCE(EXCLUDED.CivitaiVersionId, Loras.CivitaiVersionId),
                        CivitaiModelName = COALESCE(EXCLUDED.CivitaiModelName, Loras.CivitaiModelName),
                        CivitaiVersionName = COALESCE(EXCLUDED.CivitaiVersionName, Loras.CivitaiVersionName),
                        CivitaiBaseModel = COALESCE(EXCLUDED.CivitaiBaseModel, Loras.CivitaiBaseModel),
                        CivitaiDescription = COALESCE(EXCLUDED.CivitaiDescription, Loras.CivitaiDescription),
                        CivitaiDownloadUrl = COALESCE(EXCLUDED.CivitaiDownloadUrl, Loras.CivitaiDownloadUrl),
                        CivitaiUrl = COALESCE(EXCLUDED.CivitaiUrl, Loras.CivitaiUrl),
                        CivitaiPreviewImageUrl = COALESCE(EXCLUDED.CivitaiPreviewImageUrl, Loras.CivitaiPreviewImageUrl),
                        CivitaiSamplePromptsJson = COALESCE(EXCLUDED.CivitaiSamplePromptsJson, Loras.CivitaiSamplePromptsJson),
                        LibraryId = COALESCE(EXCLUDED.LibraryId, Loras.LibraryId),
                        Category = COALESCE(Loras.Category, EXCLUDED.Category),
                        TagsJson = COALESCE(Loras.TagsJson, EXCLUDED.TagsJson),
                        UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
                ";
                using var tx = await npgsqlConn.BeginTransactionAsync();
                foreach (var meta in items) {
                    using var cmd = npgsqlConn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = pgSql;
                    BindNpgsqlLoraParameters(cmd, meta);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return;
            }

            if (connection is SqliteConnection sqliteConn) {
                using var transaction = sqliteConn.BeginTransaction();

            const string sql = @"
                INSERT INTO Loras (
                    FilePath, FileName, DirectoryPath, LibraryId, BaseModel, UserBaseModel, IsFavorite, Category, TagsJson,
                    NetworkDim, NetworkAlpha, NetworkModule, LearningRate, UnetLearningRate, TextEncoderLearningRate,
                    Optimizer, LrScheduler, Epochs, TotalSteps, Resolution, Precision,
                    FileSizeBytes, LastModifiedUtc, ThumbnailPath, Sha256Hash, TrainedWordsJson, RawMetadataJson,
                    CivitaiModelId, CivitaiVersionId, CivitaiModelName, CivitaiVersionName, CivitaiBaseModel,
                    CivitaiDescription, CivitaiDownloadUrl, CivitaiUrl, CivitaiPreviewImageUrl, CivitaiSamplePromptsJson,
                    CreatedAtUtc, UpdatedAtUtc
                ) VALUES (
                    $FilePath, $FileName, $DirectoryPath, $LibraryId, $BaseModel, $UserBaseModel, $IsFavorite, $Category, $TagsJson,
                    $NetworkDim, $NetworkAlpha, $NetworkModule, $LearningRate, $UnetLearningRate, $TextEncoderLearningRate,
                    $Optimizer, $LrScheduler, $Epochs, $TotalSteps, $Resolution, $Precision,
                    $FileSizeBytes, $LastModifiedUtc, $ThumbnailPath, $Sha256Hash, $TrainedWordsJson, $RawMetadataJson,
                    $CivitaiModelId, $CivitaiVersionId, $CivitaiModelName, $CivitaiVersionName, $CivitaiBaseModel,
                    $CivitaiDescription, $CivitaiDownloadUrl, $CivitaiUrl, $CivitaiPreviewImageUrl, $CivitaiSamplePromptsJson,
                    $CreatedAtUtc, $UpdatedAtUtc
                ) ON CONFLICT(FilePath) DO UPDATE SET
                    FileName = excluded.FileName,
                    DirectoryPath = excluded.DirectoryPath,
                    LibraryId = COALESCE(excluded.LibraryId, Loras.LibraryId),
                    BaseModel = excluded.BaseModel,
                    UserBaseModel = COALESCE(Loras.UserBaseModel, excluded.UserBaseModel),
                    IsFavorite = Loras.IsFavorite,
                    Category = COALESCE(Loras.Category, excluded.Category),
                    TagsJson = COALESCE(Loras.TagsJson, excluded.TagsJson),
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

            using var cmd = sqliteConn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;

            var pFilePath = cmd.Parameters.Add("$FilePath", SqliteType.Text);
            var pFileName = cmd.Parameters.Add("$FileName", SqliteType.Text);
            var pDirectoryPath = cmd.Parameters.Add("$DirectoryPath", SqliteType.Text);
            var pLibraryId = cmd.Parameters.Add("$LibraryId", SqliteType.Text);
            var pBaseModel = cmd.Parameters.Add("$BaseModel", SqliteType.Text);
            var pUserBaseModel = cmd.Parameters.Add("$UserBaseModel", SqliteType.Text);
            var pIsFavorite = cmd.Parameters.Add("$IsFavorite", SqliteType.Integer);
            var pCategory = cmd.Parameters.Add("$Category", SqliteType.Text);
            var pTagsJson = cmd.Parameters.Add("$TagsJson", SqliteType.Text);
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
                pLibraryId.Value = (object?)meta.LibraryId ?? DBNull.Value;
                pBaseModel.Value = meta.BaseModel;
                pUserBaseModel.Value = (object?)meta.UserBaseModel ?? DBNull.Value;
                pIsFavorite.Value = meta.IsFavorite ? 1 : 0;
                pCategory.Value = (object?)meta.Category ?? DBNull.Value;
                pTagsJson.Value = meta.Tags != null && meta.Tags.Count > 0 ? JsonSerializer.Serialize(meta.Tags) : (object)DBNull.Value;
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
            }
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
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET IsFavorite = @IsFavorite, UpdatedAtUtc = @UpdatedAt WHERE FilePath = @FilePath;";
            AddParam(cmd, "@IsFavorite", isFavorite ? 1 : 0);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task SetUserBaseModelAsync(string filePath, string? userBaseModel) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Loras SET UserBaseModel = @UserBaseModel, UpdatedAtUtc = @UpdatedAt WHERE FilePath = @FilePath;";
            AddParam(cmd, "@UserBaseModel", (object?)userBaseModel ?? DBNull.Value);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string filePath) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Loras WHERE FilePath = @FilePath;";
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync();
        } finally {
            _lock.Release();
        }
    }

    public async Task DeleteMissingInFolderAsync(string folderPath, IEnumerable<string> existingPaths) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();

            var existingSet = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
            string normFolder = folderPath.Trim().TrimEnd('/', '\\').Replace('\\', '/');
            using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = @"
                SELECT FilePath FROM Loras 
                WHERE REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') = @NormFolder
                   OR REPLACE(RTRIM(DirectoryPath, '/\'), '\', '/') LIKE @NormFolderPrefix;
            ";
            AddParam(selectCmd, "@NormFolder", normFolder);
            AddParam(selectCmd, "@NormFolderPrefix", normFolder + "/%");

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
                delCmd.CommandText = "DELETE FROM Loras WHERE FilePath = @FilePath;";
                var p = delCmd.CreateParameter();
                p.ParameterName = "@FilePath";
                delCmd.Parameters.Add(p);
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

    public async Task<string> MoveLoraFileAsync(string oldFilePath, string targetDirectoryPath, string? targetLibraryId = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectoryPath);
        if (!File.Exists(oldFilePath)) {
            throw new FileNotFoundException("Source LoRA file does not exist.", oldFilePath);
        }

        Directory.CreateDirectory(targetDirectoryPath);
        string fileName = Path.GetFileName(oldFilePath);
        string newFilePath = Path.Combine(targetDirectoryPath, fileName);

        // Handle collision
        if (!string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(newFilePath)) {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            do {
                newFilePath = Path.Combine(targetDirectoryPath, $"{nameWithoutExt} ({counter}){ext}");
                counter++;
            } while (File.Exists(newFilePath));
            fileName = Path.GetFileName(newFilePath);
        }

        // Move the safetensors file
        if (!string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase)) {
            File.Move(oldFilePath, newFilePath);
        }

        // Move companion files (thumbnail, json, etc.)
        string? newThumbnailPath = null;
        string oldDir = Path.GetDirectoryName(oldFilePath) ?? string.Empty;
        string oldNameWithoutExt = Path.GetFileNameWithoutExtension(oldFilePath);
        string newNameWithoutExt = Path.GetFileNameWithoutExtension(newFilePath);

        string[] companionExts = { ".png", ".preview.png", ".jpg", ".preview.jpg", ".jpeg", ".webp", ".json", ".civitai.info" };
        foreach (var cExt in companionExts) {
            string candidateOld = Path.Combine(oldDir, oldNameWithoutExt + cExt);
            if (File.Exists(candidateOld)) {
                string candidateNew = Path.Combine(targetDirectoryPath, newNameWithoutExt + cExt);
                try {
                    if (!string.Equals(candidateOld, candidateNew, StringComparison.OrdinalIgnoreCase)) {
                        File.Move(candidateOld, candidateNew, overwrite: true);
                    }
                    if (cExt != ".json" && cExt != ".civitai.info" && newThumbnailPath == null) {
                        newThumbnailPath = candidateNew;
                    }
                } catch {
                    // Non-critical companion move
                }
            }
        }

        string newDir = Path.GetDirectoryName(newFilePath) ?? targetDirectoryPath;

        // Update database
        await EnsureInitializedAsync();
        await _lock.WaitAsync();
        try {
            using var connection = await OpenConnectionAsync();
            using var trans = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = @"
                UPDATE Loras 
                SET FilePath = @NewFilePath,
                    FileName = @NewFileName,
                    DirectoryPath = @NewDirectoryPath,
                    LibraryId = COALESCE(@TargetLibraryId, LibraryId),
                    ThumbnailPath = COALESCE(@NewThumbnailPath, ThumbnailPath),
                    UpdatedAtUtc = @UpdatedAt
                WHERE FilePath = @OldFilePath;

                UPDATE CollectionItems
                SET FilePath = @NewFilePath
                WHERE FilePath = @OldFilePath;
            ";
            AddParam(cmd, "@NewFilePath", newFilePath);
            AddParam(cmd, "@NewFileName", fileName);
            AddParam(cmd, "@NewDirectoryPath", newDir);
            AddParam(cmd, "@TargetLibraryId", (object?)targetLibraryId ?? DBNull.Value);
            AddParam(cmd, "@NewThumbnailPath", (object?)newThumbnailPath ?? DBNull.Value);
            AddParam(cmd, "@UpdatedAt", DateTime.UtcNow.ToString("O"));
            AddParam(cmd, "@OldFilePath", oldFilePath);

            await cmd.ExecuteNonQueryAsync();
            trans.Commit();
        } finally {
            _lock.Release();
        }

        return newFilePath;
    }

    private static LoraMetadata MapReaderToMetadata(DbDataReader reader) {
        var meta = new LoraMetadata {
            FilePath = reader.GetString(reader.GetOrdinal("FilePath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            BaseModel = reader.GetString(reader.GetOrdinal("BaseModel")),
            UserBaseModel = reader.IsDBNull(reader.GetOrdinal("UserBaseModel")) ? null : reader.GetString(reader.GetOrdinal("UserBaseModel")),
            IsFavorite = !reader.IsDBNull(reader.GetOrdinal("IsFavorite")) && Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IsFavorite"))) == 1,
            FileSizeBytes = !reader.IsDBNull(reader.GetOrdinal("FileSizeBytes")) ? Convert.ToInt64(reader.GetValue(reader.GetOrdinal("FileSizeBytes"))) : 0L
        };

        try {
            int libOrd = reader.GetOrdinal("LibraryId");
            if (libOrd >= 0 && !reader.IsDBNull(libOrd)) {
                meta.LibraryId = reader.GetString(libOrd);
            }
        } catch {
            // Ignore if LibraryId column is not present
        }

        try {
            int catOrd = reader.GetOrdinal("Category");
            if (catOrd >= 0 && !reader.IsDBNull(catOrd)) {
                meta.Category = reader.GetString(catOrd);
            }
        } catch { }

        try {
            int tagsOrd = reader.GetOrdinal("TagsJson");
            if (tagsOrd >= 0 && !reader.IsDBNull(tagsOrd)) {
                meta.Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(tagsOrd)) ?? new();
            }
        } catch { }

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
                ModelId = Convert.ToInt64(reader.GetValue(civModelIdOrd)),
                VersionId = reader.IsDBNull(reader.GetOrdinal("CivitaiVersionId")) ? 0L : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("CivitaiVersionId"))),
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

    // -------------------------------------------------------------
    // PostgreSQL Studio Database Engine & Bidirectional Migration
    // -------------------------------------------------------------

    public sealed record PostgreSqlConfig {
        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 5432;
        public string Database { get; init; } = "loramancer_studio";
        public string Username { get; init; } = "postgres";
        public string Password { get; init; } = string.Empty;
        public string SslMode { get; init; } = "Prefer";

        public string BuildConnectionString(string? overrideDb = null) {
            var builder = new NpgsqlConnectionStringBuilder {
                Host = Host,
                Port = Port,
                Database = overrideDb ?? Database,
                Username = Username,
                Password = Password,
                Timeout = 10,
                CommandTimeout = 30
            };
            if (Enum.TryParse<Npgsql.SslMode>(SslMode, true, out var ssl)) {
                builder.SslMode = ssl;
            }
            return builder.ConnectionString;
        }
    }

    public PostgreSqlConfig GetCurrentPostgreSqlConfig() {
        if (_settingsService == null) {
            return new PostgreSqlConfig();
        }
        return new PostgreSqlConfig {
            Host = _settingsService.Current.PgHost,
            Port = _settingsService.Current.PgPort,
            Database = _settingsService.Current.PgDatabase,
            Username = _settingsService.Current.PgUsername,
            Password = _settingsService.Current.PgPassword,
            SslMode = _settingsService.Current.PgSslMode
        };
    }

    public async Task<(bool Success, string Message, TimeSpan Latency)> TestPostgreSqlConnectionAsync(PostgreSqlConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try {
            string connStr = config.BuildConnectionString();
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version();";
            var ver = await cmd.ExecuteScalarAsync();
            sw.Stop();
            string versionStr = ver?.ToString() ?? "PostgreSQL";
            if (versionStr.Length > 50) versionStr = versionStr[..50] + "...";
            return (true, $"Connected successfully to {versionStr} ({sw.ElapsedMilliseconds}ms)", sw.Elapsed);
        } catch (Exception ex) {
            sw.Stop();
            // If the specific database doesn't exist, try connecting to 'postgres' default database
            try {
                string maintConnStr = config.BuildConnectionString(overrideDb: "postgres");
                await using var maintConn = new NpgsqlConnection(maintConnStr);
                await maintConn.OpenAsync();
                return (true, $"Server reachable! Database '{config.Database}' does not exist yet and will be created automatically.", sw.Elapsed);
            } catch {
                return (false, $"Connection failed: {ex.Message}", sw.Elapsed);
            }
        }
    }

    public async Task EnsurePostgreSqlDatabaseAndSchemaAsync(PostgreSqlConfig config, Action<string>? onStatus = null) {
        ArgumentNullException.ThrowIfNull(config);

        onStatus?.Invoke("Verifying PostgreSQL database...");
        string maintenanceConnStr = config.BuildConnectionString(overrideDb: "postgres");
        await using (var maintConn = new NpgsqlConnection(maintenanceConnStr)) {
            await maintConn.OpenAsync();
            using var checkDbCmd = maintConn.CreateCommand();
            checkDbCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbName;";
            checkDbCmd.Parameters.AddWithValue("@dbName", config.Database.ToLowerInvariant());
            var exists = await checkDbCmd.ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value) {
                onStatus?.Invoke($"Database '{config.Database}' not found. Creating database...");
                string safeDbName = config.Database.Replace("\"", "").Trim();
                using var createDbCmd = maintConn.CreateCommand();
                createDbCmd.CommandText = $"CREATE DATABASE \"{safeDbName}\";";
                await createDbCmd.ExecuteNonQueryAsync();
            }
        }

        onStatus?.Invoke($"Validating schema and tables in '{config.Database}'...");
        string targetConnStr = config.BuildConnectionString();
        await using (var conn = new NpgsqlConnection(targetConnStr)) {
            await conn.OpenAsync();

            const string createTablesSql = @"
                CREATE TABLE IF NOT EXISTS Libraries (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    FolderPath TEXT NOT NULL,
                    Description TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_libraries_path ON Libraries(FolderPath);

                CREATE TABLE IF NOT EXISTS Categories (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE,
                    Color TEXT NOT NULL,
                    Icon TEXT,
                    Description TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_categories_name ON Categories(Name);

                CREATE TABLE IF NOT EXISTS Loras (
                    FilePath TEXT PRIMARY KEY,
                    FileName TEXT NOT NULL,
                    DirectoryPath TEXT NOT NULL,
                    BaseModel TEXT NOT NULL,
                    UserBaseModel TEXT,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    NetworkDim INTEGER,
                    NetworkAlpha DOUBLE PRECISION,
                    NetworkModule TEXT,
                    LearningRate DOUBLE PRECISION,
                    UnetLearningRate DOUBLE PRECISION,
                    TextEncoderLearningRate DOUBLE PRECISION,
                    Optimizer TEXT,
                    LrScheduler TEXT,
                    Epochs INTEGER,
                    TotalSteps INTEGER,
                    Resolution TEXT,
                    Precision TEXT,
                    FileSizeBytes BIGINT NOT NULL DEFAULT 0,
                    LastModifiedUtc TEXT,
                    ThumbnailPath TEXT,
                    Sha256Hash TEXT,
                    TrainedWordsJson TEXT,
                    RawMetadataJson TEXT,
                    CivitaiModelId BIGINT,
                    CivitaiVersionId BIGINT,
                    CivitaiModelName TEXT,
                    CivitaiVersionName TEXT,
                    CivitaiBaseModel TEXT,
                    CivitaiDescription TEXT,
                    CivitaiDownloadUrl TEXT,
                    CivitaiUrl TEXT,
                    CivitaiPreviewImageUrl TEXT,
                    CivitaiSamplePromptsJson TEXT,
                    LibraryId TEXT,
                    Category TEXT,
                    TagsJson TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_loras_dir ON Loras(DirectoryPath);
                CREATE INDEX IF NOT EXISTS idx_loras_fav ON Loras(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_loras_base ON Loras(BaseModel);
                CREATE INDEX IF NOT EXISTS idx_loras_lib ON Loras(LibraryId);
                CREATE INDEX IF NOT EXISTS idx_loras_cat ON Loras(Category);
                CREATE INDEX IF NOT EXISTS idx_loras_composite ON Loras(LibraryId, DirectoryPath, IsFavorite, BaseModel);

                CREATE TABLE IF NOT EXISTS Collections (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    Color TEXT,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_collections_name ON Collections(Name);

                CREATE TABLE IF NOT EXISTS CollectionItems (
                    CollectionId TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    AddedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (CollectionId, FilePath)
                );
                CREATE INDEX IF NOT EXISTS idx_collection_items_path ON CollectionItems(FilePath);
                CREATE INDEX IF NOT EXISTS idx_collection_items_col ON CollectionItems(CollectionId);

                CREATE TABLE IF NOT EXISTS GalleryMedia (
                    FilePath TEXT PRIMARY KEY,
                    FileName TEXT NOT NULL,
                    FileExtension TEXT NOT NULL,
                    MediaType INTEGER NOT NULL DEFAULT 0,
                    FileSizeBytes BIGINT NOT NULL DEFAULT 0,
                    FormattedSize TEXT,
                    CreatedDate TEXT NOT NULL,
                    FolderPath TEXT NOT NULL,
                    FolderName TEXT,
                    FileUrl TEXT,
                    IsAiGenerated INTEGER NOT NULL DEFAULT 0,
                    AiGenerator TEXT,
                    Prompt TEXT,
                    NegativePrompt TEXT,
                    Seed BIGINT,
                    Steps INTEGER,
                    Sampler TEXT,
                    Scheduler TEXT,
                    CfgScale DOUBLE PRECISION,
                    ModelName TEXT,
                    UsedLorasJson TEXT,
                    Width INTEGER,
                    Height INTEGER,
                    DurationSeconds DOUBLE PRECISION,
                    RawMetadataJson TEXT,
                    Category TEXT,
                    UserTagsJson TEXT,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    UserNotes TEXT,
                    AssociatedLoraNamesJson TEXT,
                    AssociatedLoraFilePathsJson TEXT,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_gallery_folder ON GalleryMedia(FolderPath);
                CREATE INDEX IF NOT EXISTS idx_gallery_fav ON GalleryMedia(IsFavorite);
                CREATE INDEX IF NOT EXISTS idx_gallery_ai ON GalleryMedia(IsAiGenerated);
                CREATE INDEX IF NOT EXISTS idx_gallery_created ON GalleryMedia(CreatedDate);
            ";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = createTablesSql;
            await cmd.ExecuteNonQueryAsync();

            // Check and add missing columns if upgrading schema
            (string colName, string colType)[] checkColumns = [
                ("LibraryId", "TEXT"),
                ("Category", "TEXT"),
                ("TagsJson", "TEXT")
            ];

            foreach (var (colName, colType) in checkColumns) {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE Loras ADD COLUMN IF NOT EXISTS \"{colName}\" {colType};";
                await alterCmd.ExecuteNonQueryAsync();
            }
        }
        onStatus?.Invoke("PostgreSQL schema validated successfully.");
    }

    public async Task<int> MigrateSqliteToPostgreSqlAsync(PostgreSqlConfig config, Action<string, double>? onProgress = null) {
        onProgress?.Invoke("Validating target PostgreSQL database & schema...", 0.05);
        await EnsurePostgreSqlDatabaseAndSchemaAsync(config, msg => onProgress?.Invoke(msg, 0.1));

        onProgress?.Invoke("Reading categories from local SQLite...", 0.15);
        var categories = await GetCategoriesAsync();
        var libraries = await GetLibrariesAsync();
        var loras = await GetAllAsync();

        string targetConnStr = config.BuildConnectionString();
        await using var pgConn = new NpgsqlConnection(targetConnStr);
        await pgConn.OpenAsync();

        // Migrate Categories
        onProgress?.Invoke($"Migrating {categories.Count} categories...", 0.2);
        foreach (var cat in categories) {
            using var catCmd = pgConn.CreateCommand();
            catCmd.CommandText = @"
                INSERT INTO Categories (Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @Color, @Icon, @Description, @CreatedAtUtc, @UpdatedAtUtc)
                ON CONFLICT (Id) DO UPDATE SET
                    Name = EXCLUDED.Name,
                    Color = EXCLUDED.Color,
                    Icon = EXCLUDED.Icon,
                    Description = EXCLUDED.Description,
                    UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
            ";
            catCmd.Parameters.AddWithValue("@Id", cat.Id);
            catCmd.Parameters.AddWithValue("@Name", cat.Name);
            catCmd.Parameters.AddWithValue("@Color", cat.Color);
            catCmd.Parameters.AddWithValue("@Icon", (object?)cat.Icon ?? DBNull.Value);
            catCmd.Parameters.AddWithValue("@Description", (object?)cat.Description ?? DBNull.Value);
            catCmd.Parameters.AddWithValue("@CreatedAtUtc", cat.CreatedAtUtc.ToString("O"));
            catCmd.Parameters.AddWithValue("@UpdatedAtUtc", cat.UpdatedAtUtc.ToString("O"));
            await catCmd.ExecuteNonQueryAsync();
        }

        // Migrate Libraries
        onProgress?.Invoke($"Migrating {libraries.Count} libraries...", 0.25);
        foreach (var lib in libraries) {
            using var libCmd = pgConn.CreateCommand();
            libCmd.CommandText = @"
                INSERT INTO Libraries (Id, Name, FolderPath, Description, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @FolderPath, @Description, @CreatedAtUtc, @UpdatedAtUtc)
                ON CONFLICT (Id) DO UPDATE SET
                    Name = EXCLUDED.Name,
                    FolderPath = EXCLUDED.FolderPath,
                    Description = EXCLUDED.Description,
                    UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
            ";
            libCmd.Parameters.AddWithValue("@Id", lib.Id);
            libCmd.Parameters.AddWithValue("@Name", lib.Name);
            libCmd.Parameters.AddWithValue("@FolderPath", lib.FolderPath);
            libCmd.Parameters.AddWithValue("@Description", (object?)lib.Description ?? DBNull.Value);
            libCmd.Parameters.AddWithValue("@CreatedAtUtc", lib.CreatedAtUtc.ToString("O"));
            libCmd.Parameters.AddWithValue("@UpdatedAtUtc", lib.UpdatedAtUtc.ToString("O"));
            await libCmd.ExecuteNonQueryAsync();
        }

        // Migrate Collections
        var collections = await GetCollectionsAsync();
        onProgress?.Invoke($"Migrating {collections.Count} collections...", 0.28);
        foreach (var col in collections) {
            using var colCmd = pgConn.CreateCommand();
            colCmd.CommandText = @"
                INSERT INTO Collections (Id, Name, Description, Color, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @Description, @Color, @CreatedAtUtc, @UpdatedAtUtc)
                ON CONFLICT (Id) DO UPDATE SET
                    Name = EXCLUDED.Name,
                    Description = EXCLUDED.Description,
                    Color = EXCLUDED.Color,
                    UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
            ";
            colCmd.Parameters.AddWithValue("@Id", col.Id);
            colCmd.Parameters.AddWithValue("@Name", col.Name);
            colCmd.Parameters.AddWithValue("@Description", (object?)col.Description ?? DBNull.Value);
            colCmd.Parameters.AddWithValue("@Color", (object?)col.Color ?? DBNull.Value);
            colCmd.Parameters.AddWithValue("@CreatedAtUtc", col.CreatedAtUtc.ToString("O"));
            colCmd.Parameters.AddWithValue("@UpdatedAtUtc", col.UpdatedAtUtc.ToString("O"));
            await colCmd.ExecuteNonQueryAsync();

            var colLoras = await GetCollectionLorasAsync(col.Id);
            foreach (var item in colLoras) {
                if (string.IsNullOrWhiteSpace(item.FilePath)) continue;
                using var itemCmd = pgConn.CreateCommand();
                itemCmd.CommandText = @"
                    INSERT INTO CollectionItems (CollectionId, FilePath, AddedAtUtc)
                    VALUES (@CollectionId, @FilePath, @AddedAtUtc)
                    ON CONFLICT (CollectionId, FilePath) DO NOTHING;
                ";
                itemCmd.Parameters.AddWithValue("@CollectionId", col.Id);
                itemCmd.Parameters.AddWithValue("@FilePath", item.FilePath);
                itemCmd.Parameters.AddWithValue("@AddedAtUtc", DateTime.UtcNow.ToString("O"));
                await itemCmd.ExecuteNonQueryAsync();
            }
        }

        // Migrate Loras in batches
        int total = loras.Count;
        int migrated = 0;
        int batchSize = 100;
        for (int i = 0; i < total; i += batchSize) {
            var batch = loras.Skip(i).Take(batchSize).ToList();
            await using var tx = await pgConn.BeginTransactionAsync();
            foreach (var lora in batch) {
                using var loraCmd = pgConn.CreateCommand();
                loraCmd.Transaction = tx;
                loraCmd.CommandText = @"
                    INSERT INTO Loras (
                        FilePath, FileName, DirectoryPath, BaseModel, UserBaseModel, IsFavorite,
                        NetworkDim, NetworkAlpha, NetworkModule, LearningRate, UnetLearningRate, TextEncoderLearningRate,
                        Optimizer, LrScheduler, Epochs, TotalSteps, Resolution, Precision, FileSizeBytes,
                        LastModifiedUtc, ThumbnailPath, Sha256Hash, TrainedWordsJson, RawMetadataJson,
                        CivitaiModelId, CivitaiVersionId, CivitaiModelName, CivitaiVersionName, CivitaiBaseModel,
                        CivitaiDescription, CivitaiDownloadUrl, CivitaiUrl, CivitaiPreviewImageUrl,
                        CivitaiSamplePromptsJson, LibraryId, Category, TagsJson, CreatedAtUtc, UpdatedAtUtc
                    ) VALUES (
                        @FilePath, @FileName, @DirectoryPath, @BaseModel, @UserBaseModel, @IsFavorite,
                        @NetworkDim, @NetworkAlpha, @NetworkModule, @LearningRate, @UnetLearningRate, @TextEncoderLearningRate,
                        @Optimizer, @LrScheduler, @Epochs, @TotalSteps, @Resolution, @Precision, @FileSizeBytes,
                        @LastModifiedUtc, @ThumbnailPath, @Sha256Hash, @TrainedWordsJson, @RawMetadataJson,
                        @CivitaiModelId, @CivitaiVersionId, @CivitaiModelName, @CivitaiVersionName, @CivitaiBaseModel,
                        @CivitaiDescription, @CivitaiDownloadUrl, @CivitaiUrl, @CivitaiPreviewImageUrl,
                        @CivitaiSamplePromptsJson, @LibraryId, @Category, @TagsJson, @CreatedAtUtc, @UpdatedAtUtc
                    ) ON CONFLICT (FilePath) DO UPDATE SET
                        FileName = EXCLUDED.FileName,
                        DirectoryPath = EXCLUDED.DirectoryPath,
                        BaseModel = EXCLUDED.BaseModel,
                        UserBaseModel = EXCLUDED.UserBaseModel,
                        IsFavorite = EXCLUDED.IsFavorite,
                        NetworkDim = EXCLUDED.NetworkDim,
                        NetworkAlpha = EXCLUDED.NetworkAlpha,
                        NetworkModule = EXCLUDED.NetworkModule,
                        LearningRate = EXCLUDED.LearningRate,
                        UnetLearningRate = EXCLUDED.UnetLearningRate,
                        TextEncoderLearningRate = EXCLUDED.TextEncoderLearningRate,
                        Optimizer = EXCLUDED.Optimizer,
                        LrScheduler = EXCLUDED.LrScheduler,
                        Epochs = EXCLUDED.Epochs,
                        TotalSteps = EXCLUDED.TotalSteps,
                        Resolution = EXCLUDED.Resolution,
                        Precision = EXCLUDED.Precision,
                        FileSizeBytes = EXCLUDED.FileSizeBytes,
                        LastModifiedUtc = EXCLUDED.LastModifiedUtc,
                        ThumbnailPath = EXCLUDED.ThumbnailPath,
                        Sha256Hash = EXCLUDED.Sha256Hash,
                        TrainedWordsJson = EXCLUDED.TrainedWordsJson,
                        RawMetadataJson = EXCLUDED.RawMetadataJson,
                        CivitaiModelId = EXCLUDED.CivitaiModelId,
                        CivitaiVersionId = EXCLUDED.CivitaiVersionId,
                        CivitaiModelName = EXCLUDED.CivitaiModelName,
                        CivitaiVersionName = EXCLUDED.CivitaiVersionName,
                        CivitaiBaseModel = EXCLUDED.CivitaiBaseModel,
                        CivitaiDescription = EXCLUDED.CivitaiDescription,
                        CivitaiDownloadUrl = EXCLUDED.CivitaiDownloadUrl,
                        CivitaiUrl = EXCLUDED.CivitaiUrl,
                        CivitaiPreviewImageUrl = EXCLUDED.CivitaiPreviewImageUrl,
                        CivitaiSamplePromptsJson = EXCLUDED.CivitaiSamplePromptsJson,
                        LibraryId = EXCLUDED.LibraryId,
                        Category = EXCLUDED.Category,
                        TagsJson = EXCLUDED.TagsJson,
                        UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
                ";
                BindNpgsqlLoraParameters(loraCmd, lora);
                await loraCmd.ExecuteNonQueryAsync();
                migrated++;
            }
            await tx.CommitAsync();
            double progress = 0.25 + ((double)migrated / total * 0.70);
            onProgress?.Invoke($"Migrated {migrated} of {total} LoRAs to PostgreSQL...", progress);
        }

        // Migrate GalleryMedia if any exist in SQLite
        var galleryItems = await GetAllGalleryMediaAsync();
        if (galleryItems.Count > 0) {
            onProgress?.Invoke($"Migrating {galleryItems.Count} gallery images to PostgreSQL...", 0.95);
            await UpsertGalleryMediaBatchToPostgreSqlAsync(pgConn, galleryItems, CancellationToken.None);
        }

        onProgress?.Invoke($"Migration complete! {migrated} LoRAs & {galleryItems.Count} gallery items successfully synced to PostgreSQL.", 1.0);
        return migrated;
    }

    public async Task<int> MigratePostgreSqlToSqliteAsync(PostgreSqlConfig config, Action<string, double>? onProgress = null) {
        onProgress?.Invoke("Validating local SQLite database & schema...", 0.05);
        await EnsureInitializedAsync();

        string targetConnStr = config.BuildConnectionString();
        await using var pgConn = new NpgsqlConnection(targetConnStr);
        await pgConn.OpenAsync();

        onProgress?.Invoke("Reading categories from PostgreSQL...", 0.15);
        var pgCategories = new List<LoraCategory>();
        using (var catCmd = pgConn.CreateCommand()) {
            catCmd.CommandText = "SELECT Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc FROM Categories;";
            using var reader = await catCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                pgCategories.Add(new LoraCategory {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Color = reader.GetString(2),
                    Icon = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAtUtc = DateTime.TryParse(reader.GetString(5), out var c) ? c : DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.TryParse(reader.GetString(6), out var u) ? u : DateTime.UtcNow
                });
            }
        }
        foreach (var c in pgCategories) {
            await SaveCategoryAsync(c);
        }

        onProgress?.Invoke("Reading libraries from PostgreSQL...", 0.25);
        var pgLibraries = new List<LoraLibrary>();
        using (var libCmd = pgConn.CreateCommand()) {
            libCmd.CommandText = "SELECT Id, Name, FolderPath, Description, CreatedAtUtc, UpdatedAtUtc FROM Libraries;";
            using var reader = await libCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                pgLibraries.Add(new LoraLibrary {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    FolderPath = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAtUtc = DateTime.TryParse(reader.GetString(4), out var c) ? c : DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.TryParse(reader.GetString(5), out var u) ? u : DateTime.UtcNow
                });
            }
        }
        foreach (var l in pgLibraries) {
            await UpsertLibraryAsync(l);
        }

        onProgress?.Invoke("Reading collections from PostgreSQL...", 0.30);
        var pgCollections = new List<LoraCollection>();
        using (var colCmd = pgConn.CreateCommand()) {
            colCmd.CommandText = "SELECT Id, Name, Description, Color, CreatedAtUtc, UpdatedAtUtc FROM Collections;";
            using var reader = await colCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                pgCollections.Add(new LoraCollection {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Color = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAtUtc = DateTime.TryParse(reader.GetString(4), out var c) ? c : DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.TryParse(reader.GetString(5), out var u) ? u : DateTime.UtcNow
                });
            }
        }
        foreach (var col in pgCollections) {
            await UpsertCollectionAsync(col);
        }

        using (var itemCmd = pgConn.CreateCommand()) {
            itemCmd.CommandText = "SELECT CollectionId, FilePath FROM CollectionItems;";
            using var reader = await itemCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                string colId = reader.GetString(0);
                string path = reader.GetString(1);
                await AddToCollectionAsync(colId, path);
            }
        }

        onProgress?.Invoke("Reading LoRAs from PostgreSQL...", 0.35);
        var pgLoras = new List<LoraMetadata>();
        using (var loraCmd = pgConn.CreateCommand()) {
            loraCmd.CommandText = "SELECT * FROM Loras;";
            using var reader = await loraCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                pgLoras.Add(MapReaderToMetadata(reader));
            }
        }

        int total = pgLoras.Count;
        onProgress?.Invoke($"Writing {total} LoRAs to local SQLite...", 0.5);
        await UpsertBatchAsync(pgLoras);

        onProgress?.Invoke($"Migration complete! {total} LoRAs successfully imported into SQLite.", 1.0);
        return total;
    }

    public async Task SwitchDatabaseProviderAsync(string provider) {
        if (_settingsService != null) {
            _settingsService.Current.DatabaseProvider = provider;
            await _settingsService.SaveSettingsAsync(_settingsService.Current);
            _initialized = false;
            await EnsureInitializedAsync();
        }
    }

    private static void BindNpgsqlLoraParameters(NpgsqlCommand cmd, LoraMetadata lora) {
        cmd.Parameters.AddWithValue("@FilePath", lora.FilePath);
        cmd.Parameters.AddWithValue("@FileName", lora.FileName);
        cmd.Parameters.AddWithValue("@DirectoryPath", Path.GetDirectoryName(lora.FilePath) ?? string.Empty);
        cmd.Parameters.AddWithValue("@BaseModel", lora.BaseModel ?? "Unknown");
        cmd.Parameters.AddWithValue("@UserBaseModel", (object?)lora.UserBaseModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsFavorite", lora.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("@NetworkDim", (object?)lora.NetworkDim ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NetworkAlpha", (object?)lora.NetworkAlpha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NetworkModule", (object?)lora.NetworkModule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LearningRate", (object?)lora.LearningRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UnetLearningRate", (object?)lora.UnetLearningRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TextEncoderLearningRate", (object?)lora.TextEncoderLearningRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Optimizer", (object?)lora.Optimizer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LrScheduler", (object?)lora.LrScheduler ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Epochs", (object?)lora.Epochs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TotalSteps", (object?)lora.TotalSteps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Resolution", (object?)lora.Resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Precision", (object?)lora.Precision ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileSizeBytes", lora.FileSizeBytes);
        cmd.Parameters.AddWithValue("@LastModifiedUtc", lora.LastModifiedUtc?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ThumbnailPath", (object?)lora.ThumbnailPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Sha256Hash", (object?)lora.Sha256Hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TrainedWordsJson", lora.TrainedWords != null && lora.TrainedWords.Count > 0 ? JsonSerializer.Serialize(lora.TrainedWords) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@RawMetadataJson", lora.RawHeaderMetadata != null && lora.RawHeaderMetadata.Count > 0 ? JsonSerializer.Serialize(lora.RawHeaderMetadata) : (object)DBNull.Value);

        if (lora.CivitaiInfo != null) {
            cmd.Parameters.AddWithValue("@CivitaiModelId", lora.CivitaiInfo.ModelId);
            cmd.Parameters.AddWithValue("@CivitaiVersionId", lora.CivitaiInfo.VersionId);
            cmd.Parameters.AddWithValue("@CivitaiModelName", (object?)lora.CivitaiInfo.ModelName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiVersionName", (object?)lora.CivitaiInfo.VersionName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiBaseModel", (object?)lora.CivitaiInfo.BaseModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiDescription", (object?)lora.CivitaiInfo.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiDownloadUrl", (object?)lora.CivitaiInfo.DownloadUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiUrl", (object?)lora.CivitaiInfo.CivitaiUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiPreviewImageUrl", (object?)lora.CivitaiInfo.PreviewImageUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiSamplePromptsJson", lora.CivitaiInfo.SamplePrompts != null && lora.CivitaiInfo.SamplePrompts.Count > 0 ? JsonSerializer.Serialize(lora.CivitaiInfo.SamplePrompts) : (object)DBNull.Value);
        } else {
            cmd.Parameters.AddWithValue("@CivitaiModelId", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiVersionId", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiModelName", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiVersionName", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiBaseModel", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiDescription", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiDownloadUrl", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiUrl", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiPreviewImageUrl", DBNull.Value);
            cmd.Parameters.AddWithValue("@CivitaiSamplePromptsJson", DBNull.Value);
        }

        cmd.Parameters.AddWithValue("@LibraryId", (object?)lora.LibraryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Category", (object?)lora.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TagsJson", lora.Tags != null && lora.Tags.Count > 0 ? JsonSerializer.Serialize(lora.Tags) : (object)DBNull.Value);
        string now = DateTime.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("@CreatedAtUtc", now);
        cmd.Parameters.AddWithValue("@UpdatedAtUtc", now);
    }

    public async Task<List<ShowcaseMediaItem>> GetAllGalleryMediaAsync(CancellationToken cancellationToken = default) {
        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM GalleryMedia ORDER BY CreatedDate DESC;";

            var list = new List<ShowcaseMediaItem>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                list.Add(MapReaderToGalleryMedia(reader));
            }
            return list;
        } finally {
            _lock.Release();
        }
    }

    public async Task UpsertGalleryMediaBatchAsync(IEnumerable<ShowcaseMediaItem> items, CancellationToken cancellationToken = default) {
        var itemList = items.ToList();
        if (itemList.Count == 0) return;

        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();

            if (connection is NpgsqlConnection npgsqlConn) {
                await UpsertGalleryMediaBatchToPostgreSqlAsync(npgsqlConn, itemList, cancellationToken);
                return;
            }

            if (connection is SqliteConnection sqliteConn) {
                using var tx = sqliteConn.BeginTransaction();

                const string sql = @"
                    INSERT INTO GalleryMedia (
                        FilePath, FileName, FileExtension, MediaType, FileSizeBytes, FormattedSize,
                        CreatedDate, FolderPath, FolderName, FileUrl, IsAiGenerated, AiGenerator,
                        Prompt, NegativePrompt, Seed, Steps, Sampler, Scheduler, CfgScale, ModelName,
                        UsedLorasJson, Width, Height, DurationSeconds, RawMetadataJson, Category,
                        UserTagsJson, IsFavorite, UserNotes, AssociatedLoraNamesJson, AssociatedLoraFilePathsJson,
                        UpdatedAtUtc
                    ) VALUES (
                        @FilePath, @FileName, @FileExtension, @MediaType, @FileSizeBytes, @FormattedSize,
                        @CreatedDate, @FolderPath, @FolderName, @FileUrl, @IsAiGenerated, @AiGenerator,
                        @Prompt, @NegativePrompt, @Seed, @Steps, @Sampler, @Scheduler, @CfgScale, @ModelName,
                        @UsedLorasJson, @Width, @Height, @DurationSeconds, @RawMetadataJson, @Category,
                        @UserTagsJson, @IsFavorite, @UserNotes, @AssociatedLoraNamesJson, @AssociatedLoraFilePathsJson,
                        @UpdatedAtUtc
                    ) ON CONFLICT(FilePath) DO UPDATE SET
                        FileName = excluded.FileName,
                        FileExtension = excluded.FileExtension,
                        MediaType = excluded.MediaType,
                        FileSizeBytes = excluded.FileSizeBytes,
                        FormattedSize = excluded.FormattedSize,
                        CreatedDate = excluded.CreatedDate,
                        FolderPath = excluded.FolderPath,
                        FolderName = excluded.FolderName,
                        FileUrl = excluded.FileUrl,
                        IsAiGenerated = excluded.IsAiGenerated,
                        AiGenerator = excluded.AiGenerator,
                        Prompt = excluded.Prompt,
                        NegativePrompt = excluded.NegativePrompt,
                        Seed = excluded.Seed,
                        Steps = excluded.Steps,
                        Sampler = excluded.Sampler,
                        Scheduler = excluded.Scheduler,
                        CfgScale = excluded.CfgScale,
                        ModelName = excluded.ModelName,
                        UsedLorasJson = excluded.UsedLorasJson,
                        Width = excluded.Width,
                        Height = excluded.Height,
                        DurationSeconds = excluded.DurationSeconds,
                        RawMetadataJson = excluded.RawMetadataJson,
                        Category = excluded.Category,
                        UserTagsJson = excluded.UserTagsJson,
                        IsFavorite = excluded.IsFavorite,
                        UserNotes = excluded.UserNotes,
                        AssociatedLoraNamesJson = excluded.AssociatedLoraNamesJson,
                        AssociatedLoraFilePathsJson = excluded.AssociatedLoraFilePathsJson,
                        UpdatedAtUtc = excluded.UpdatedAtUtc;
                ";

                foreach (var item in itemList) {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    BindGalleryMediaParameters(cmd, item);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
        } finally {
            _lock.Release();
        }
    }

    private static async Task UpsertGalleryMediaBatchToPostgreSqlAsync(NpgsqlConnection conn, IEnumerable<ShowcaseMediaItem> items, CancellationToken cancellationToken) {
        const string pgSql = @"
            INSERT INTO GalleryMedia (
                FilePath, FileName, FileExtension, MediaType, FileSizeBytes, FormattedSize,
                CreatedDate, FolderPath, FolderName, FileUrl, IsAiGenerated, AiGenerator,
                Prompt, NegativePrompt, Seed, Steps, Sampler, Scheduler, CfgScale, ModelName,
                UsedLorasJson, Width, Height, DurationSeconds, RawMetadataJson, Category,
                UserTagsJson, IsFavorite, UserNotes, AssociatedLoraNamesJson, AssociatedLoraFilePathsJson,
                UpdatedAtUtc
            ) VALUES (
                @FilePath, @FileName, @FileExtension, @MediaType, @FileSizeBytes, @FormattedSize,
                @CreatedDate, @FolderPath, @FolderName, @FileUrl, @IsAiGenerated, @AiGenerator,
                @Prompt, @NegativePrompt, @Seed, @Steps, @Sampler, @Scheduler, @CfgScale, @ModelName,
                @UsedLorasJson, @Width, @Height, @DurationSeconds, @RawMetadataJson, @Category,
                @UserTagsJson, @IsFavorite, @UserNotes, @AssociatedLoraNamesJson, @AssociatedLoraFilePathsJson,
                @UpdatedAtUtc
            ) ON CONFLICT (FilePath) DO UPDATE SET
                FileName = EXCLUDED.FileName,
                FileExtension = EXCLUDED.FileExtension,
                MediaType = EXCLUDED.MediaType,
                FileSizeBytes = EXCLUDED.FileSizeBytes,
                FormattedSize = EXCLUDED.FormattedSize,
                CreatedDate = EXCLUDED.CreatedDate,
                FolderPath = EXCLUDED.FolderPath,
                FolderName = EXCLUDED.FolderName,
                FileUrl = EXCLUDED.FileUrl,
                IsAiGenerated = EXCLUDED.IsAiGenerated,
                AiGenerator = EXCLUDED.AiGenerator,
                Prompt = EXCLUDED.Prompt,
                NegativePrompt = EXCLUDED.NegativePrompt,
                Seed = EXCLUDED.Seed,
                Steps = EXCLUDED.Steps,
                Sampler = EXCLUDED.Sampler,
                Scheduler = EXCLUDED.Scheduler,
                CfgScale = EXCLUDED.CfgScale,
                ModelName = EXCLUDED.ModelName,
                UsedLorasJson = EXCLUDED.UsedLorasJson,
                Width = EXCLUDED.Width,
                Height = EXCLUDED.Height,
                DurationSeconds = EXCLUDED.DurationSeconds,
                RawMetadataJson = EXCLUDED.RawMetadataJson,
                Category = EXCLUDED.Category,
                UserTagsJson = EXCLUDED.UserTagsJson,
                IsFavorite = EXCLUDED.IsFavorite,
                UserNotes = EXCLUDED.UserNotes,
                AssociatedLoraNamesJson = EXCLUDED.AssociatedLoraNamesJson,
                AssociatedLoraFilePathsJson = EXCLUDED.AssociatedLoraFilePathsJson,
                UpdatedAtUtc = EXCLUDED.UpdatedAtUtc;
        ";

        using var tx = await conn.BeginTransactionAsync(cancellationToken);
        foreach (var item in items) {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = pgSql;
            BindGalleryMediaParameters(cmd, item);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    private static void BindGalleryMediaParameters(DbCommand cmd, ShowcaseMediaItem item) {
        AddParam(cmd, "@FilePath", item.FilePath);
        AddParam(cmd, "@FileName", item.FileName);
        AddParam(cmd, "@FileExtension", item.FileExtension);
        AddParam(cmd, "@MediaType", (int)item.MediaType);
        AddParam(cmd, "@FileSizeBytes", item.FileSizeBytes);
        AddParam(cmd, "@FormattedSize", item.FormattedSize);
        AddParam(cmd, "@CreatedDate", item.CreatedDate.ToString("O"));
        AddParam(cmd, "@FolderPath", item.FolderPath);
        AddParam(cmd, "@FolderName", item.FolderName);
        AddParam(cmd, "@FileUrl", item.FileUrl);
        AddParam(cmd, "@IsAiGenerated", item.IsAiGenerated ? 1 : 0);
        AddParam(cmd, "@AiGenerator", item.AiGenerator);
        AddParam(cmd, "@Prompt", item.Prompt);
        AddParam(cmd, "@NegativePrompt", item.NegativePrompt);
        AddParam(cmd, "@Seed", item.Seed);
        AddParam(cmd, "@Steps", item.Steps);
        AddParam(cmd, "@Sampler", item.Sampler);
        AddParam(cmd, "@Scheduler", item.Scheduler);
        AddParam(cmd, "@CfgScale", item.CfgScale);
        AddParam(cmd, "@ModelName", item.ModelName);
        AddParam(cmd, "@UsedLorasJson", item.UsedLoras != null && item.UsedLoras.Count > 0 ? JsonSerializer.Serialize(item.UsedLoras) : null);
        AddParam(cmd, "@Width", item.Width);
        AddParam(cmd, "@Height", item.Height);
        AddParam(cmd, "@DurationSeconds", item.Duration?.TotalSeconds);
        AddParam(cmd, "@RawMetadataJson", item.RawMetadata != null && item.RawMetadata.Count > 0 ? JsonSerializer.Serialize(item.RawMetadata) : null);
        AddParam(cmd, "@Category", item.Category);
        AddParam(cmd, "@UserTagsJson", item.UserTags != null && item.UserTags.Count > 0 ? JsonSerializer.Serialize(item.UserTags) : null);
        AddParam(cmd, "@IsFavorite", item.IsFavorite ? 1 : 0);
        AddParam(cmd, "@UserNotes", item.UserNotes);
        AddParam(cmd, "@AssociatedLoraNamesJson", item.AssociatedLoraNames != null && item.AssociatedLoraNames.Count > 0 ? JsonSerializer.Serialize(item.AssociatedLoraNames) : null);
        AddParam(cmd, "@AssociatedLoraFilePathsJson", item.AssociatedLoraFilePaths != null && item.AssociatedLoraFilePaths.Count > 0 ? JsonSerializer.Serialize(item.AssociatedLoraFilePaths) : null);
        AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));
    }

    public async Task ToggleGalleryFavoriteAsync(string filePath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE GalleryMedia 
                SET IsFavorite = CASE WHEN IsFavorite = 1 THEN 0 ELSE 1 END,
                    UpdatedAtUtc = @UpdatedAtUtc
                WHERE FilePath = @FilePath;
            ";
            AddParam(cmd, "@FilePath", filePath);
            AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        } finally {
            _lock.Release();
        }
    }

    public async Task UpdateGalleryMediaItemAsync(ShowcaseMediaItem item, CancellationToken cancellationToken = default) {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE GalleryMedia 
                SET Category = @Category,
                    UserTagsJson = @UserTagsJson,
                    IsFavorite = @IsFavorite,
                    UserNotes = @UserNotes,
                    AssociatedLoraNamesJson = @AssociatedLoraNamesJson,
                    AssociatedLoraFilePathsJson = @AssociatedLoraFilePathsJson,
                    UpdatedAtUtc = @UpdatedAtUtc
                WHERE FilePath = @FilePath;
            ";
            AddParam(cmd, "@FilePath", item.FilePath);
            AddParam(cmd, "@Category", item.Category);
            AddParam(cmd, "@UserTagsJson", item.UserTags != null && item.UserTags.Count > 0 ? JsonSerializer.Serialize(item.UserTags) : null);
            AddParam(cmd, "@IsFavorite", item.IsFavorite ? 1 : 0);
            AddParam(cmd, "@UserNotes", item.UserNotes);
            AddParam(cmd, "@AssociatedLoraNamesJson", item.AssociatedLoraNames != null && item.AssociatedLoraNames.Count > 0 ? JsonSerializer.Serialize(item.AssociatedLoraNames) : null);
            AddParam(cmd, "@AssociatedLoraFilePathsJson", item.AssociatedLoraFilePaths != null && item.AssociatedLoraFilePaths.Count > 0 ? JsonSerializer.Serialize(item.AssociatedLoraFilePaths) : null);
            AddParam(cmd, "@UpdatedAtUtc", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        } finally {
            _lock.Release();
        }
    }

    public async Task RemoveGalleryMediaAsync(string filePath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM GalleryMedia WHERE FilePath = @FilePath;";
            AddParam(cmd, "@FilePath", filePath);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        } finally {
            _lock.Release();
        }
    }

    public async Task RemoveGalleryMediaByFolderAsync(string folderPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        await EnsureInitializedAsync();
        await _lock.WaitAsync(cancellationToken);
        try {
            using var connection = await OpenConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM GalleryMedia WHERE FolderPath = @FolderPath OR FilePath LIKE @FolderPrefix;";
            AddParam(cmd, "@FolderPath", folderPath);
            AddParam(cmd, "@FolderPrefix", folderPath.TrimEnd('\\', '/') + "%");
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        } finally {
            _lock.Release();
        }
    }

    private static ShowcaseMediaItem MapReaderToGalleryMedia(DbDataReader reader) {
        var item = new ShowcaseMediaItem {
            FilePath = reader.GetString(reader.GetOrdinal("FilePath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            FileExtension = reader.GetString(reader.GetOrdinal("FileExtension")),
            MediaType = (ShowcaseMediaType)reader.GetInt32(reader.GetOrdinal("MediaType")),
            FileSizeBytes = reader.GetInt64(reader.GetOrdinal("FileSizeBytes")),
            FormattedSize = reader.IsDBNull(reader.GetOrdinal("FormattedSize")) ? "0 B" : reader.GetString(reader.GetOrdinal("FormattedSize")),
            CreatedDate = DateTime.TryParse(reader.GetString(reader.GetOrdinal("CreatedDate")), out var cd) ? cd : DateTime.UtcNow,
            FolderPath = reader.GetString(reader.GetOrdinal("FolderPath")),
            FolderName = reader.IsDBNull(reader.GetOrdinal("FolderName")) ? string.Empty : reader.GetString(reader.GetOrdinal("FolderName")),
            FileUrl = reader.IsDBNull(reader.GetOrdinal("FileUrl")) ? string.Empty : reader.GetString(reader.GetOrdinal("FileUrl")),
            IsAiGenerated = reader.GetInt32(reader.GetOrdinal("IsAiGenerated")) == 1,
            AiGenerator = reader.IsDBNull(reader.GetOrdinal("AiGenerator")) ? string.Empty : reader.GetString(reader.GetOrdinal("AiGenerator")),
            Prompt = reader.IsDBNull(reader.GetOrdinal("Prompt")) ? string.Empty : reader.GetString(reader.GetOrdinal("Prompt")),
            NegativePrompt = reader.IsDBNull(reader.GetOrdinal("NegativePrompt")) ? string.Empty : reader.GetString(reader.GetOrdinal("NegativePrompt")),
            Seed = reader.IsDBNull(reader.GetOrdinal("Seed")) ? null : reader.GetInt64(reader.GetOrdinal("Seed")),
            Steps = reader.IsDBNull(reader.GetOrdinal("Steps")) ? null : reader.GetInt32(reader.GetOrdinal("Steps")),
            Sampler = reader.IsDBNull(reader.GetOrdinal("Sampler")) ? string.Empty : reader.GetString(reader.GetOrdinal("Sampler")),
            Scheduler = reader.IsDBNull(reader.GetOrdinal("Scheduler")) ? string.Empty : reader.GetString(reader.GetOrdinal("Scheduler")),
            CfgScale = reader.IsDBNull(reader.GetOrdinal("CfgScale")) ? null : reader.GetDouble(reader.GetOrdinal("CfgScale")),
            ModelName = reader.IsDBNull(reader.GetOrdinal("ModelName")) ? string.Empty : reader.GetString(reader.GetOrdinal("ModelName")),
            Width = reader.IsDBNull(reader.GetOrdinal("Width")) ? null : reader.GetInt32(reader.GetOrdinal("Width")),
            Height = reader.IsDBNull(reader.GetOrdinal("Height")) ? null : reader.GetInt32(reader.GetOrdinal("Height")),
            Category = reader.IsDBNull(reader.GetOrdinal("Category")) ? "Uncategorized" : reader.GetString(reader.GetOrdinal("Category")),
            IsFavorite = reader.GetInt32(reader.GetOrdinal("IsFavorite")) == 1,
            UserNotes = reader.IsDBNull(reader.GetOrdinal("UserNotes")) ? string.Empty : reader.GetString(reader.GetOrdinal("UserNotes"))
        };

        int durOrd = reader.GetOrdinal("DurationSeconds");
        if (!reader.IsDBNull(durOrd)) {
            item.Duration = TimeSpan.FromSeconds(reader.GetDouble(durOrd));
        }

        int usedLorasOrd = reader.GetOrdinal("UsedLorasJson");
        if (!reader.IsDBNull(usedLorasOrd)) {
            try {
                item.UsedLoras = JsonSerializer.Deserialize<List<string>>(reader.GetString(usedLorasOrd)) ?? new();
            } catch { }
        }

        int rawMetaOrd = reader.GetOrdinal("RawMetadataJson");
        if (!reader.IsDBNull(rawMetaOrd)) {
            try {
                item.RawMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(rawMetaOrd)) ?? new();
            } catch { }
        }

        int tagsOrd = reader.GetOrdinal("UserTagsJson");
        if (!reader.IsDBNull(tagsOrd)) {
            try {
                item.UserTags = JsonSerializer.Deserialize<List<string>>(reader.GetString(tagsOrd)) ?? new();
            } catch { }
        }

        int loraNamesOrd = reader.GetOrdinal("AssociatedLoraNamesJson");
        if (!reader.IsDBNull(loraNamesOrd)) {
            try {
                item.AssociatedLoraNames = JsonSerializer.Deserialize<List<string>>(reader.GetString(loraNamesOrd)) ?? new();
            } catch { }
        }

        int loraPathsOrd = reader.GetOrdinal("AssociatedLoraFilePathsJson");
        if (!reader.IsDBNull(loraPathsOrd)) {
            try {
                item.AssociatedLoraFilePaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(loraPathsOrd)) ?? new();
            } catch { }
        }

        return item;
    }

    public async Task ExportDatabaseAsync(string targetFilePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        await EnsureInitializedAsync();

        string? dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(targetFilePath)) {
            File.Delete(targetFilePath);
        }

        if (IsPostgreSql) {
            if (File.Exists(_dbPath)) {
                File.Copy(_dbPath, targetFilePath, overwrite: true);
            }
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{targetFilePath.Replace("'", "''")}';";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<DatabaseImportAnalysis> AnalyzeDatabaseForImportAsync(string sourceDbPath) {
        var analysis = new DatabaseImportAnalysis();
        if (!File.Exists(sourceDbPath)) {
            analysis.IsValid = false;
            analysis.ErrorMessage = $"Source file not found at '{sourceDbPath}'.";
            return analysis;
        }

        analysis.FileSizeBytes = new FileInfo(sourceDbPath).Length;
        await EnsureInitializedAsync();

        try {
            var srcBuilder = new SqliteConnectionStringBuilder {
                DataSource = sourceDbPath,
                Mode = SqliteOpenMode.ReadOnly
            };

            using var srcConn = new SqliteConnection(srcBuilder.ToString());
            await srcConn.OpenAsync();

            var srcTables = new List<string>();
            using (var cmd = srcConn.CreateCommand()) {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    srcTables.Add(reader.GetString(0));
                }
            }

            var destTables = new List<string>();
            using var destConn = new SqliteConnection(_connectionString);
            await destConn.OpenAsync();
            using (var cmd = destConn.CreateCommand()) {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    destTables.Add(reader.GetString(0));
                }
            }

            foreach (var tbl in srcTables) {
                if (destTables.Contains(tbl, StringComparer.OrdinalIgnoreCase)) {
                    analysis.MatchingTables.Add(tbl);
                } else {
                    analysis.ExtraTables.Add(tbl);
                }

                try {
                    using var countCmd = srcConn.CreateCommand();
                    countCmd.CommandText = $"SELECT COUNT(*) FROM \"{tbl.Replace("\"", "\"\"")}\";";
                    var count = await countCmd.ExecuteScalarAsync();
                    analysis.SourceRowCounts[tbl] = Convert.ToInt32(count);
                } catch {
                    analysis.SourceRowCounts[tbl] = 0;
                }
            }

            foreach (var tbl in destTables) {
                if (!srcTables.Contains(tbl, StringComparer.OrdinalIgnoreCase)) {
                    analysis.MissingTables.Add(tbl);
                }
            }

            foreach (var tbl in analysis.MatchingTables) {
                var srcCols = await GetTableColumnsAsync(srcConn, tbl);
                var destCols = await GetTableColumnsAsync(destConn, tbl);

                var srcColNames = srcCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var destColNames = destCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var missingInDest = srcCols.Where(c => !destColNames.Contains(c.Name)).Select(c => c.Name).ToList();
                if (missingInDest.Count > 0) {
                    analysis.MissingColumnsInDestination[tbl] = missingInDest;
                }

                var missingInSrc = destCols.Where(c => !srcColNames.Contains(c.Name)).Select(c => c.Name).ToList();
                if (missingInSrc.Count > 0) {
                    analysis.MissingColumnsInSource[tbl] = missingInSrc;
                }
            }

            analysis.IsValid = analysis.MatchingTables.Count > 0;
            if (!analysis.IsValid) {
                analysis.ErrorMessage = "The selected file does not appear to be a valid LoRAMancer SQLite database (no matching tables found).";
            }
        } catch (Exception ex) {
            analysis.IsValid = false;
            analysis.ErrorMessage = $"Failed to analyze database: {ex.Message}";
        }

        return analysis;
    }

    private static async Task<List<(string Name, string Type)>> GetTableColumnsAsync(SqliteConnection conn, string tableName) {
        var cols = new List<(string Name, string Type)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\");";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) {
            string name = reader.GetString(1);
            string type = reader.IsDBNull(2) ? "TEXT" : reader.GetString(2);
            cols.Add((name, type));
        }
        return cols;
    }

    public async Task<(int TablesImported, int RowsImported, List<string> AppliedResolutions)> ImportAndMergeDatabaseAsync(string sourceDbPath, bool autoAddMissingColumns = true) {
        var resolutions = new List<string>();
        int totalTables = 0;
        int totalRows = 0;

        await EnsureInitializedAsync();
        var analysis = await AnalyzeDatabaseForImportAsync(sourceDbPath);
        if (!analysis.IsValid) {
            throw new InvalidOperationException(analysis.ErrorMessage);
        }

        var srcBuilder = new SqliteConnectionStringBuilder {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly
        };

        using var srcConn = new SqliteConnection(srcBuilder.ToString());
        await srcConn.OpenAsync();

        using var destConn = new SqliteConnection(_connectionString);
        await destConn.OpenAsync();

        if (autoAddMissingColumns && analysis.MissingColumnsInDestination.Count > 0) {
            foreach (var kvp in analysis.MissingColumnsInDestination) {
                string tableName = kvp.Key;
                var srcCols = await GetTableColumnsAsync(srcConn, tableName);
                foreach (string colName in kvp.Value) {
                    var colDef = srcCols.FirstOrDefault(c => string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
                    string colType = !string.IsNullOrWhiteSpace(colDef.Type) ? colDef.Type : "TEXT";
                    try {
                        using var alterCmd = destConn.CreateCommand();
                        alterCmd.CommandText = $"ALTER TABLE \"{tableName.Replace("\"", "\"\"")}\" ADD COLUMN \"{colName.Replace("\"", "\"\"")}\" {colType};";
                        await alterCmd.ExecuteNonQueryAsync();
                        resolutions.Add($"Added column '{colName}' ({colType}) to '{tableName}'");
                    } catch (Exception ex) {
                        resolutions.Add($"Could not add column '{colName}' to '{tableName}': {ex.Message}");
                    }
                }
            }
        }

        foreach (string tableName in analysis.MatchingTables) {
            var srcCols = await GetTableColumnsAsync(srcConn, tableName);
            var destCols = await GetTableColumnsAsync(destConn, tableName);

            var destColNames = destCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonCols = srcCols.Where(c => destColNames.Contains(c.Name)).Select(c => c.Name).ToList();

            if (commonCols.Count == 0) {
                continue;
            }

            string escapedTable = $"\"{tableName.Replace("\"", "\"\"")}\"";
            string colList = string.Join(", ", commonCols.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
            string paramList = string.Join(", ", commonCols.Select((_, idx) => $"@p{idx}"));

            using var selectCmd = srcConn.CreateCommand();
            selectCmd.CommandText = $"SELECT {colList} FROM {escapedTable};";
            using var reader = await selectCmd.ExecuteReaderAsync();

            using var tx = destConn.BeginTransaction();
            try {
                using var insertCmd = destConn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = $"INSERT OR REPLACE INTO {escapedTable} ({colList}) VALUES ({paramList});";

                var parameters = new List<SqliteParameter>();
                for (int i = 0; i < commonCols.Count; i++) {
                    var p = insertCmd.CreateParameter();
                    p.ParameterName = $"@p{i}";
                    insertCmd.Parameters.Add(p);
                    parameters.Add(p);
                }

                int tableRowCount = 0;
                while (await reader.ReadAsync()) {
                    for (int i = 0; i < commonCols.Count; i++) {
                        parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    }
                    await insertCmd.ExecuteNonQueryAsync();
                    tableRowCount++;
                }

                await tx.CommitAsync();
                totalTables++;
                totalRows += tableRowCount;
            } catch {
                await tx.RollbackAsync();
                throw;
            }
        }

        return (totalTables, totalRows, resolutions);
    }

    public async Task<bool> ReplaceDatabaseAsync(string sourceDbPath) {
        if (!File.Exists(sourceDbPath)) {
            throw new FileNotFoundException("Source database file not found.", sourceDbPath);
        }

        await EnsureInitializedAsync();

        string backupPath = _dbPath + $".backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        try {
            if (File.Exists(_dbPath)) {
                File.Copy(_dbPath, backupPath, overwrite: true);
            }
        } catch { }

        SqliteConnection.ClearAllPools();

        File.Copy(sourceDbPath, _dbPath, overwrite: true);
        if (File.Exists(_dbPath + "-wal")) {
            try {
                File.Delete(_dbPath + "-wal");
            } catch { }
        }
        if (File.Exists(_dbPath + "-shm")) {
            try {
                File.Delete(_dbPath + "-shm");
            } catch { }
        }

        _initialized = false;
        await EnsureInitializedAsync();

        return true;
    }

    public void Dispose() {
        _lock.Dispose();
    }
}

public sealed class DatabaseImportAnalysis {
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public List<string> MatchingTables { get; set; } = new();
    public List<string> ExtraTables { get; set; } = new();
    public List<string> MissingTables { get; set; } = new();
    public Dictionary<string, int> SourceRowCounts { get; set; } = new();
    public Dictionary<string, List<string>> MissingColumnsInDestination { get; set; } = new();
    public Dictionary<string, List<string>> MissingColumnsInSource { get; set; } = new();
}
