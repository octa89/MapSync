using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WpfMapApp1;

namespace POSM_MR3_2
{
    public class PosmDatabaseService
    {
        private readonly ConcurrentDictionary<string, bool> _inspectionCache = new();
        private readonly SemaphoreSlim _dbSemaphore = new(1, 1);
        private readonly Config _config;
        private bool _aceProviderAvailable = true;
        private string? _lastProviderError;

        public PosmDatabaseService(Config config)
        {
            _config = config;
            CheckAceProviderAvailability();
        }

        public string? PosmDbPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_config.posmExecutablePath))
                    return null;
                    
                var directory = Path.GetDirectoryName(_config.posmExecutablePath);
                return directory != null ? Path.Combine(directory, "POSM.mdb") : null;
            }
        }

        public bool IsAvailable
        {
            get
            {
                var dbPath = PosmDbPath;
                return !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath);
            }
        }

        public string GetUnavailabilityReason()
        {
            if (!_aceProviderAvailable)
                return $"ACE database provider unavailable: {_lastProviderError}";
                
            var dbPath = PosmDbPath;
            if (string.IsNullOrEmpty(dbPath))
                return "POSM executable path not configured";
                
            if (!File.Exists(dbPath))
                return $"POSM.mdb not found at: {dbPath}";
                
            return "Database unavailable";
        }

        public async Task<bool> HasLatestInspectionAsync(string assetId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(assetId) || !IsAvailable)
                return false;

            var normalizedId = NormalizeId(assetId);
            
            // Check cache first
            if (_inspectionCache.TryGetValue(normalizedId, out bool cachedResult))
            {
                return cachedResult;
            }

            await _dbSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Double-check cache after acquiring semaphore
                if (_inspectionCache.TryGetValue(normalizedId, out cachedResult))
                {
                    return cachedResult;
                }

                var result = await CheckDatabaseForInspectionAsync(assetId, normalizedId, cancellationToken);
                
                // Cache positive and negative results
                _inspectionCache[normalizedId] = result;
                
                return result;
            }
            finally
            {
                _dbSemaphore.Release();
            }
        }

        private async Task<bool> CheckDatabaseForInspectionAsync(string originalId, string normalizedId, CancellationToken cancellationToken)
        {
            var dbPath = PosmDbPath;
            if (string.IsNullOrEmpty(dbPath))
                return false;

            // Providers to try (16.0 then 12.0)
            var providers = new[] { "Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0" };

            // Query matches any inspection (align with highlighter logic) - avoid Session table
            const string anyQuery = @"
                SELECT TOP 1 sf.SessionID 
                FROM SpecialFields sf 
                WHERE (
                    UCase(Trim(sf.AssetID)) = @normalizedId 
                    OR (Val(sf.AssetID) = Val(@originalId) AND Val(@originalId) > 0)
                )";

            foreach (var provider in providers)
            {
                try
                {
                    var connectionString = $"Provider={provider};Data Source={dbPath};";
                    using var connection = new OleDbConnection(connectionString);
                    using var command = new OleDbCommand(anyQuery, connection);
                    command.Parameters.AddWithValue("@normalizedId", normalizedId);
                    command.Parameters.AddWithValue("@originalId", originalId);
                    command.CommandTimeout = 3; // 3 second timeout

                    await connection.OpenAsync(cancellationToken);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));

                    var result = await command.ExecuteScalarAsync(cts.Token);
                    if (result != null && result != DBNull.Value)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] Inspection exists for '{originalId}'. Provider={provider}");
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[PosmDB] Timeout checking inspection for '{originalId}'");
                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PosmDB] Provider {provider} failed during inspection check: {ex.Message}");
                    // try next provider
                }
            }
            return false;
        }

        private void CheckAceProviderAvailability()
        {
            try
            {
                // Defer actual provider testing to query time; assume available if file exists
                _aceProviderAvailable = true;
                _lastProviderError = null;
            }
            catch (Exception ex)
            {
                _aceProviderAvailable = false;
                _lastProviderError = ex.Message;
                System.Diagnostics.Debug.WriteLine($"[PosmDB] ACE provider unavailable: {ex.Message}");
            }
        }

        private static string NormalizeId(string id)
        {
            return id?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        private static bool IsNumericLike(string value)
        {
            return !string.IsNullOrEmpty(value) && value.All(char.IsDigit);
        }

        public void ClearCache()
        {
            _inspectionCache.Clear();
        }

        public async Task<List<VideoInfo>> GetVideoPathsForAssetAsync(string assetId, CancellationToken cancellationToken = default)
        {
            var videoPaths = new List<VideoInfo>();
            
            Console.WriteLine($"[POSM VIDEO DEBUG] === Starting video search for asset: {assetId} ===");
            
            if (string.IsNullOrWhiteSpace(assetId))
            {
                Console.WriteLine("[POSM VIDEO DEBUG] Asset ID is null or empty - returning no videos");
                return videoPaths;
            }
            
            if (!IsAvailable)
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] Database not available - reason: {GetUnavailabilityReason()}");
                return videoPaths;
            }

            var dbPath = PosmDbPath;
            if (string.IsNullOrEmpty(dbPath))
            {
                Console.WriteLine("[POSM VIDEO DEBUG] Database path is null or empty");
                return videoPaths;
            }

            Console.WriteLine($"[POSM VIDEO DEBUG] Database path: {dbPath}");
            Console.WriteLine($"[POSM VIDEO DEBUG] Database exists: {File.Exists(dbPath)}");

            // Access queries - direct approach using MediaFolder from SpecialFields and VideoLocation from Data
            const string anyQuery = @"
                SELECT sf.MediaFolder, d.VideoLocation 
                FROM SpecialFields sf 
                INNER JOIN Data d ON sf.SessionID = d.SessionID 
                WHERE (
                    UCase(Trim(sf.AssetID)) = @normalizedId 
                    OR (Val(sf.AssetID) = Val(@originalId) AND Val(@originalId) > 0)
                )";

            try
            {
                var normalizedId = NormalizeId(assetId);
                var providers = new[] { "Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0" };
                
                Console.WriteLine($"[POSM VIDEO DEBUG] Original Asset ID: '{assetId}'");
                Console.WriteLine($"[POSM VIDEO DEBUG] Normalized Asset ID: '{normalizedId}'");
                Console.WriteLine($"[POSM VIDEO DEBUG] SQL Query: {anyQuery}");
                
                System.Diagnostics.Debug.WriteLine($"[PosmDB] GetVideoPathsForAssetAsync: asset='{assetId}', normalized='{normalizedId}', db='{dbPath}'");
                System.Console.WriteLine($"[POSM Videos] DB path: {dbPath}");

                foreach (var provider in providers)
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] Trying provider: {provider}");
                    try
                    {
                        var connectionString = $"Provider={provider};Data Source={dbPath};";
                        Console.WriteLine($"[POSM VIDEO DEBUG] Connection string: {connectionString}");
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] Trying provider: {provider}");
                        System.Console.WriteLine($"[POSM Videos] Provider: {provider}");
                        using var connection = new OleDbConnection(connectionString);
                        await connection.OpenAsync(cancellationToken);
                        Console.WriteLine($"[POSM VIDEO DEBUG] Successfully connected to database with {provider}");

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));

                        using var command = new OleDbCommand(anyQuery, connection);
                        command.Parameters.AddWithValue("@normalizedId", normalizedId);
                        command.Parameters.AddWithValue("@originalId", assetId);
                        command.CommandTimeout = 3;

                        using var reader = await command.ExecuteReaderAsync(cts.Token);
                        Console.WriteLine($"[POSM VIDEO DEBUG] Query executed successfully");

                        var baseDirectory = Path.GetDirectoryName(_config.posmExecutablePath);
                        if (string.IsNullOrEmpty(baseDirectory))
                        {
                            Console.WriteLine("[POSM VIDEO DEBUG] Base directory is null or empty - cannot construct video paths");
                            return videoPaths;
                        }
                        
                        // Log expected template and both base roots we might see
                        Console.WriteLine($"[POSM VIDEO DEBUG] POSM executable path: {_config.posmExecutablePath}");
                        Console.WriteLine($"[POSM VIDEO DEBUG] Base directory (from exe): {baseDirectory}");
                        Console.WriteLine($"[POSM VIDEO DEBUG] Base directory exists: {Directory.Exists(baseDirectory)}");
                        
                        var baseVideoDirectory = Path.Combine(baseDirectory, "Video");
                        Console.WriteLine($"[POSM VIDEO DEBUG] Video base directory: {baseVideoDirectory}");
                        Console.WriteLine($"[POSM VIDEO DEBUG] Video base directory exists: {Directory.Exists(baseVideoDirectory)}");
                        
                        System.Diagnostics.Debug.WriteLine("[PosmDB] Expected path template: C:\\POSM\\Video\\{media folder}\\{Video Location}");
                        System.Console.WriteLine("[POSM Videos] Expected template: C:\\POSM\\Video\\{media folder}\\{Video Location}");
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] POSM base directory (from exe): {baseDirectory}");
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] POSM video base (exe\\Video): {baseVideoDirectory}");
                        System.Console.WriteLine($"[POSM Videos] POSM base: {baseDirectory}");
                        System.Console.WriteLine($"[POSM Videos] POSM video base: {baseVideoDirectory}");

                        int rowCount = 0;
                        while (await reader.ReadAsync(cts.Token))
                        {
                            rowCount++;
                            Console.WriteLine($"[POSM VIDEO DEBUG] Processing database row #{rowCount}");
                            
                            var mediaFolder = reader["MediaFolder"]?.ToString() ?? string.Empty;
                            var videoLocation = reader["VideoLocation"]?.ToString() ?? string.Empty;
                            
                            Console.WriteLine($"[POSM VIDEO DEBUG] Raw MediaFolder: '{mediaFolder}'");
                            Console.WriteLine($"[POSM VIDEO DEBUG] Raw VideoLocation: '{videoLocation}'");
                            
                            if (!string.IsNullOrWhiteSpace(mediaFolder) && !string.IsNullOrWhiteSpace(videoLocation))
                            {
                                // Current logic: baseDirectory + mediaFolder + videoLocation
                                var currentPath = Path.GetFullPath(Path.Combine(baseDirectory, mediaFolder, videoLocation));
                                var currentExists = File.Exists(currentPath);

                                // Expected logic per convention: baseDirectory\\Video + mediaFolder + videoLocation
                                var expectedPath = Path.GetFullPath(Path.Combine(baseVideoDirectory, mediaFolder, videoLocation));
                                var expectedExists = File.Exists(expectedPath);

                                // Use the correct path - expected path includes Video subdirectory
                                videoPaths.Add(new VideoInfo(expectedPath, mediaFolder, videoLocation));

                                Console.WriteLine($"[POSM VIDEO DEBUG] Current path: {currentPath}");
                                Console.WriteLine($"[POSM VIDEO DEBUG] Current path exists: {currentExists}");
                                Console.WriteLine($"[POSM VIDEO DEBUG] Expected path: {expectedPath}");
                                Console.WriteLine($"[POSM VIDEO DEBUG] Expected path exists: {expectedExists}");
                                Console.WriteLine($"[POSM VIDEO DEBUG] Using expected path: {expectedPath}");

                                System.Diagnostics.Debug.WriteLine("[PosmDB] Video row:");
                                System.Diagnostics.Debug.WriteLine($"  - MediaFolder: {mediaFolder}");
                                System.Diagnostics.Debug.WriteLine($"  - VideoLocation: {videoLocation}");
                                System.Diagnostics.Debug.WriteLine($"  - Using (current): {currentPath} (exists={currentExists})");
                                System.Diagnostics.Debug.WriteLine($"  - Expected (exe\\Video): {expectedPath} (exists={expectedExists})");

                                System.Console.WriteLine("[POSM Videos] Row:");
                                System.Console.WriteLine($"  - MediaFolder: {mediaFolder}");
                                System.Console.WriteLine($"  - VideoLocation: {videoLocation}");
                                System.Console.WriteLine($"  - Using (current): {currentPath} (exists={currentExists})");
                                System.Console.WriteLine($"  - Expected (exe\\Video): {expectedPath} (exists={expectedExists})");
                            }
                            else
                            {
                                Console.WriteLine($"[POSM VIDEO DEBUG] Skipping row - empty MediaFolder or VideoLocation");
                            }
                        }

                        Console.WriteLine($"[POSM VIDEO DEBUG] Total database rows processed: {rowCount}");
                        Console.WriteLine($"[POSM VIDEO DEBUG] Total video paths found: {videoPaths.Count}");
                        
                        if (videoPaths.Count > 0)
                        {
                            var existingCount = videoPaths.Count(v => File.Exists(v.FilePath));
                            Console.WriteLine($"[POSM VIDEO DEBUG] Videos that exist on disk: {existingCount} out of {videoPaths.Count}");
                            
                            System.Diagnostics.Debug.WriteLine($"[PosmDB] Found {videoPaths.Count} video paths for asset '{assetId}' (Provider={provider})");
                            System.Console.WriteLine($"[POSM Videos] Found {videoPaths.Count} candidate video path(s)");
                            return videoPaths;
                        }
                        else
                        {
                            Console.WriteLine($"[POSM VIDEO DEBUG] No video paths found for asset '{assetId}' with provider {provider}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] Timeout getting video paths for '{assetId}'");
                        return videoPaths;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[POSM VIDEO DEBUG] Provider {provider} failed: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[PosmDB] Provider {provider} failed while getting videos: {ex.Message}");
                        System.Console.WriteLine($"[POSM Videos] Provider {provider} failed: {ex.Message}");
                        // try next provider
                    }
                }

                return videoPaths;
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[PosmDB] Timeout getting video paths for '{assetId}'");
                return videoPaths;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PosmDB] Error getting video paths for '{assetId}': {ex.Message}");
                return videoPaths;
            }
        }
        
        public void LogDiagnostics()
        {
            var dbPath = PosmDbPath;
            System.Diagnostics.Debug.WriteLine($"[PosmDB] Diagnostics:");
            System.Diagnostics.Debug.WriteLine($"  - POSM Executable: {_config.posmExecutablePath}");
            System.Diagnostics.Debug.WriteLine($"  - Database Path: {dbPath}");
            System.Diagnostics.Debug.WriteLine($"  - File Exists: {(dbPath != null ? File.Exists(dbPath) : false)}");
            System.Diagnostics.Debug.WriteLine($"  - ACE Available: {_aceProviderAvailable}");
            System.Diagnostics.Debug.WriteLine($"  - Selected Layer: {_config.selectedLayer}");
            System.Diagnostics.Debug.WriteLine($"  - ID Field: {_config.idField}");
            System.Diagnostics.Debug.WriteLine($"  - Cache Size: {_inspectionCache.Count}");
        }

        public async Task<List<ImageInfo>> GetImagePathsForAssetAsync(string assetId, CancellationToken cancellationToken = default)
        {
            var imagePaths = new List<ImageInfo>();
            
            Console.WriteLine($"[POSM IMAGE DEBUG] === Starting image search for asset: {assetId} ===");
            
            if (string.IsNullOrWhiteSpace(assetId))
            {
                Console.WriteLine("[POSM IMAGE DEBUG] Asset ID is null or empty - returning no images");
                return imagePaths;
            }
            
            if (!IsAvailable)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Database not available - reason: {GetUnavailabilityReason()}");
                return imagePaths;
            }

            var dbPath = PosmDbPath;
            if (string.IsNullOrEmpty(dbPath))
            {
                Console.WriteLine("[POSM IMAGE DEBUG] Database path is null or empty");
                return imagePaths;
            }

            Console.WriteLine($"[POSM IMAGE DEBUG] Database path: {dbPath}");
            Console.WriteLine($"[POSM IMAGE DEBUG] Database exists: {File.Exists(dbPath)}");

            // Query for images - similar to video query but including Distance and PictureLocation
            const string imageQuery = @"
                SELECT sf.MediaFolder, d.PictureLocation, d.Distance 
                FROM SpecialFields sf 
                INNER JOIN Data d ON sf.SessionID = d.SessionID 
                WHERE (
                    UCase(Trim(sf.AssetID)) = @normalizedId 
                    OR (Val(sf.AssetID) = Val(@originalId) AND Val(@originalId) > 0)
                ) AND d.PictureLocation IS NOT NULL AND d.PictureLocation <> ''";

            try
            {
                var normalizedId = NormalizeId(assetId);
                var providers = new[] { "Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0" };
                
                Console.WriteLine($"[POSM IMAGE DEBUG] Original Asset ID: '{assetId}'");
                Console.WriteLine($"[POSM IMAGE DEBUG] Normalized Asset ID: '{normalizedId}'");
                Console.WriteLine($"[POSM IMAGE DEBUG] SQL Query: {imageQuery}");

                foreach (var provider in providers)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Trying provider: {provider}");
                    try
                    {
                        var connectionString = $"Provider={provider};Data Source={dbPath};";
                        Console.WriteLine($"[POSM IMAGE DEBUG] Connection string: {connectionString}");
                        
                        using var connection = new OleDbConnection(connectionString);
                        await connection.OpenAsync(cancellationToken);
                        Console.WriteLine($"[POSM IMAGE DEBUG] Successfully connected to database with {provider}");

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(3));

                        using var command = new OleDbCommand(imageQuery, connection);
                        command.Parameters.AddWithValue("@normalizedId", normalizedId);
                        command.Parameters.AddWithValue("@originalId", assetId);
                        command.CommandTimeout = 3;

                        using var reader = await command.ExecuteReaderAsync(cts.Token);
                        Console.WriteLine($"[POSM IMAGE DEBUG] Query executed successfully");

                        var baseDirectory = Path.GetDirectoryName(_config.posmExecutablePath);
                        if (string.IsNullOrEmpty(baseDirectory))
                        {
                            Console.WriteLine("[POSM IMAGE DEBUG] Base directory is null or empty - cannot construct image paths");
                            return imagePaths;
                        }
                        
                        Console.WriteLine($"[POSM IMAGE DEBUG] POSM executable path: {_config.posmExecutablePath}");
                        Console.WriteLine($"[POSM IMAGE DEBUG] Base directory (from exe): {baseDirectory}");
                        Console.WriteLine($"[POSM IMAGE DEBUG] Base directory exists: {Directory.Exists(baseDirectory)}");
                        
                        var baseImageDirectory = Path.Combine(baseDirectory, "Video");
                        Console.WriteLine($"[POSM IMAGE DEBUG] Image base directory: {baseImageDirectory}");
                        Console.WriteLine($"[POSM IMAGE DEBUG] Image base directory exists: {Directory.Exists(baseImageDirectory)}");

                        int rowCount = 0;
                        while (await reader.ReadAsync(cts.Token))
                        {
                            rowCount++;
                            Console.WriteLine($"[POSM IMAGE DEBUG] Processing database row #{rowCount}");
                            
                            var mediaFolder = reader["MediaFolder"]?.ToString() ?? string.Empty;
                            var pictureLocation = reader["PictureLocation"]?.ToString() ?? string.Empty;
                            var distance = Convert.ToDouble(reader["Distance"] ?? 0.0);
                            
                            Console.WriteLine($"[POSM IMAGE DEBUG] Raw MediaFolder: '{mediaFolder}'");
                            Console.WriteLine($"[POSM IMAGE DEBUG] Raw PictureLocation: '{pictureLocation}'");
                            Console.WriteLine($"[POSM IMAGE DEBUG] Raw Distance: {distance}");
                            
                            if (!string.IsNullOrWhiteSpace(mediaFolder) && !string.IsNullOrWhiteSpace(pictureLocation))
                            {
                                var expectedPath = Path.GetFullPath(Path.Combine(baseImageDirectory, mediaFolder, pictureLocation));
                                var expectedExists = File.Exists(expectedPath);

                                imagePaths.Add(new ImageInfo(expectedPath, mediaFolder, pictureLocation, distance));

                                Console.WriteLine($"[POSM IMAGE DEBUG] Expected path: {expectedPath}");
                                Console.WriteLine($"[POSM IMAGE DEBUG] Expected path exists: {expectedExists}");
                            }
                            else
                            {
                                Console.WriteLine($"[POSM IMAGE DEBUG] Skipping row - empty MediaFolder or PictureLocation");
                            }
                        }

                        Console.WriteLine($"[POSM IMAGE DEBUG] Total database rows processed: {rowCount}");
                        Console.WriteLine($"[POSM IMAGE DEBUG] Total image paths found: {imagePaths.Count}");
                        
                        if (imagePaths.Count > 0)
                        {
                            var existingCount = imagePaths.Count(i => File.Exists(i.FilePath));
                            Console.WriteLine($"[POSM IMAGE DEBUG] Images that exist on disk: {existingCount} out of {imagePaths.Count}");
                            return imagePaths;
                        }
                        else
                        {
                            Console.WriteLine($"[POSM IMAGE DEBUG] No image paths found for asset '{assetId}' with provider {provider}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Timeout getting image paths for '{assetId}'");
                        return imagePaths;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Provider {provider} failed: {ex.Message}");
                    }
                }

                return imagePaths;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error getting image paths for asset '{assetId}': {ex.Message}");
                return imagePaths;
            }
        }
        
        public async Task<List<string>> GetSampleAssetIdsAsync(CancellationToken cancellationToken = default)
        {
            var assetIds = new List<string>();
            
            if (!IsAvailable)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Database not available for sample AssetIDs - reason: {GetUnavailabilityReason()}");
                return assetIds;
            }

            var dbPath = PosmDbPath;
            if (string.IsNullOrEmpty(dbPath))
            {
                Console.WriteLine("[POSM IMAGE DEBUG] Database path is null or empty for sample AssetIDs");
                return assetIds;
            }

            const string sampleQuery = @"
                SELECT DISTINCT TOP 10 sf.AssetID 
                FROM SpecialFields sf 
                WHERE sf.AssetID IS NOT NULL AND sf.AssetID <> ''";

            try
            {
                var providers = new[] { "Microsoft.ACE.OLEDB.16.0", "Microsoft.ACE.OLEDB.12.0" };
                
                Console.WriteLine($"[POSM IMAGE DEBUG] Getting sample AssetIDs from database");
                Console.WriteLine($"[POSM IMAGE DEBUG] SQL Query: {sampleQuery}");

                foreach (var provider in providers)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Trying provider: {provider}");
                    try
                    {
                        var connectionString = $"Provider={provider};Data Source={dbPath}";
                        using var connection = new OleDbConnection(connectionString);
                        await connection.OpenAsync(cancellationToken);
                        
                        using var command = new OleDbCommand(sampleQuery, connection);
                        using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var assetId = reader.GetString(0);
                            if (!string.IsNullOrWhiteSpace(assetId))
                            {
                                assetIds.Add(assetId);
                            }
                        }
                        
                        Console.WriteLine($"[POSM IMAGE DEBUG] Successfully retrieved {assetIds.Count} sample AssetIDs using provider {provider}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Provider {provider} failed for sample AssetIDs: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error getting sample AssetIDs: {ex.Message}");
            }
            
            return assetIds;
        }
    }
}
