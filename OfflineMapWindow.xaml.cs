using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WpfMapApp1.Services;

namespace WpfMapApp1
{
    /// <summary>
    /// Enhanced offline map generation window with improved basemap handling
    /// </summary>
    public partial class OfflineMapWindow : Window
    {
        private readonly IOfflineMapService _offlineMapService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<OfflineMapWindow> _logger;
        private readonly Map _sourceMap;
        private readonly Envelope _areaOfInterest;
        private CancellationTokenSource? _cancellationTokenSource;
        private List<ExistingOfflineMap> _existingOfflineMaps = new();
        
        public string? SelectedOfflineMapPath { get; private set; }

        public OfflineMapWindow(
            IOfflineMapService offlineMapService,
            IConfigurationService configurationService,
            ILogger<OfflineMapWindow> logger,
            Map sourceMap,
            Envelope areaOfInterest)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogDebug("[OFFLINE MAP UI] Constructor called with services: {HasOfflineService}, {HasConfigService}, {HasSourceMap}, {HasAOI}",
                offlineMapService != null, configurationService != null, sourceMap != null, areaOfInterest != null);
                
            _offlineMapService = offlineMapService ?? throw new ArgumentNullException(nameof(offlineMapService));
            _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
            _sourceMap = sourceMap ?? throw new ArgumentNullException(nameof(sourceMap));
            _areaOfInterest = areaOfInterest ?? throw new ArgumentNullException(nameof(areaOfInterest));

            _logger.LogDebug("[OFFLINE MAP UI] All parameters validated, calling InitializeComponent");
            InitializeComponent();
            _logger.LogDebug("[OFFLINE MAP UI] InitializeComponent completed, calling Initialize");
            Initialize();
            _logger.LogInformation("[OFFLINE MAP UI] OfflineMapWindow constructor completed successfully");
        }

        private void Initialize()
        {
            // Determine current map state and set appropriate output path
            var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OfflineMaps");
            var mapInfo = GetCurrentMapInfo();
            OutputPathTextBox.Text = Path.Combine(defaultPath, $"OfflineMap_{DateTime.Now:yyyyMMdd_HHmmss}{mapInfo}");

            // Set default basemap directory from configuration
            var config = _configurationService.Configuration;
            if (!string.IsNullOrWhiteSpace(config?.offlineBasemapPath))
            {
                BasemapDirectoryTextBox.Text = Path.GetDirectoryName(config.offlineBasemapPath);
            }

            // Wire up events
            UseLocalBasemapRadio.Checked += OnBasemapSourceChanged;
            DownloadBasemapRadio.Checked += OnBasemapSourceChanged;
            BrowseBasemapButton.Click += OnBrowseBasemapDirectory;
            BrowseOutputButton.Click += OnBrowseOutputDirectory;
            BasemapDirectoryTextBox.TextChanged += OnBasemapDirectoryChanged;
            GenerateButton.Click += OnGenerateOfflineMap;
            CancelButton.Click += OnCancelGeneration;
            CloseButton.Click += (s, e) => Close();

            // Wire up existing offline maps events
            RefreshExistingMapsButton.Click += OnRefreshExistingMaps;
            LoadExistingMapButton.Click += OnLoadExistingMap;
            ExistingMapsComboBox.SelectionChanged += OnExistingMapSelectionChanged;

            // Subscribe to offline map service events
            _offlineMapService.ProgressChanged += OnOfflineMapProgress;

            // Load existing offline maps
            _ = LoadExistingOfflineMapsAsync();

            _logger.LogInformation("[OFFLINE MAP UI] Window initialized");
        }

        private void OnBasemapSourceChanged(object sender, RoutedEventArgs e)
        {
            LocalBasemapPanel.IsEnabled = UseLocalBasemapRadio.IsChecked == true;
            
            if (UseLocalBasemapRadio.IsChecked == true && !string.IsNullOrWhiteSpace(BasemapDirectoryTextBox.Text))
            {
                _ = LoadBasemapFilesAsync();
            }
        }

        private void OnBrowseBasemapDirectory(object sender, RoutedEventArgs e)
        {
            // Use WPF-compatible folder browser
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select a basemap file (TPK, TPKX, VTPK, MMPK)",
                Filter = "Basemap Files|*.tpk;*.tpkx;*.vtpk;*.mmpk|All Files|*.*",
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (!string.IsNullOrWhiteSpace(BasemapDirectoryTextBox.Text))
            {
                dialog.InitialDirectory = BasemapDirectoryTextBox.Text;
            }

            if (dialog.ShowDialog() == true)
            {
                BasemapDirectoryTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
            }
        }

        private void OnBrowseOutputDirectory(object sender, RoutedEventArgs e)
        {
            // Use WPF SaveFileDialog to select a directory-like approach
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Select output location for offline map",
                FileName = $"OfflineMap_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = "folder",
                Filter = "Folders|*.folder"
            };

            if (!string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(OutputPathTextBox.Text);
            }

            if (dialog.ShowDialog() == true)
            {
                // Remove the .folder extension if added
                var selectedPath = dialog.FileName;
                if (selectedPath.EndsWith(".folder"))
                {
                    selectedPath = selectedPath.Substring(0, selectedPath.Length - 7);
                }
                OutputPathTextBox.Text = selectedPath;
            }
        }

        private async void OnBasemapDirectoryChanged(object sender, RoutedEventArgs e)
        {
            if (UseLocalBasemapRadio.IsChecked == true)
            {
                await LoadBasemapFilesAsync();
            }
        }

        private async Task LoadBasemapFilesAsync()
        {
            try
            {
                var directory = BasemapDirectoryTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(directory))
                {
                    BasemapFileComboBox.ItemsSource = null;
                    BasemapInfoTextBlock.Text = "No directory specified";
                    return;
                }

                BasemapInfoTextBlock.Text = "Scanning for basemap files...";
                
                var basemapFiles = await _offlineMapService.DetectLocalBasemapsAsync(directory);
                
                BasemapFileComboBox.ItemsSource = basemapFiles;
                
                if (basemapFiles.Any())
                {
                    BasemapFileComboBox.SelectedIndex = 0;
                    var validCount = basemapFiles.Count(f => f.IsValid);
                    BasemapInfoTextBlock.Text = $"Found {basemapFiles.Count} files ({validCount} valid)";
                }
                else
                {
                    BasemapInfoTextBlock.Text = "No basemap files found in directory";
                }

                _logger.LogInformation("[OFFLINE MAP UI] Loaded {Count} basemap files from {Directory}", 
                    basemapFiles.Count, directory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP UI] Error loading basemap files");
                BasemapInfoTextBlock.Text = $"Error: {ex.Message}";
            }
        }

        private async void OnGenerateOfflineMap(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
                {
                    MessageBox.Show("Please specify an output directory.", "Validation Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (UseLocalBasemapRadio.IsChecked == true && 
                    (string.IsNullOrWhiteSpace(BasemapDirectoryTextBox.Text) || BasemapFileComboBox.SelectedItem == null))
                {
                    MessageBox.Show("Please select a local basemap file.", "Validation Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Show progress panel
                ProgressPanel.Visibility = Visibility.Visible;
                GenerateButton.IsEnabled = false;

                // Create options
                var options = new OfflineMapOptions
                {
                    IncludeBasemap = IncludeBasemapCheckBox.IsChecked == true,
                    SchemaOnlyForEditableLayers = SchemaOnlyCheckBox.IsChecked == true,
                    ShowUserConfirmation = false // We've already shown our UI
                };

                // Configure local basemap if selected
                if (UseLocalBasemapRadio.IsChecked == true)
                {
                    options.LocalBasemapDirectory = BasemapDirectoryTextBox.Text;
                    if (BasemapFileComboBox.SelectedItem is BasemapFileInfo selectedBasemap)
                    {
                        options.LocalBasemapFilename = Path.GetFileName(selectedBasemap.FilePath);
                        options.ForceLocalBasemap = true;
                    }
                }

                // Configure attachments
                if (ExcludeAttachmentsCheckBox.IsChecked == true)
                {
                    options.IncludeAttachments = false;
                }

                // Configure max features
                if (int.TryParse(MaxFeaturesTextBox.Text, out int maxFeatures) && maxFeatures > 0)
                {
                    options.MaxFeaturesPerLayer = maxFeatures;
                }

                _cancellationTokenSource = new CancellationTokenSource();

                _logger.LogInformation("[OFFLINE MAP UI] Starting offline map generation");

                // Generate offline map
                var result = await _offlineMapService.GenerateOfflineMapAsync(
                    _sourceMap, 
                    _areaOfInterest, 
                    OutputPathTextBox.Text, 
                    options, 
                    _cancellationTokenSource.Token);

                // Handle success
                ProgressTextBlock.Text = "Offline map generated successfully!";
                ProgressBar.Value = 100;

                var successMessage = $"Offline map generated successfully!\n\nLocation: {OutputPathTextBox.Text}";
                if (result.LayerErrors.Any())
                {
                    successMessage += $"\n\nNote: {result.LayerErrors.Count} layer(s) had errors during generation.";
                }

                MessageBox.Show(successMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);

                _logger.LogInformation("[OFFLINE MAP UI] Generation completed successfully");
                Close();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[OFFLINE MAP UI] Generation was canceled");
                ProgressTextBlock.Text = "Generation canceled";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP UI] Error during offline map generation");
                MessageBox.Show($"Error generating offline map:\n\n{ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ProgressTextBlock.Text = "Generation failed";
            }
            finally
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                GenerateButton.IsEnabled = true;
                _cancellationTokenSource?.Dispose();
            }
        }

        private async void OnCancelGeneration(object sender, RoutedEventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                await _offlineMapService.CancelCurrentJobAsync();
                ProgressTextBlock.Text = "Canceling...";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP UI] Error canceling generation");
            }
        }

        private void OnOfflineMapProgress(object? sender, OfflineMapProgressEventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ProgressBar.Value = e.PercentComplete;
                ProgressTextBlock.Text = e.StatusMessage;

                if (e.IsComplete)
                {
                    if (e.Status == OfflineMapJobStatus.Succeeded)
                    {
                        ProgressTextBlock.Text = "Generation completed successfully!";
                    }
                    else if (e.Status == OfflineMapJobStatus.Failed)
                    {
                        ProgressTextBlock.Text = "Generation failed";
                    }
                    else if (e.Status == OfflineMapJobStatus.Canceled)
                    {
                        ProgressTextBlock.Text = "Generation was canceled";
                    }
                }
            });
        }

        private async Task LoadExistingOfflineMapsAsync()
        {
            try
            {
                _logger.LogInformation("[OFFLINE MAP UI] Loading existing offline maps");
                
                var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OfflineMaps");
                _existingOfflineMaps = await _offlineMapService.DetectExistingOfflineMapsAsync(defaultPath);
                
                ExistingMapsComboBox.ItemsSource = _existingOfflineMaps;
                
                if (_existingOfflineMaps.Any())
                {
                    ExistingMapInfoTextBlock.Text = $"Found {_existingOfflineMaps.Count} existing offline maps";
                    _logger.LogInformation("[OFFLINE MAP UI] Found {Count} existing offline maps", _existingOfflineMaps.Count);
                }
                else
                {
                    ExistingMapInfoTextBlock.Text = "No existing offline maps found";
                    _logger.LogInformation("[OFFLINE MAP UI] No existing offline maps found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP UI] Error loading existing offline maps");
                ExistingMapInfoTextBlock.Text = "Error loading existing offline maps";
            }
        }

        private async void OnRefreshExistingMaps(object sender, RoutedEventArgs e)
        {
            _logger.LogInformation("[OFFLINE MAP UI] Refreshing existing offline maps");
            await LoadExistingOfflineMapsAsync();
        }

        private void OnExistingMapSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedMap = ExistingMapsComboBox.SelectedItem as ExistingOfflineMap;
            LoadExistingMapButton.IsEnabled = selectedMap != null && selectedMap.IsValid;
            
            if (selectedMap != null)
            {
                if (selectedMap.IsValid)
                {
                    ExistingMapInfoTextBlock.Text = $"Map: {selectedMap.FolderName}\n" +
                                                   $"Created: {selectedMap.CreatedDate:MMM dd, yyyy HH:mm}\n" +
                                                   $"Size: {selectedMap.TotalSizeBytes / 1024 / 1024:F1} MB\n" +
                                                   $"Layers: {selectedMap.LayerCount}";
                }
                else
                {
                    ExistingMapInfoTextBlock.Text = $"Invalid map: {selectedMap.ValidationError}";
                }
                
                _logger.LogDebug("[OFFLINE MAP UI] Selected offline map: {Name} (Valid: {IsValid})", 
                    selectedMap.FolderName, selectedMap.IsValid);
            }
        }

        private async void OnLoadExistingMap(object sender, RoutedEventArgs e)
        {
            var selectedMap = ExistingMapsComboBox.SelectedItem as ExistingOfflineMap;
            if (selectedMap == null || !selectedMap.IsValid)
            {
                MessageBox.Show("Please select a valid offline map to load.", "No Map Selected", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _logger.LogInformation("[OFFLINE MAP UI] Loading existing offline map: {Path}", selectedMap.FolderPath);
                
                // Check for offline map package structure first (p13/mobile_map.mmap)
                var mobileMapFile = Path.Combine(selectedMap.FolderPath, "p13", "mobile_map.mmap");
                if (File.Exists(mobileMapFile))
                {
                    _logger.LogInformation("[OFFLINE MAP UI] Found offline map package structure: {Path}", mobileMapFile);
                    
                    // Set the selected path for the main window to pick up (use the mobile_map.mmap file)
                    SelectedOfflineMapPath = mobileMapFile;
                }
                else
                {
                    // Fallback: Look for .mmpk files for backward compatibility
                    var mmpkFiles = Directory.GetFiles(selectedMap.FolderPath, "*.mmpk", SearchOption.TopDirectoryOnly);
                    if (!mmpkFiles.Any())
                    {
                        MessageBox.Show("No mobile map package or offline map found in the selected folder.", "Invalid Map", 
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var mmpkFile = mmpkFiles.First();
                    _logger.LogInformation("[OFFLINE MAP UI] Found .mmpk file: {Path}", mmpkFile);
                    
                    // Set the selected path for the main window to pick up
                    SelectedOfflineMapPath = mmpkFile;
                }
                
                _logger.LogInformation("[OFFLINE MAP UI] Successfully selected existing map for loading: {Name}", selectedMap.FolderName);
                
                // Close this window with DialogResult.OK to indicate a map was selected
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[OFFLINE MAP UI] Error loading existing offline map");
                MessageBox.Show($"Error loading offline map: {ex.Message}", "Load Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetCurrentMapInfo()
        {
            try
            {
                // Check if current map is from a Mobile Map Package (offline)
                var mapItem = _sourceMap.Item;
                if (mapItem is Esri.ArcGISRuntime.Portal.PortalItem portalItem)
                {
                    if (portalItem.TypeKeywords?.Contains("Mobile Map Package") == true)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(portalItem.Name);
                        return $"_FromOffline_{fileName}";
                    }
                    
                    // Check if it's a WebMap (online)
                    if (portalItem.TypeKeywords?.Contains("Web Map") == true)
                    {
                        return "_FromOnlineWebMap";
                    }
                }
                
                // Check layer types to determine if mostly offline
                int offlineLayerCount = 0;
                int totalLayerCount = _sourceMap.OperationalLayers.Count;
                
                foreach (var layer in _sourceMap.OperationalLayers)
                {
                    // Check layer type for offline indicators
                    var layerTypeName = layer.GetType().Name;
                    if (layerTypeName.Contains("FeatureLayer", StringComparison.OrdinalIgnoreCase) ||
                        layerTypeName.Contains("ArcGISTiledLayer", StringComparison.OrdinalIgnoreCase))
                    {
                        // These could be online or offline, check the layer name for patterns
                        if (layer.Name?.Contains("_offline", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            offlineLayerCount++;
                        }
                    }
                }
                
                // If more than half suggest offline, consider it mixed offline
                if (offlineLayerCount > totalLayerCount / 2)
                {
                    return "_FromMixedOffline";
                }
                
                // Default to online
                return "_FromOnline";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OFFLINE MAP UI] Error determining map info: {Error}", ex.Message);
                return "_FromUnknown";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Clean up
            _offlineMapService.ProgressChanged -= OnOfflineMapProgress;
            _cancellationTokenSource?.Dispose();
            base.OnClosed(e);
        }
    }
}