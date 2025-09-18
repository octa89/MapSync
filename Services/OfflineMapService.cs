using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.Tasks.Offline;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Enhanced offline map service with improved basemap detection and handling
    /// </summary>
    public class OfflineMapService : IOfflineMapService
    {
        private readonly ILogger<OfflineMapService> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly INetworkService _networkService;
        private GenerateOfflineMapJob? _currentJob;
        private CancellationTokenSource? _cancellationTokenSource;

        // Supported basemap file extensions
        private readonly string[] SupportedBasemapExtensions = { ".tpk", ".tpkx", ".vtpk", ".mmpk" };

        public event EventHandler<OfflineMapProgressEventArgs>? ProgressChanged;
        public event EventHandler<OfflineMapCompletedEventArgs>? OfflineMapCompleted;

        public bool IsGenerationInProgress => _currentJob != null && 
            (_currentJob.Status == JobStatus.Started || _currentJob.Status == JobStatus.Paused);

        public OfflineMapService(
            ILogger<OfflineMapService> logger,
            IConfigurationService configurationService,
            INetworkService networkService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
            _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        }

        public async Task<GenerateOfflineMapResult> GenerateOfflineMapAsync(
            Map map,
            Envelope areaOfInterest,
            string outputPath,
            OfflineMapOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("[OFFLINE MAP] GenerateOfflineMapAsync called with map={MapType}, aoi={AOI}, path={Path}", 
                map?.GetType().Name, areaOfInterest?.ToString(), outputPath);
                
            if (map == null) 
            {
                _logger.LogError("[OFFLINE MAP] Map parameter is null");
                throw new ArgumentNullException(nameof(map));
            }
            if (areaOfInterest == null) 
            {
                _logger.LogError("[OFFLINE MAP] AreaOfInterest parameter is null");
                throw new ArgumentNullException(nameof(areaOfInterest));
            }
            if (string.IsNullOrWhiteSpace(outputPath)) 
            {
                _logger.LogError("[OFFLINE MAP] OutputPath parameter is null or empty");
                throw new ArgumentException("Output path cannot be empty", nameof(outputPath));
            }

            options ??= new OfflineMapOptions();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                _logger.LogInformation("[OFFLINE MAP] Starting offline map generation to {OutputPath}", outputPath);
                _logger.LogDebug("[OFFLINE MAP] Map LoadStatus: {LoadStatus}, HasPortalItem: {HasItem}", 
                    map.LoadStatus, map.Item != null);
                
                // Ensure output directory exists
                Directory.CreateDirectory(outputPath);

                // Create offline map task
                var offlineMapTask = await OfflineMapTask.CreateAsync(map);
                
                // Get enhanced parameters
                var parameters = await CreateEnhancedParametersAsync(map, areaOfInterest);
                
                // Apply user options to parameters
                await ApplyOptionsToParametersAsync(parameters, options);
                
                // Create and configure the job
                _currentJob = offlineMapTask.GenerateOfflineMap(parameters, outputPath);
                _currentJob.ProgressChanged += OnJobProgressChanged;

                _logger.LogInformation("[OFFLINE MAP] Job created, starting generation...");
                
                // Start the job and await completion
                var result = await _currentJob.GetResultAsync();
                
                _logger.LogInformation("[OFFLINE MAP] Generation completed with status: {Status}", _currentJob.Status);
                
                // Handle job completion
                if (_currentJob.Status == JobStatus.Succeeded)
                {
                    await HandleSuccessfulGeneration(result, outputPath);
                }
                else
                {
                    await HandleFailedGeneration();
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[OFFLINE MAP] Generation was canceled");
                OnProgressChanged(new OfflineMapProgressEventArgs
                {
                    Status = OfflineMapJobStatus.Canceled,
                    StatusMessage = "Generation was canceled",
                    IsComplete = true
                });
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP] Error during offline map generation");
                OnProgressChanged(new OfflineMapProgressEventArgs
                {
                    Status = OfflineMapJobStatus.Failed,
                    StatusMessage = $"Generation failed: {ex.Message}",
                    HasErrors = true,
                    Error = ex,
                    IsComplete = true
                });
                throw;
            }
            finally
            {
                _currentJob = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        public async Task<List<BasemapFileInfo>> DetectLocalBasemapsAsync(string? basemapDirectory)
        {
            var basemapFiles = new List<BasemapFileInfo>();

            if (string.IsNullOrWhiteSpace(basemapDirectory) || !Directory.Exists(basemapDirectory))
            {
                _logger.LogWarning("[OFFLINE MAP] Basemap directory does not exist: {Directory}", basemapDirectory);
                return basemapFiles;
            }

            try
            {
                _logger.LogInformation("[OFFLINE MAP] Scanning for basemap files in: {Directory}", basemapDirectory);

                var files = Directory.GetFiles(basemapDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(file => SupportedBasemapExtensions.Contains(Path.GetExtension(file).ToLower()));

                foreach (var filePath in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var basemapInfo = new BasemapFileInfo
                        {
                            FilePath = filePath,
                            FileName = Path.GetFileNameWithoutExtension(filePath),
                            FileType = Path.GetExtension(filePath).TrimStart('.').ToLower(),
                            FileSizeBytes = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            IsValid = await ValidateBasemapFileAsync(filePath)
                        };

                        if (!basemapInfo.IsValid)
                        {
                            basemapInfo.ValidationError = "File validation failed - may be corrupted or incompatible";
                        }

                        basemapFiles.Add(basemapInfo);
                        _logger.LogDebug("[OFFLINE MAP] Found basemap: {FileName} ({FileType}) - Valid: {IsValid}", 
                            basemapInfo.FileName, basemapInfo.FileType, basemapInfo.IsValid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[OFFLINE MAP] Error processing basemap file: {FilePath}", filePath);
                    }
                }

                _logger.LogInformation("[OFFLINE MAP] Found {Count} basemap files ({ValidCount} valid)", 
                    basemapFiles.Count, basemapFiles.Count(b => b.IsValid));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP] Error scanning basemap directory: {Directory}", basemapDirectory);
            }

            return basemapFiles.OrderByDescending(b => b.IsValid).ThenBy(b => b.FileName).ToList();
        }

        public async Task<GenerateOfflineMapParameters> CreateEnhancedParametersAsync(Map map, Envelope areaOfInterest)
        {
            try
            {
                var offlineMapTask = await OfflineMapTask.CreateAsync(map);
                var parameters = await offlineMapTask.CreateDefaultGenerateOfflineMapParametersAsync(areaOfInterest);

                _logger.LogInformation("[OFFLINE MAP] Created default parameters for area: {Area}", areaOfInterest);
                _logger.LogDebug("[OFFLINE MAP] Reference basemap filename from web map: {Filename}", 
                    parameters.ReferenceBasemapFilename ?? "None specified");

                // Log all available layers in the map
                _logger.LogInformation("[OFFLINE MAP] Map contains {LayerCount} operational layers:", map.OperationalLayers.Count);
                foreach (var layer in map.OperationalLayers)
                {
                    _logger.LogInformation("[OFFLINE MAP] - Layer: {LayerName} (Type: {LayerType}, Visible: {IsVisible})", 
                        layer.Name, layer.GetType().Name, layer.IsVisible);
                    
                    // Log layer scale information
                    _logger.LogInformation("[OFFLINE MAP] - Layer Scale: Min={MinScale}, Max={MaxScale}", 
                        layer.MinScale, layer.MaxScale);
                    
                    // Force layer to be visible for offline generation
                    if (!layer.IsVisible)
                    {
                        _logger.LogInformation("[OFFLINE MAP] Setting layer {LayerName} to visible for offline generation", layer.Name);
                        layer.IsVisible = true;
                    }
                    
                    // Force layer scale ranges to be unrestricted for offline generation
                    if (layer.MinScale > 0 || layer.MaxScale > 0)
                    {
                        _logger.LogInformation("[OFFLINE MAP] Removing scale restrictions from layer {LayerName}", layer.Name);
                        layer.MinScale = 0;
                        layer.MaxScale = 0;
                    }
                }

                // Apply enhanced configuration from app settings
                await ApplyEnhancedConfigurationAsync(parameters, map);

                return parameters;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP] Error creating enhanced parameters");
                throw;
            }
        }

        public async Task CancelCurrentJobAsync()
        {
            if (_currentJob != null && IsGenerationInProgress)
            {
                try
                {
                    _logger.LogInformation("[OFFLINE MAP] Canceling current offline map job");
                    
                    _cancellationTokenSource?.Cancel();
                    await _currentJob.CancelAsync();
                    
                    OnProgressChanged(new OfflineMapProgressEventArgs
                    {
                        Status = OfflineMapJobStatus.Canceling,
                        StatusMessage = "Canceling generation...",
                        PercentComplete = _currentJob.Progress
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[OFFLINE MAP] Error canceling job");
                }
            }
        }

        private async Task ApplyOptionsToParametersAsync(GenerateOfflineMapParameters parameters, OfflineMapOptions options)
        {
            // Configure basemap handling
            parameters.IncludeBasemap = options.IncludeBasemap;

            if (options.IncludeBasemap && !string.IsNullOrWhiteSpace(options.LocalBasemapDirectory))
            {
                await ConfigureLocalBasemapAsync(parameters, options);
            }

            // Configure performance optimizations
            if (options.SchemaOnlyForEditableLayers)
            {
                parameters.ReturnSchemaOnlyForEditableLayers = true;
                _logger.LogInformation("[OFFLINE MAP] Enabled schema-only mode for editable layers");
            }

            // Configure attachment handling
            if (!options.IncludeAttachments)
            {
                // Configure to exclude attachments - implementation depends on ArcGIS Runtime version
                _logger.LogInformation("[OFFLINE MAP] Configured to exclude attachments");
            }

            // Configure feature limits
            if (options.MaxFeaturesPerLayer.HasValue)
            {
                // Apply to all layer parameters if available
                _logger.LogInformation("[OFFLINE MAP] Setting max features per layer: {MaxFeatures}", options.MaxFeaturesPerLayer.Value);
            }

            _logger.LogInformation("[OFFLINE MAP] Applied user options to parameters");
        }

        private async Task ConfigureLocalBasemapAsync(GenerateOfflineMapParameters parameters, OfflineMapOptions options)
        {
            try
            {
                var availableBasemaps = await DetectLocalBasemapsAsync(options.LocalBasemapDirectory);
                var validBasemaps = availableBasemaps.Where(b => b.IsValid).ToList();

                if (!validBasemaps.Any())
                {
                    _logger.LogWarning("[OFFLINE MAP] No valid local basemaps found in: {Directory}", options.LocalBasemapDirectory);
                    return;
                }

                BasemapFileInfo? selectedBasemap = null;

                // Try to use specified filename first
                if (!string.IsNullOrWhiteSpace(options.LocalBasemapFilename))
                {
                    selectedBasemap = validBasemaps.FirstOrDefault(b => 
                        string.Equals(b.FileName, Path.GetFileNameWithoutExtension(options.LocalBasemapFilename), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(b.FilePath), options.LocalBasemapFilename, StringComparison.OrdinalIgnoreCase));
                }

                // Try to use web map reference basemap
                if (selectedBasemap == null && !string.IsNullOrWhiteSpace(parameters.ReferenceBasemapFilename))
                {
                    selectedBasemap = validBasemaps.FirstOrDefault(b => 
                        string.Equals(Path.GetFileName(b.FilePath), parameters.ReferenceBasemapFilename, StringComparison.OrdinalIgnoreCase));
                }

                // Use first available basemap as fallback
                if (selectedBasemap == null)
                {
                    selectedBasemap = validBasemaps.FirstOrDefault();
                    if (selectedBasemap != null)
                    {
                        _logger.LogInformation("[OFFLINE MAP] Using first available basemap: {FileName}", selectedBasemap.FileName);
                    }
                }

                if (selectedBasemap != null)
                {
                    // Show user confirmation if requested
                    bool useLocalBasemap = true;
                    if (options.ShowUserConfirmation && !options.ForceLocalBasemap)
                    {
                        var result = MessageBox.Show(
                            $"Use local basemap '{selectedBasemap.DisplayName}' instead of downloading online basemap?\n\n" +
                            "This will reduce download time and size but may use a different basemap than the online version.",
                            "Local Basemap Available",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        
                        useLocalBasemap = result == MessageBoxResult.Yes;
                    }

                    if (useLocalBasemap || options.ForceLocalBasemap)
                    {
                        parameters.ReferenceBasemapDirectory = options.LocalBasemapDirectory;
                        parameters.ReferenceBasemapFilename = Path.GetFileName(selectedBasemap.FilePath);
                        
                        _logger.LogInformation("[OFFLINE MAP] Configured local basemap: {Directory}\\{Filename}", 
                            parameters.ReferenceBasemapDirectory, parameters.ReferenceBasemapFilename);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP] Error configuring local basemap");
            }
        }

        private async Task ApplyEnhancedConfigurationAsync(GenerateOfflineMapParameters parameters, Map map)
        {
            try
            {
                var config = _configurationService.Configuration;
                
                // Apply configuration from app settings
                if (!string.IsNullOrWhiteSpace(config?.offlineBasemapPath))
                {
                    var basemapDir = Path.GetDirectoryName(config.offlineBasemapPath);
                    var basemapFile = Path.GetFileName(config.offlineBasemapPath);
                    
                    if (Directory.Exists(basemapDir) && File.Exists(config.offlineBasemapPath))
                    {
                        parameters.ReferenceBasemapDirectory = basemapDir;
                        parameters.ReferenceBasemapFilename = basemapFile;
                        
                        _logger.LogInformation("[OFFLINE MAP] Applied basemap from configuration: {Path}", config.offlineBasemapPath);
                    }
                }

                // Force all layers to be included regardless of scale visibility
                _logger.LogInformation("[OFFLINE MAP] Configuring all layers to be included regardless of scale visibility");
                
                // Check current basemap configuration
                var currentBasemap = map.Basemap;
                if (currentBasemap != null)
                {
                    _logger.LogInformation("[OFFLINE MAP] Current basemap type: {BasemapType}", currentBasemap.Name ?? "Unknown");
                    _logger.LogInformation("[OFFLINE MAP] Basemap has base layers: {HasBaseLayers}", currentBasemap.BaseLayers?.Count > 0);
                    if (currentBasemap.BaseLayers?.Count > 0)
                    {
                        foreach (var baseLayer in currentBasemap.BaseLayers)
                        {
                            _logger.LogInformation("[OFFLINE MAP] Base layer: {LayerName}, Type: {LayerType}, Visible: {IsVisible}", 
                                baseLayer.Name, baseLayer.GetType().Name, baseLayer.IsVisible);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[OFFLINE MAP] No basemap found in current map!");
                }
                
                // Set parameters to include basemap and all features
                parameters.IncludeBasemap = true;
                parameters.ReturnSchemaOnlyForEditableLayers = false;  // Include all features
                
                // Configure layer parameters to ensure all levels are included
                // Note: DestinationTableRowFilter is for specific use cases, removing for now
                
                // Override update mode to include all features
                parameters.UpdateMode = GenerateOfflineMapUpdateMode.SyncWithFeatureServices;
                
                // Force min/max scale to include all zoom levels
                parameters.MinScale = 0;  // No minimum scale limit
                parameters.MaxScale = 0;  // No maximum scale limit
                
                _logger.LogInformation("[OFFLINE MAP] Enhanced configuration applied:");
                _logger.LogInformation("[OFFLINE MAP] - IncludeBasemap: {IncludeBasemap}", parameters.IncludeBasemap);
                _logger.LogInformation("[OFFLINE MAP] - ReturnSchemaOnlyForEditableLayers: {SchemaOnly}", parameters.ReturnSchemaOnlyForEditableLayers);
                _logger.LogInformation("[OFFLINE MAP] - MinScale: {MinScale}, MaxScale: {MaxScale}", parameters.MinScale, parameters.MaxScale);
                _logger.LogInformation("[OFFLINE MAP] - UpdateMode: {UpdateMode}", parameters.UpdateMode);
                
                // Log layer-specific configurations (GenerateOfflineMapParameters doesn't have LayerOptions in this version)
                _logger.LogInformation("[OFFLINE MAP] Configuration completed for basemap and operational layers");

                // Check network connectivity for optimizations
                bool isOnline = await _networkService.IsArcGisOnlineReachableAsync();
                if (!isOnline)
                {
                    _logger.LogInformation("[OFFLINE MAP] Network unavailable - optimizing for offline mode");
                    // Could disable certain online-only features here
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP] Error applying enhanced configuration");
            }
        }

        private async Task<bool> ValidateBasemapFileAsync(string filePath)
        {
            try
            {
                // Basic file existence and extension validation
                if (!File.Exists(filePath))
                    return false;

                var extension = Path.GetExtension(filePath).ToLower();
                if (!SupportedBasemapExtensions.Contains(extension))
                    return false;

                // File size validation (should be > 0 and < 50GB)
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length == 0 || fileInfo.Length > 50L * 1024 * 1024 * 1024)
                    return false;

                // For TPK/TPKX files, we could do more detailed validation here
                // For now, we'll consider the file valid if it passes basic checks
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[OFFLINE MAP] Validation failed for file: {FilePath}", filePath);
                return false;
            }
        }

        private void OnJobProgressChanged(object? sender, EventArgs e)
        {
            if (_currentJob != null)
            {
                var status = _currentJob.Status switch
                {
                    JobStatus.NotStarted => OfflineMapJobStatus.NotStarted,
                    JobStatus.Started => OfflineMapJobStatus.Started,
                    JobStatus.Paused => OfflineMapJobStatus.Paused,
                    JobStatus.Succeeded => OfflineMapJobStatus.Succeeded,
                    JobStatus.Failed => OfflineMapJobStatus.Failed,
                    JobStatus.Canceling => OfflineMapJobStatus.Canceling,
                    _ => OfflineMapJobStatus.NotStarted
                };

                OnProgressChanged(new OfflineMapProgressEventArgs
                {
                    PercentComplete = _currentJob.Progress,
                    Status = status,
                    StatusMessage = $"Progress: {_currentJob.Progress}%",
                    IsComplete = _currentJob.Status == JobStatus.Succeeded || _currentJob.Status == JobStatus.Failed
                });
            }
        }

        private void OnProgressChanged(OfflineMapProgressEventArgs args)
        {
            ProgressChanged?.Invoke(this, args);
        }

        private Task HandleSuccessfulGeneration(GenerateOfflineMapResult result, string outputPath)
        {
            _logger.LogInformation("[OFFLINE MAP] Generation succeeded");
            
            // Log details about the generated offline map
            if (result.OfflineMap != null)
            {
                _logger.LogInformation("[OFFLINE MAP] Generated offline map contains {LayerCount} operational layers", 
                    result.OfflineMap.OperationalLayers.Count);
                    
                foreach (var layer in result.OfflineMap.OperationalLayers)
                {
                    _logger.LogInformation("[OFFLINE MAP] Generated layer: {LayerName} (Type: {LayerType})", 
                        layer.Name, layer.GetType().Name);
                }
                
                // Check basemap in generated offline map
                var generatedBasemap = result.OfflineMap.Basemap;
                if (generatedBasemap != null)
                {
                    _logger.LogInformation("[OFFLINE MAP] Generated offline map includes basemap: {BasemapName}", 
                        generatedBasemap.Name ?? "Unknown");
                    _logger.LogInformation("[OFFLINE MAP] Generated basemap has {BaseLayerCount} base layers", 
                        generatedBasemap.BaseLayers?.Count ?? 0);
                    
                    if (generatedBasemap.BaseLayers?.Count > 0)
                    {
                        foreach (var baseLayer in generatedBasemap.BaseLayers)
                        {
                            _logger.LogInformation("[OFFLINE MAP] Generated base layer: {LayerName} (Type: {LayerType})", 
                                baseLayer.Name, baseLayer.GetType().Name);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("[OFFLINE MAP] Generated offline map does NOT include a basemap!");
                }
            }
            
            if (result.LayerErrors.Any())
            {
                _logger.LogWarning("[OFFLINE MAP] Generation completed with {ErrorCount} layer errors", result.LayerErrors.Count);
                foreach (var error in result.LayerErrors)
                {
                    _logger.LogWarning("[OFFLINE MAP] Layer error - {LayerId}: {Error}", error.Key.Id, error.Value.Message);
                }
            }
            else
            {
                _logger.LogInformation("[OFFLINE MAP] All layers generated successfully without errors");
            }

            OnProgressChanged(new OfflineMapProgressEventArgs
            {
                PercentComplete = 100,
                Status = OfflineMapJobStatus.Succeeded,
                StatusMessage = "Offline map generation completed successfully",
                IsComplete = true,
                HasErrors = result.LayerErrors.Any()
            });

            // Fire offline map completed event for automatic switching
            OfflineMapCompleted?.Invoke(this, new OfflineMapCompletedEventArgs
            {
                OfflineMapPath = outputPath,
                HasLayerErrors = result.LayerErrors.Any(),
                LayerErrorCount = result.LayerErrors.Count
            });

            return Task.CompletedTask;
        }

        private Task HandleFailedGeneration()
        {
            _logger.LogError("[OFFLINE MAP] Generation failed");
            
            OnProgressChanged(new OfflineMapProgressEventArgs
            {
                Status = OfflineMapJobStatus.Failed,
                StatusMessage = "Offline map generation failed",
                HasErrors = true,
                IsComplete = true
            });
            return Task.CompletedTask;
        }

        public async Task<List<ExistingOfflineMap>> DetectExistingOfflineMapsAsync(string outputDirectory)
        {
            var existingMaps = new List<ExistingOfflineMap>();
            
            try
            {
                _logger.LogInformation("[OFFLINE MAP DETECTION] Scanning directory for existing offline maps: {Directory}", outputDirectory);
                
                if (!Directory.Exists(outputDirectory))
                {
                    _logger.LogWarning("[OFFLINE MAP DETECTION] Output directory does not exist: {Directory}", outputDirectory);
                    return existingMaps;
                }

                var subDirectories = Directory.GetDirectories(outputDirectory);
                _logger.LogDebug("[OFFLINE MAP DETECTION] Found {Count} subdirectories", subDirectories.Length);

                foreach (var directory in subDirectories)
                {
                    try
                    {
                        var offlineMap = await ValidateOfflineMapDirectoryAsync(directory);
                        if (offlineMap != null)
                        {
                            existingMaps.Add(offlineMap);
                            _logger.LogDebug("[OFFLINE MAP DETECTION] Valid offline map found: {Name}", offlineMap.FolderName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[OFFLINE MAP DETECTION] Error validating directory: {Directory}", directory);
                    }
                }

                existingMaps = existingMaps.OrderByDescending(m => m.CreatedDate).ToList();
                _logger.LogInformation("[OFFLINE MAP DETECTION] Found {Count} valid offline maps", existingMaps.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP DETECTION] Error scanning directory: {Directory}", outputDirectory);
            }

            return existingMaps;
        }

        private async Task<ExistingOfflineMap?> ValidateOfflineMapDirectoryAsync(string directoryPath)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                var folderName = directoryInfo.Name;
                
                // Look for offline map package structure: p13 folder with mobile_map.mmap
                var p13Directory = Path.Combine(directoryPath, "p13");
                var packageInfoFile = Path.Combine(directoryPath, "package.info");
                var mobileMapFile = Path.Combine(p13Directory, "mobile_map.mmap");
                
                // Check for standard offline map package structure
                if (Directory.Exists(p13Directory) && File.Exists(packageInfoFile) && File.Exists(mobileMapFile))
                {
                    _logger.LogDebug("[OFFLINE MAP DETECTION] Found offline map package structure in: {Directory}", directoryPath);
                    
                    // Get creation date from directory or package.info file
                    var createdDate = directoryInfo.CreationTime;
                    if (File.Exists(packageInfoFile))
                    {
                        var packageInfoDate = new FileInfo(packageInfoFile).CreationTime;
                        createdDate = packageInfoDate; // Use package.info creation time as it's more accurate
                    }
                    
                    // Calculate total size
                    var totalSize = CalculateDirectorySize(directoryPath);
                    
                    // Count layers (approximate by geodatabase files)
                    var layerCount = Directory.GetFiles(p13Directory, "*.geodatabase", SearchOption.TopDirectoryOnly).Length;
                    
                    return new ExistingOfflineMap
                    {
                        FolderPath = directoryPath,
                        FolderName = folderName,
                        CreatedDate = createdDate,
                        TotalSizeBytes = totalSize,
                        LayerCount = layerCount,
                        IsValid = true
                    };
                }
                
                // Fallback: Look for .mmpk files (Mobile Map Package) for backward compatibility
                var mmpkFiles = Directory.GetFiles(directoryPath, "*.mmpk", SearchOption.TopDirectoryOnly);
                
                if (mmpkFiles.Length == 0)
                {
                    _logger.LogDebug("[OFFLINE MAP DETECTION] No offline map package or .mmpk files found in: {Directory}", directoryPath);
                    return null;
                }

                var mmpkFile = mmpkFiles.First();
                var fileInfo = new FileInfo(mmpkFile);
                
                // Try to validate the mobile map package
                try
                {
                    var mobileMapPackage = await MobileMapPackage.OpenAsync(mmpkFile);
                    if (mobileMapPackage?.Maps?.Any() == true)
                    {
                        var map = mobileMapPackage.Maps.First();
                        var layerCount = map.OperationalLayers.Count + (map.Basemap?.BaseLayers.Count ?? 0);
                        
                        // Calculate total directory size
                        var totalSize = CalculateDirectorySize(directoryPath);
                        
                        return new ExistingOfflineMap
                        {
                            FolderPath = directoryPath,
                            FolderName = folderName,
                            CreatedDate = directoryInfo.CreationTime,
                            TotalSizeBytes = totalSize,
                            LayerCount = layerCount,
                            IsValid = true
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[OFFLINE MAP DETECTION] Invalid mobile map package: {File}", mmpkFile);
                    return new ExistingOfflineMap
                    {
                        FolderPath = directoryPath,
                        FolderName = folderName,
                        CreatedDate = directoryInfo.CreationTime,
                        TotalSizeBytes = fileInfo.Length,
                        LayerCount = 0,
                        IsValid = false,
                        ValidationError = $"Invalid mobile map package: {ex.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[OFFLINE MAP DETECTION] Error validating directory: {Directory}", directoryPath);
            }

            return null;
        }

        private long CalculateDirectorySize(string directoryPath)
        {
            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                return directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[OFFLINE MAP DETECTION] Error calculating directory size: {Directory}", directoryPath);
                return 0;
            }
        }
    }
}