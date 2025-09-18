#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Toolkit.UI.Controls;
using Esri.ArcGISRuntime.Tasks.Geocoding;
using Esri.ArcGISRuntime.Tasks.Offline;
using Esri.ArcGISRuntime.Rasters;
using POSM_MR3_2;
using WpfMapApp1.Services;
using Microsoft.Extensions.Logging;

namespace WpfMapApp1
{
    public partial class MainWindow : Window
    {
        // Injected services
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfigurationService _configurationService;
        private readonly IMapService _mapService;
        private readonly INetworkService _networkService;
        private readonly IProgressReporter _progressReporter;
        private readonly ILogger<MainWindow> _logger;
        
        // Map status tracking
        private bool _isOfflineMap = false;

        // Field to store the selected feature for later use.
        private ArcGISFeature? _selectedFeature;
        
        // POSM Videos button for popups
        private PosmVideoPopupButton? _posmVideoButton;
        
        // POSM Inspection Images overlay
        private InspectionImageOverlay? _inspectionImageOverlay;
        private PosmDatabaseService? _databaseService;

        // Optional online locator for geocoding when internet+API key are available
        private LocatorTask? _onlineLocator;

        // Legacy SearchView removed - using unified search instead
        
        // LayerSearchSource for autocomplete suggestions
        private LayerSearchSource? _layerSearchSource;
        
        // Search mode tracking
        private bool _isAddressMode = true; // Default to address search
        
        // Geocoding service for address search
        private LocatorTask? _geocodingService;
        
        // Attribute replica cache (in-memory index)
        private ReplicaCache? _replicaCache;
        private InMemorySearchIndex? _searchIndex;
        private bool _showingResults = false;
        private List<SearchResultItem> _lastResults = new List<SearchResultItem>();
        
        // Performance optimization fields
        private System.Windows.Threading.DispatcherTimer? _debounceTimer;
        private CancellationTokenSource? _currentSearchCts;
        private readonly Dictionary<string, List<string>> _suggestionCache = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, SuggestResult> _geoSuggestionIndex = new Dictionary<string, SuggestResult>();

        // NEW: Structured asset suggestions
        private readonly Dictionary<string, List<AssetSearchResult>> _assetSuggestionCache = new Dictionary<string, List<AssetSearchResult>>();
        private List<AssetSearchResult> _currentAssetSuggestions = new List<AssetSearchResult>();
        private string _lastSearchText = "";
        private readonly object _searchLock = new object();

        private readonly IReplicaCacheService _replicaCacheService;
        private readonly ILayerSearchService _layerSearchService;

        public MainWindow(
            IServiceProvider serviceProvider,
            IConfigurationService configurationService,
            IMapService mapService,
            INetworkService networkService,
            IProgressReporter progressReporter,
            IReplicaCacheService replicaCacheService,
            ILayerSearchService layerSearchService,
            ILogger<MainWindow> logger)
        {
            _serviceProvider = serviceProvider;
            _configurationService = configurationService;
            _mapService = mapService;
            _networkService = networkService;
            _progressReporter = progressReporter;
            _replicaCacheService = replicaCacheService;
            _layerSearchService = layerSearchService;
            _logger = logger;

            InitializeComponent();

            // Initialize search mode (default to address mode)
            if (SearchModeToggle != null)
            {
                SearchModeToggle.IsChecked = _isAddressMode; // true = Address mode
            }
            UpdateSearchMode();

            // grab the file-version (from AssemblyFileVersion or ProductVersion)
            var fi = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
            var version = fi.ProductVersion ?? fi.FileVersion ?? "1.0.0.0";
            this.Title = $"POSM Map Reader � Version {version}";
            
            // Initialize POSM Videos button
            InitializePosmVideoButton();
            
            // Initialize POSM Database Service
            InitializeDatabaseService();

            InitializeMapAsync();
        }

        private void OnProgressChanged(object? sender, ProgressEventArgs e)
        {
            // Update UI with progress information
            Dispatcher.BeginInvoke(() =>
            {
                // You can update a status bar or progress indicator here
                // For now, just log the progress
                _logger.LogInformation("[PROGRESS] {Message} {Percentage}%", e.Message, e.Percentage?.ToString("F0") ?? "");
            });
        }

        #region ----------- POSM launcher helper -----------
        private bool LaunchPosm(string arguments)
        {
            // resolve executable path (config.json or hard-coded fallback)
            string exePath = _configurationService.Configuration?.posmExecutablePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = @"C:\POSM\POSM.exe";

            if (!File.Exists(exePath))
            {
                MessageBox.Show(
                    "POSM executable not found at the configured path or at C:\\POSM\\POSM.exe.",
                    "POSM not found", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            Debug.WriteLine($"Starting POSM ? \"{exePath}\" {arguments}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false
                });
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error launching POSM:\n{ex.Message}",
                                "Launch failed",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        #endregion

        #region ----------- Connectivity helper -----------
        #endregion

        #region ----------- Map initialisation -----------
        private async void InitializeMapAsync()
        {
            try
            {
                // Show loading overlay while map initializes
                LoadingOverlay.Visibility = Visibility.Visible;
                
                // Subscribe to progress updates
                _progressReporter.ProgressChanged += OnProgressChanged;
                
                // Read config
                string? mapId = _configurationService.Configuration?.mapId;
                if (string.IsNullOrWhiteSpace(mapId))
                {
                    MessageBox.Show(
                        "No map configured.\n\n� File ? Open MMPK for a local map\n� Or enter a WebMap ID in Settings",
                        "Map not configured", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Use the MapService to load the map
                var map = await _mapService.LoadMapAsync(mapId, new Progress<string>(status => 
                {
                    _progressReporter.Report(status);
                    _logger.LogInformation("[MAP LOADING] {Status}", status);
                }));

                // Assign the map to the view
                MyMapView.Map = map;
                
                // Initialize MapView using the MapService
                await _mapService.InitializeMapViewAsync(MyMapView, new Progress<string>(status => 
                {
                    _progressReporter.Report(status);
                    _logger.LogInformation("[MAP VIEW INIT] {Status}", status);
                }));
                
                // Initialize inspection image overlay after map is assigned
                InitializeInspectionImageOverlay();

                // Hide loading overlay once map is assigned and start loading
                await HideLoadingOverlayWhenMapReady();

                // Configure selection glow (bright green)
                try
                {
                    MyMapView.SelectionProperties.Color = System.Drawing.Color.Lime;
                }
                catch { /* SelectionProperties may not be available in some contexts */ }

                // Initialize layer search and geocoding after map is loaded (parallel for speed)
                var initTasks = new List<Task>
                {
                    InitializeLayerSearchSourceAsync(),
                    InitializeGeocodingServiceAsync(),
                    BuildSearchIndexAsync() // Build in-memory index
                };
                await Task.WhenAll(initTasks);

                // Apply default layer visibility preferences, if any
                try
                {
                    var vis = _configurationService.Configuration?.layerVisibilities;
                    if (vis != null && vis.Count > 0)
                    {
                        await ApplyLayerVisibilitiesAsync(map.OperationalLayers, vis);
                    }
                }
                catch { }

                // Apply default basemap selection, if configured
                try
                {
                    var name = _configurationService.Configuration?.defaultBasemap;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var bm = CreateBasemapFromName(name!);
                        if (bm != null) map.Basemap = bm;
                    }
                }
                catch { }

                // Optional toast
                // MessageBox.Show("Map loaded successfully!");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to load the map:\n{ex.Message}",
                                "Map load error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task HideLoadingOverlayWhenMapReady()
        {
            try
            {
                // Wait for the map to be loaded
                if (MyMapView.Map != null)
                {
                    // Subscribe to the map's LoadStatusChanged event
                    MyMapView.Map.LoadStatusChanged += (sender, args) =>
                    {
                        if (MyMapView.Map.LoadStatus == LoadStatus.Loaded)
                        {
                            // Hide the loading overlay on the UI thread
                            Dispatcher.Invoke(() =>
                            {
                                LoadingOverlay.Visibility = Visibility.Collapsed; try { if (MyMapView != null) MyMapView.Effect = null; } catch { }
                                // Update map status indicator
                                UpdateMapStatusIndicator();
                            });
                        }
                    };

                    // If already loaded, hide immediately
                    if (MyMapView.Map.LoadStatus == LoadStatus.Loaded)
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed; try { if (MyMapView != null) MyMapView.Effect = null; } catch { }
                        // Update map status indicator
                        UpdateMapStatusIndicator();
                    }
                    else
                    {
                        // Wait for the map to load with a timeout
                        await MyMapView.Map.LoadAsync();
                        LoadingOverlay.Visibility = Visibility.Collapsed; try { if (MyMapView != null) MyMapView.Effect = null; } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error hiding loading overlay: {ex.Message}");
                // Always hide overlay on error to prevent permanent loading screen
                LoadingOverlay.Visibility = Visibility.Collapsed; try { if (MyMapView != null) MyMapView.Effect = null; } catch { }
            }
        }
        
        // Build the in-memory search index for ultra-fast searching
        private async Task BuildSearchIndexAsync()
        {
            try
            {
#if DEBUG
                Console.WriteLine("[SEARCH INDEX] Building in-memory search index...");
#endif
                
                // Initialize the replica cache service with the MapView
                if (_replicaCacheService is ReplicaCacheService replicaService)
                {
                    replicaService.Initialize(MyMapView);
                }
                
                // Initialize the layer search service with the MapView
                _layerSearchService.Initialize(MyMapView);

                // Validate configuration and log resolved layers and field lists
                _layerSearchService.ValidateConfigurationAndLogResolutions();
                
                // Subscribe to progress updates from the replica cache service
                _replicaCacheService.ProgressChanged += (sender, args) =>
                {
#if DEBUG
                    Console.WriteLine($"[REPLICA CACHE] {args.Message}");
#endif
                    _progressReporter.Report(args.Message, args.Percentage);
                };
                
                // Warm the cache with progress reporting
                await _replicaCacheService.WarmCacheAsync();
                
                // Update legacy references for backward compatibility
                _replicaCache = new ReplicaCache(MyMapView);
                _searchIndex = _replicaCache.Index;
                LayerSearchSource.SetSharedIndex(_searchIndex);
                
                var stats = _searchIndex.GetStatistics();
#if DEBUG
                Console.WriteLine($"[SEARCH INDEX] ? Index ready: {stats.totalEntries} entries, {stats.indexKeys} keys");
#endif
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine($"[SEARCH INDEX] ? Error building index: {ex.Message}");
#endif
            }
        }
        #endregion

        #region ----------- Extent helper -----------
        private async Task<Envelope?> CalculateCombinedExtentAsync(IEnumerable<Layer> layers)
        {
            Envelope? combined = null;
            const double WORLD_WIDTH = 25_000_000; // skip extents wider than this

            foreach (var layer in layers)
            {
                // ensure the layer is loaded
                if (layer.LoadStatus != LoadStatus.Loaded)
                {
                    try { await layer.LoadAsync(); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"?? Error loading layer '{layer.Name}': {ex.Message}");
                        continue;
                    }
                }

                Envelope? env = null;

                // 1?? If it's a FeatureLayer, get a good extent
                if (layer is FeatureLayer fl)
                {
                    Debug.WriteLine($"[{fl.Name}] FullExtent (schema): {fl.FullExtent}");
                    try
                    {
                        if (fl.FeatureTable is ServiceFeatureTable sft)
                        {
                            // Online/service-backed layer: ask the server for a real extent
                            var result = await sft.QueryExtentAsync(new QueryParameters { WhereClause = "1=1" });
                            env = result.Extent;
                            Debug.WriteLine($"[{fl.Name}] Queried server extent: {env}");
                        }
                        else
                        {
                            // Offline (e.g., GeodatabaseFeatureTable/GeoPackage): use local extent
                            env = fl.FullExtent;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[{fl.Name}] extent fetch failed: {ex.Message}");
                        env = fl.FullExtent;
                    }
                }
                else
                {
                    // 2?? Other layer types: use FullExtent
                    env = layer.FullExtent;
                    Debug.WriteLine($"[{layer.Name}] FullExtent: {env}");
                }

                // 3?? Skip null or �world�-sized envelopes
                if (env == null)
                {
                    Debug.WriteLine($"[{layer.Name}] ? skipping (null extent)");
                    continue;
                }
                if (env.Width > WORLD_WIDTH || env.Height > WORLD_WIDTH)
                {
                    Debug.WriteLine($"[{layer.Name}] ? skipping (world extent)");
                    continue;
                }

                // 4?? Union
                combined = combined == null
                           ? env
                           : new Envelope(
                                Math.Min(combined.XMin, env.XMin),
                                Math.Min(combined.YMin, env.YMin),
                                Math.Max(combined.XMax, env.XMax),
                                Math.Max(combined.YMax, env.YMax),
                                combined.SpatialReference ?? env.SpatialReference);

                Debug.WriteLine($"Combined extent is now: {combined}");
            }

            return combined;
        }
        #endregion

        #region ----------- Offline basemap helper -----------
        private async Task<Basemap?> CreateOfflineBasemapAsync(string basemapPath)
        {
            try
            {
                if (basemapPath.EndsWith(".tpkx", StringComparison.OrdinalIgnoreCase) && File.Exists(basemapPath))
                {
                    var tileCache = new TileCache(basemapPath);
                    var tiledLayer = new ArcGISTiledLayer(tileCache);
                    return new Basemap(tiledLayer);
                }
                else if (basemapPath.EndsWith(".vtpk", StringComparison.OrdinalIgnoreCase) && File.Exists(basemapPath))
                {
                    var vectorTileCache = new VectorTileCache(basemapPath);
                    var vectorTileLayer = new ArcGISVectorTiledLayer(vectorTileCache);
                    return new Basemap(vectorTileLayer);
                }
                else if (basemapPath.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase) && File.Exists(basemapPath))
                {
                    var tileCache = new TileCache(basemapPath);
                    var tiledLayer = new ArcGISTiledLayer(tileCache);
                    return new Basemap(tiledLayer);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load offline basemap: {ex.Message}");
            }
            return null;
        }
        #endregion

        #region ----------- Identify & call-out -----------
        private async void MyMapView_GeoViewTapped(
            object sender,
            Esri.ArcGISRuntime.UI.Controls.GeoViewInputEventArgs e)
        {
            try
            {
                // Check if the event was already handled by image overlay
                if (e.Handled)
                {
                    Console.WriteLine("[MAP TAP] Event already handled by image overlay - skipping feature identification");
                    return;
                }

                // First check if we clicked on an image pin (if images are shown)
                if (chkShowImages?.IsChecked == true && _inspectionImageOverlay != null)
                {
                    // Give the image overlay priority to handle the tap
                    // The overlay's OnMapTapped will set e.Handled = true if it handles the click
                    await Task.Delay(50); // Small delay to allow overlay to process first
                    if (e.Handled)
                    {
                        Console.WriteLine("[MAP TAP] Image overlay handled the tap - exiting");
                        return;
                    }
                }

                var identifyResults = await MyMapView.IdentifyLayersAsync(e.Position, 20, false);

                if (identifyResults.Count > 0 && identifyResults[0].GeoElements.Count > 0)
                {
                    var feature = identifyResults[0].GeoElements[0] as ArcGISFeature;
                    if (feature != null)
                    {
                        await feature.LoadAsync();

                        _selectedFeature = feature;

                        // Retrieve the Asset ID using your config or default "AssetID"
                        string idField = !string.IsNullOrEmpty(_configurationService.Configuration?.idField)
                            ? _configurationService.Configuration!.idField
                            : "AssetID";

                        string assetId = feature.Attributes.ContainsKey(idField)
                            ? feature.Attributes[idField]?.ToString() ?? string.Empty
                            : string.Empty;
                        assetId = assetId.Trim('\"');

                        System.Diagnostics.Debug.WriteLine($"Selected Asset ID: {assetId}");
                        txtAssetID.Text = assetId;

                        // Highlight the selected feature
                        foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                            lyr.ClearSelection();

                        var featureLayer = identifyResults[0].LayerContent as FeatureLayer;
                        featureLayer?.SelectFeature(feature);

                        // Build details string
                        string details = string.Join("\n", feature.Attributes
                            .Select(kvp => $"{kvp.Key}: {kvp.Value}"));

                        // ---------------------------
                        // CREATE A TRANSPARENT FLOATING POPUP
                        // ---------------------------
                        var outerStack = new StackPanel 
                        { 
                            Orientation = Orientation.Vertical,
                            Background = Brushes.Transparent, // Completely transparent background
                            MaxWidth = 320
                        };

                        // Title with transparent background
                        var titleBorder = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(12, 6, 12, 6),
                            Margin = new Thickness(0, 0, 0, 6),
                            Child = new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(assetId) ? "AssetID" : $"AssetID: {assetId}",
                                FontWeight = FontWeights.Bold,
                                FontSize = 13,
                                Foreground = Brushes.Black,
                                TextAlignment = TextAlignment.Center
                            }
                        };
                        outerStack.Children.Add(titleBorder);

                        // OPEN-IN-POSM BUTTON
                        var openPosmBtn = new Button
                        {
                            Content = "Open in POSM",
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Margin = new Thickness(0, 3, 0, 3),
                            FontWeight = FontWeights.Bold,
                            Height = 36,
                            Background = new SolidColorBrush(Color.FromArgb(150, 70, 130, 180)), // More transparent blue
                            Foreground = Brushes.White,
                            BorderThickness = new Thickness(0),
                            FontSize = 12,
                            Template = new ControlTemplate(typeof(Button))
                            {
                                VisualTree = new FrameworkElementFactory(typeof(Border))
                            }
                        };
                        
                        // Custom button template for floating appearance
                        var buttonTemplate = new ControlTemplate(typeof(Button));
                        var borderFactory = new FrameworkElementFactory(typeof(Border));
                        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(150, 70, 130, 180)));
                        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                        borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
                        
                        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                        borderFactory.AppendChild(contentFactory);
                        
                        buttonTemplate.VisualTree = borderFactory;
                        openPosmBtn.Template = buttonTemplate;
                        openPosmBtn.Click += (s2, e2) =>
                        {
                            if (!string.IsNullOrWhiteSpace(assetId))
                                LaunchPosm($"/SSM /AID {assetId}");
                        };
                        outerStack.Children.Add(openPosmBtn);
                        
                        // POSM VIDEOS BUTTON
                        Console.WriteLine($"[POSM VIDEO DEBUG] === Creating popup - checking video button ===");
                        Console.WriteLine($"[POSM VIDEO DEBUG] _posmVideoButton is null: {_posmVideoButton == null}");
                        
                        if (_posmVideoButton != null)
                        {
                            Console.WriteLine($"[POSM VIDEO DEBUG] Creating video button for popup");
                            var posmVideosBtn = _posmVideoButton.CreateButton();
                            posmVideosBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                            posmVideosBtn.Margin = new Thickness(0, 2, 0, 3);
                            posmVideosBtn.Height = 36;
                            posmVideosBtn.FontSize = 12;
                            // Don't apply template - let button manage its own appearance for color changes
                            
                            // Apply basic styling but preserve background colors for state indication
                            posmVideosBtn.BorderThickness = new Thickness(0);
                            posmVideosBtn.FontWeight = FontWeights.Bold;
                            Console.WriteLine($"[POSM VIDEO DEBUG] Video button configured for color changes");
                            
                            Console.WriteLine($"[POSM VIDEO DEBUG] Adding video button to popup UI");
                            outerStack.Children.Add(posmVideosBtn);
                            
                            Console.WriteLine($"[POSM VIDEO DEBUG] Notifying button of feature change");
                            Console.WriteLine($"[POSM VIDEO DEBUG] Feature: {(feature != null ? "Present" : "Null")}");
                            Console.WriteLine($"[POSM VIDEO DEBUG] FeatureLayer: {featureLayer?.Name ?? "Null"}");
                            
                            // Notify the button about the current feature
                            _posmVideoButton.OnPopupFeatureChanged(feature, featureLayer);
                            Console.WriteLine($"[POSM VIDEO DEBUG] Feature change notification sent");
                        }
                        else
                        {
                            Console.WriteLine($"[POSM VIDEO DEBUG] Video button is null - cannot add to popup");
                        }
                        
                        // Note: Images are controlled by the "Show Images" checkbox and display for all assets

                        // SCROLL VIEWER for attribute list with floating appearance
                        var scrollBorder = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), // More transparent white
                            CornerRadius = new CornerRadius(8),
                            Margin = new Thickness(0, 6, 0, 6),
                            MaxHeight = 180,
                            Child = new ScrollViewer
                            {
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                Background = Brushes.Transparent,
                                Padding = new Thickness(10, 8, 10, 8)
                            }
                        };

                        var detailBlock = TextHelper.CreateHyperlinkedText(details);
                        detailBlock.Foreground = Brushes.Black;
                        detailBlock.FontSize = 11;
                        ((ScrollViewer)scrollBorder.Child).Content = detailBlock;
                        outerStack.Children.Add(scrollBorder);

                        // CLOSE button
                        var closeBtn = new Button
                        {
                            Content = "Close",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 8, 0, 0),
                            Height = 32,
                            Width = 90,
                            FontWeight = FontWeights.Bold,
                            Background = new SolidColorBrush(Color.FromArgb(150, 220, 53, 69)), // More transparent red
                            Foreground = Brushes.White,
                            BorderThickness = new Thickness(0),
                            FontSize = 11,
                            Template = buttonTemplate // Use same floating template
                        };
                        closeBtn.Click += (s2, e2) =>
                        {
                            MyMapView.DismissCallout();
                            
                            // Only clear line selection, NOT inspection images!
                            // Image pins should remain visible until "Show Images" checkbox is unchecked
                            foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                                lyr.ClearSelection();
                            _selectedFeature = null;
                        };
                        outerStack.Children.Add(closeBtn);

                        // Show the callout at the tap location
                        MyMapView.ShowCalloutAt(e.Location, outerStack);

                        // Hide any separate overlay close button
                        CloseCalloutButton.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    txtAssetID.Text = string.Empty;
                    MyMapView.DismissCallout();
                    CloseCalloutButton.Visibility = Visibility.Collapsed;
                    
                    // Only clear line selections, NOT inspection images!
                    // Image pins should remain visible until "Show Images" checkbox is unchecked
                    foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                        lyr.ClearSelection();
                    _selectedFeature = null;
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Error identifying features: {ex.Message}",
                                "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region ----------- UI command handlers -----------
        private async void OnOpenMmpkClick(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Mobile Map Package (*.mmpk)|*.mmpk",
                Title = "Open MMPK File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    var filePath = openFileDialog.FileName;
                    var mmpk = await MobileMapPackage.OpenAsync(filePath);

                    if (mmpk.Maps.Any())
                    {
                        MyMapView.Map = mmpk.Maps.First();
                        _isOfflineMap = true; // Mark as offline since MMPK is offline
                        UpdateMapStatusIndicator(); // Update status indicator
                        MessageBox.Show("MMPK loaded successfully.");
                    }
                    else
                    {
                        MessageBox.Show("No maps found in the selected MMPK.");
                    }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Error loading MMPK: {ex.Message}");
                }
            }
        }

        private static Basemap? CreateBasemapFromName(string name)
        {
            try
            {
                // Map friendly names to BasemapStyle values
                return name switch
                {
                    "World Imagery" => new Basemap(BasemapStyle.ArcGISImageryStandard),
                    "World Imagery Hybrid" => new Basemap(BasemapStyle.ArcGISImagery),
                    "World Street Map" => new Basemap(BasemapStyle.ArcGISStreets),
                    "World Topographic Map" => new Basemap(BasemapStyle.ArcGISTopographic),
                    "World Navigation Map" => new Basemap(BasemapStyle.ArcGISNavigation),
                    "World Dark Gray Canvas" => new Basemap(BasemapStyle.ArcGISDarkGray),
                    "World Light Gray Canvas" => new Basemap(BasemapStyle.ArcGISLightGray),
                    "World Terrain" => new Basemap(BasemapStyle.ArcGISTerrain),
                    "OpenStreetMap" => new Basemap(BasemapStyle.OSMStandard),
                    "World Oceans" => new Basemap(BasemapStyle.ArcGISOceans),
                    _ => new Basemap(BasemapStyle.ArcGISImageryStandard)
                };
            }
            catch { return null; }
        }
        
        private static async Task ApplyLayerVisibilitiesAsync(IEnumerable<Layer> layers, System.Collections.Generic.Dictionary<string, bool> vis)
        {
            foreach (var layer in layers)
            {
                if (layer is ILoadable load && load.LoadStatus == LoadStatus.NotLoaded)
                {
                    try { await load.LoadAsync(); } catch { }
                }
                var key = $"{layer.GetType().Name}_{(string.IsNullOrWhiteSpace(layer.Name) ? "Unnamed" : layer.Name)}";
                if (vis.TryGetValue(key, out bool isVisible))
                {
                    layer.IsVisible = isVisible;
                }
                if (layer is GroupLayer g)
                {
                    await ApplyLayerVisibilitiesAsync(g.Layers, vis);
                }
                if (layer is ILayerContent lc && lc.SublayerContents?.Count > 0)
                {
                    foreach (var sub in lc.SublayerContents)
                    {
                        if (sub is Layer subLayer)
                        {
                            await ApplyLayerVisibilitiesAsync(new[] { subLayer }, vis);
                        }
                    }
                }
            }
        }

        // Take current web map offline using area of interest from current view
        private async void TakeMapOffline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MyMapView?.Map == null)
                {
                    MessageBox.Show("No map is loaded.", "Take Map Offline", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Ensure this is a web map (PortalItem-backed). OfflineMapTask requires an online map source.
                try
                {
                    await MyMapView.Map.LoadAsync();
                }
                catch { }
                if (MyMapView.Map.Item == null)
                {
                    MessageBox.Show("Current map is not an online WebMap. Load a WebMap (not an MMPK) before taking offline.",
                        "Take Map Offline", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Make sure API key is applied if available (improves access to basemaps/secure content)
                await _networkService.EnsureOnlineApiKeyIfAvailableAsync();

                // Determine area of interest from current view
                Envelope? aoi = null;
                try
                {
                    var vp = MyMapView.GetCurrentViewpoint(ViewpointType.BoundingGeometry);
                    aoi = vp?.TargetGeometry as Envelope;
                    if (aoi == null && MyMapView.VisibleArea is Esri.ArcGISRuntime.Geometry.Geometry vis)
                    {
                        aoi = vis.Extent;
                    }
                }
                catch { }

                if (aoi == null)
                {
                    MessageBox.Show("Could not determine an area of interest from the current view.", "Take Map Offline", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _logger.LogInformation("[TAKE OFFLINE] Starting enhanced offline map generation for current displayed map");
                _logger.LogDebug("[TAKE OFFLINE] Current map: {MapType}, Portal Item: {HasPortalItem}", 
                    MyMapView.Map?.GetType().Name, MyMapView.Map?.Item != null);
                _logger.LogDebug("[TAKE OFFLINE] Area of interest: {AOI}", aoi?.ToString());

                try
                {
                    // Get required services from DI container
                    var offlineMapService = _serviceProvider.GetRequiredService<IOfflineMapService>();
                    var logger = _serviceProvider.GetRequiredService<ILogger<OfflineMapWindow>>();
                    
                    _logger.LogDebug("[TAKE OFFLINE] Retrieved services: OfflineMapService={HasService}, Logger={HasLogger}", 
                        offlineMapService != null, logger != null);

                    // Subscribe to offline map completion event for automatic switching
                    offlineMapService.OfflineMapCompleted += OnOfflineMapCompleted;

                    // Create the window directly with constructor parameters (not through DI)
                    var offlineMapWindow = new OfflineMapWindow(
                        offlineMapService,
                        _configurationService,
                        logger,
                        MyMapView.Map,  // Use current displayed map
                        aoi
                    );

                    _logger.LogInformation("[TAKE OFFLINE] Created OfflineMapWindow successfully");
                    _logger.LogInformation("[TAKE OFFLINE] Showing enhanced offline map dialog");
                    
                    var dialogResult = offlineMapWindow.ShowDialog();
                    
                    _logger.LogInformation("[TAKE OFFLINE] OfflineMapWindow dialog closed with result: {Result}", dialogResult);
                    
                    // Check if user selected an existing offline map to load
                    if (dialogResult == true && !string.IsNullOrWhiteSpace(offlineMapWindow.SelectedOfflineMapPath))
                    {
                        _logger.LogInformation("[TAKE OFFLINE] Loading selected existing offline map: {Path}", 
                            offlineMapWindow.SelectedOfflineMapPath);
                        await LoadSelectedOfflineMapAsync(offlineMapWindow.SelectedOfflineMapPath);
                    }
                }
                catch (Exception serviceEx)
                {
                    _logger.LogError(serviceEx, "[TAKE OFFLINE] Error creating OfflineMapWindow or retrieving services");
                    throw;
                }
                
                // Update status indicator since offline map generation might have completed
                UpdateMapStatusIndicator();
            }
            catch (TaskCanceledException)
            {
                HideLoadingOverlaySimple();
                MessageBox.Show("Taking map offline was canceled.", "Offline Map", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                HideLoadingOverlaySimple();
                MessageBox.Show($"Error taking map offline: {ex.Message}", "Offline Map", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ReturnToOnlineMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _configurationService.Configuration;
                if (cfg == null)
                {
                    MessageBox.Show("Configuration not loaded.", "Online Map", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Determine a web map ID: prefer explicit webMapId; otherwise if mapId isn't a file, treat as web map id
                string onlineId = (cfg.webMapId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(onlineId))
                {
                    var candidate = (cfg.mapId ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) && !File.Exists(candidate))
                    {
                        onlineId = candidate;
                    }
                }

                if (string.IsNullOrWhiteSpace(onlineId))
                {
                    MessageBox.Show("No online web map ID configured. Add 'webMapId' to config.json or set 'mapId' to a web map ID.",
                        "Online Map", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ShowLoadingOverlay("Loading online map...");
                UpdateLoadingText("Connecting to ArcGIS Online...");

                if (!await _networkService.EnsureOnlineApiKeyIfAvailableAsync())
                {
                    HideLoadingOverlaySimple();
                    MessageBox.Show("No API key configured for online access.", "Online Map", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Load the web map and assign
                var portal = await ArcGISPortal.CreateAsync();
                var portalItem = await PortalItem.CreateAsync(portal, onlineId);
                var map = new Map(portalItem);
                await map.LoadAsync();
                MyMapView.Map = map;

                // Optionally update offlineMode flag in memory
                cfg.offlineMode = false;
                _isOfflineMap = false;

                HideLoadingOverlaySimple();
                // Update status indicator since we've switched to online
                UpdateMapStatusIndicator();
                MessageBox.Show("Switched to online web map.", "Online Map", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                HideLoadingOverlaySimple();
                MessageBox.Show($"Failed to switch to online map: {ex.Message}", "Online Map", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Minimal helpers to reuse the existing overlay
        private void ShowLoadingOverlay(string message = "Loading...")
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingText(message);
            }
        }

        private void HideLoadingOverlaySimple()
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed; try { if (MyMapView != null) MyMapView.Effect = null; } catch { }
            }
        }

        /// <summary>
        /// Updates the modern map status indicator based on current map state
        /// </summary>
        private void UpdateMapStatusIndicator()
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MyMapView?.Map == null)
                    {
                        // No map loaded
                        MapStatusText.Text = "No Map";
                        StatusIndicatorDot.Fill = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Gray
                        MapStatusIndicator.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        MapStatusIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                        return;
                    }

                    // Check if this is an offline map (MMPK or offline generated map)
                    bool isOffline = _isOfflineMap || 
                                   MyMapView.Map.Item == null || // No portal item usually means offline
                                   (MyMapView.Map.LoadStatus == LoadStatus.Loaded && MyMapView.Map.Item == null);

                    if (isOffline)
                    {
                        // Offline map
                        MapStatusText.Text = "Offline";
                        StatusIndicatorDot.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
                        MapStatusIndicator.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                        MapStatusIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 124, 0));
                    }
                    else
                    {
                        // Online map
                        MapStatusText.Text = "Online";
                        StatusIndicatorDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        MapStatusIndicator.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // Blue
                        MapStatusIndicator.BorderBrush = new SolidColorBrush(Color.FromRgb(25, 118, 210));
                    }

                    _logger.LogDebug("[MAP STATUS] Updated indicator: {Status}", isOffline ? "Offline" : "Online");
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MAP STATUS] Error updating map status indicator");
            }
        }

        private async void ForceLabelsToDisplay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLoadingOverlay("Forcing labels to display...");
                await FontFallbackHelper.MakeMapLabelingSafe(MyMapView?.Map);
                HideLoadingOverlaySimple();
                MessageBox.Show("Applied font fallbacks to label definitions.", "Font Fallback",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                HideLoadingOverlaySimple();
                MessageBox.Show($"Font fallback encountered an error: {ex.Message}", "Font Fallback",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateLoadingText(string status)
        {
            try
            {
                // LoadingText is named in XAML to allow updates
                var tb = this.LoadingText; // generated field from x:Name
                if (tb != null)
                {
                    tb.Text = status;
                }
            }
            catch { }
        }

        private async void OnOpenBasemapGallery_Click(object sender, RoutedEventArgs e)
        {
            // ensure API key is set if we�re online, so premium basemaps appear
            await _networkService.EnsureOnlineApiKeyIfAvailableAsync();

            var basemapWindow = new BasemapGalleryWindow
            {
                Owner = this,
                Map = MyMapView.Map
            };
            basemapWindow.Show();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
            MyMapView.SetViewpointScaleAsync(MyMapView.MapScale / 2);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
            MyMapView.SetViewpointScaleAsync(MyMapView.MapScale * 2);

        // made async to refresh API key/search after saving config
        private async void OpenConfig_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigWindow(_configurationService.Configuration, MyMapView.Map);
            if (configWindow.ShowDialog() == true)
            {
                _configurationService.UpdateConfiguration(configWindow.Configuration);
                MessageBox.Show("Configuration updated successfully!",
                                "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);

                // Re-apply API key & refresh search with the new key
                await _networkService.EnsureOnlineApiKeyIfAvailableAsync();
                await InitializeLayerSearchSourceAsync();
                await InitializeGeocodingServiceAsync();
                // Also refresh the optional locator used by TryGeocodeAndZoomAsync
                await InitOnlineLocatorAsync();
            }
        }

        private void OpenAppendWindowButton_Click(object sender, RoutedEventArgs e)
        {
            if (MyMapView.Map == null)
            {
                MessageBox.Show("The map is not loaded yet.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var appendWindow = new AppendToPosmDbWindow(MyMapView.Map);
            appendWindow.ShowDialog();
        }
        #endregion

        #region ----------- Launch POSM toolbar/menu -----------
        private void LaunchPOSM_Click(object sender, RoutedEventArgs e)
        {
            string assetId = txtAssetID.Text.Trim();

            if (string.IsNullOrEmpty(assetId))
            {
                MessageBox.Show("No Asset ID retrieved. Please select a feature first.",
                                "No Asset ID",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string idField = (_configurationService.Configuration != null && !string.IsNullOrEmpty(_configurationService.Configuration.idField))
                                ? _configurationService.Configuration.idField
                                : "AssetID";

            if (_selectedFeature != null && _selectedFeature.Attributes.ContainsKey(idField))
                assetId = _selectedFeature.Attributes[idField]?.ToString()?.Trim('\"') ?? assetId;

            // optional inspectionType flag
            string inspectionType = _configurationService.Configuration?.inspectionType ?? "";

            string arguments = $"/S /T {inspectionType} /AID {assetId}";
            Debug.WriteLine($"Executing POSM with arguments: {arguments}");

            if (LaunchPosm(arguments))
                MessageBox.Show("POSM launched successfully!",
                                "Success",
                                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void OpenLayersWindow_Click(object sender, RoutedEventArgs e)
        {
            if (MyMapView.Map != null)
            {
                var layersWindow = new LayersWindow(MyMapView.Map) { Owner = this };
                layersWindow.Show();
            }
            else
            {
                MessageBox.Show("Map is not loaded yet.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseCalloutButton_Click(object sender, RoutedEventArgs e)
        {
            MyMapView.DismissCallout();
            CloseCalloutButton.Visibility = Visibility.Collapsed;
            
            // Only clear line selection, NOT inspection images!
            // Image pins should remain visible until "Show Images" checkbox is unchecked
            foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                lyr.ClearSelection();
            _selectedFeature = null;
        }

        private async void ShowInspections(object sender, RoutedEventArgs e)
        {
            if (MyMapView.Map == null)
            {
                MessageBox.Show("Map not loaded yet.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1) Determine which layer to highlight
            var layerName = _configurationService.Configuration?.selectedLayer;
            if (string.IsNullOrWhiteSpace(layerName))
            {
                var first = MyMapView.Map.OperationalLayers
                               .OfType<FeatureLayer>()
                               .FirstOrDefault();
                if (first == null)
                {
                    MessageBox.Show(
                        "No feature layers available to inspect.",
                        "Nothing to highlight",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                layerName = first.Name;
                Console.WriteLine($"[ShowInspections] No config.selectedLayer; defaulting to '{layerName}'");
                var config = _configurationService.Configuration;
                if (config != null)
                {
                    config.selectedLayer = layerName;
                    _configurationService.UpdateConfiguration(config);
                }
            }

            // 2) Run the unified highlighter that selects matched features
            await InspectionHighlighter.ApplyInspectionGlowAsync(MyMapView);

            // 3) Tell the user how many were highlighted (sum across selected features)
            int count = 0;
            foreach (var fl in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
            {
                try { count += (await fl.GetSelectedFeaturesAsync())?.Count() ?? 0; }
                catch { }
            }
            MessageBox.Show(
                $"{count} inspection(s) highlighted on layer '{layerName}'.",
                "Inspections Highlighted",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Open POSM video player for the first matched asset with available videos
            await ShowVideosForMatchedInspectionsAsync();
        }

        private async void ShowInspections_Checked(object sender, RoutedEventArgs e)
        {
            await ShowInspectionsAsync();
        }

        private void ShowInspections_Unchecked(object sender, RoutedEventArgs e)
        {
            HideInspections();
        }

        private async Task ShowInspectionsAsync()
        {
            Console.WriteLine("[POSM INSPECTION DEBUG] === Starting ShowInspectionsAsync ===");
            
            if (MyMapView.Map == null)
            {
                Console.WriteLine("[POSM INSPECTION DEBUG] Map is null - cannot show inspections");
                MessageBox.Show("Map not loaded yet.", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                if (chkShowInspections != null)
                    chkShowInspections.IsChecked = false;
                return;
            }

            // Debug configuration info
            Console.WriteLine($"[POSM INSPECTION DEBUG] Configuration is null: {_configurationService.Configuration == null}");
            if (_configurationService.Configuration != null)
            {
                Console.WriteLine($"[POSM INSPECTION DEBUG] selectedLayer: '{_configurationService.Configuration.selectedLayer}'");
                Console.WriteLine($"[POSM INSPECTION DEBUG] idField: '{_configurationService.Configuration.idField}'");
                Console.WriteLine($"[POSM INSPECTION DEBUG] posmExecutablePath: '{_configurationService.Configuration.posmExecutablePath}'");
            }

            // 1) Determine which layer to highlight
            var layerName = _configurationService.Configuration?.selectedLayer;
            if (string.IsNullOrWhiteSpace(layerName))
            {
                var availableLayers = MyMapView.Map.OperationalLayers.OfType<FeatureLayer>().ToList();
                Console.WriteLine($"[POSM INSPECTION DEBUG] No selectedLayer configured. Available layers: {string.Join(", ", availableLayers.Select(l => l.Name))}");
                
                var first = availableLayers.FirstOrDefault();
                if (first == null)
                {
                    Console.WriteLine("[POSM INSPECTION DEBUG] No feature layers available");
                    MessageBox.Show(
                        "No feature layers available to inspect.",
                        "Nothing to highlight",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    if (chkShowInspections != null)
                        chkShowInspections.IsChecked = false;
                    return;
                }
                layerName = first.Name;
                Console.WriteLine($"[POSM INSPECTION DEBUG] Defaulting to first layer: '{layerName}'");
                if (_configurationService.Configuration != null)
                {
                    var config = _configurationService.Configuration;
                    config.selectedLayer = layerName;
                    _configurationService.UpdateConfiguration(config);
                }
            }

            Console.WriteLine($"[POSM INSPECTION DEBUG] Using layer: '{layerName}'");

            // 2) Run the unified highlighter that selects matched features
            Console.WriteLine("[POSM INSPECTION DEBUG] Calling InspectionHighlighter.ApplyInspectionGlowAsync");
            await InspectionHighlighter.ApplyInspectionGlowAsync(MyMapView);
            Console.WriteLine("[POSM INSPECTION DEBUG] InspectionHighlighter completed");

            // 3) Show info in console (count selected features)
            int count = 0;
            foreach (var fl in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
            {
                try 
                { 
                    var selected = await fl.GetSelectedFeaturesAsync();
                    var layerCount = selected?.Count() ?? 0;
                    Console.WriteLine($"[POSM INSPECTION DEBUG] Layer '{fl.Name}': {layerCount} selected features");
                    count += layerCount;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM INSPECTION DEBUG] Error counting selections for layer '{fl.Name}': {ex.Message}");
                }
            }
            Console.WriteLine($"[POSM INSPECTION DEBUG] Total: {count} inspection(s) highlighted on layer \"{layerName}\".");

            // Show message to user
            MessageBox.Show($"{count} inspection(s) highlighted on layer '{layerName}'.", 
                           "Inspections Highlighted", 
                           MessageBoxButton.OK, 
                           MessageBoxImage.Information);

            // Open POSM video player for the first matched asset with available videos
            Console.WriteLine("[POSM INSPECTION DEBUG] Calling ShowVideosForMatchedInspectionsAsync");
            await ShowVideosForMatchedInspectionsAsync();
            Console.WriteLine("[POSM INSPECTION DEBUG] === ShowInspectionsAsync completed ===");
        }

        private void HideInspections()
        {
            // Clear selection on all feature layers
            foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                lyr.ClearSelection();
            Console.WriteLine("Inspections hidden (selection cleared).");
        }

        #region ----------- Show Images Checkbox Handlers -----------
        private async void ShowImages_Checked(object sender, RoutedEventArgs e)
        {
            try 
            {
                Console.WriteLine("=====================================");
                Console.WriteLine("[POSM IMAGE DEBUG] SHOW IMAGES CHECKBOX CHECKED EVENT FIRED!");
                Console.WriteLine($"[POSM IMAGE DEBUG] Sender: {sender?.GetType().Name}");
                Console.WriteLine($"[POSM IMAGE DEBUG] Event args: {e?.GetType().Name}");
                Console.WriteLine("[POSM IMAGE DEBUG] Image display enabled - starting to load images for all assets");
                Console.WriteLine("=====================================");
                
                await DisplayImagesForAllAssetsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] CRITICAL ERROR in ShowImages_Checked: {ex.Message}");
                Console.WriteLine($"[POSM IMAGE DEBUG] Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Error in Show Images checkbox: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowImages_Unchecked(object sender, RoutedEventArgs e)
        {
            try 
            {
                Console.WriteLine("=====================================");
                Console.WriteLine("[POSM IMAGE DEBUG] SHOW IMAGES CHECKBOX UNCHECKED EVENT FIRED!");
                Console.WriteLine($"[POSM IMAGE DEBUG] Sender: {sender?.GetType().Name}");
                Console.WriteLine("=====================================");
                
                // Clear all currently displayed images
                _inspectionImageOverlay?.ClearImages();
                Console.WriteLine("[POSM IMAGE DEBUG] Image display disabled, all images cleared");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] ERROR in ShowImages_Unchecked: {ex.Message}");
                MessageBox.Show($"Error clearing images: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private async Task DisplayImagesForAllAssetsAsync()
        {
            Console.WriteLine("[POSM IMAGE DEBUG] ========================================");
            Console.WriteLine("[POSM IMAGE DEBUG] === DisplayImagesForAllAssetsAsync STARTED ===");
            Console.WriteLine("[POSM IMAGE DEBUG] Using InspectionHighlighter pattern for data access");
            Console.WriteLine("[POSM IMAGE DEBUG] ========================================");
            
            // Check all required components in detail
            Console.WriteLine($"[POSM IMAGE DEBUG] _inspectionImageOverlay is null: {_inspectionImageOverlay == null}");
            Console.WriteLine($"[POSM IMAGE DEBUG] _databaseService is null: {_databaseService == null}");
            Console.WriteLine($"[POSM IMAGE DEBUG] MyMapView is null: {MyMapView == null}");
            Console.WriteLine($"[POSM IMAGE DEBUG] MyMapView.Map is null: {MyMapView?.Map == null}");
            
            if (_inspectionImageOverlay == null || _databaseService == null || MyMapView?.Map == null)
            {
                Console.WriteLine("[POSM IMAGE DEBUG] CRITICAL: Cannot display images - components not initialized");
                MessageBox.Show("Error: Image display components not properly initialized.\nCheck console for details.", 
                               "Initialization Error", 
                               MessageBoxButton.OK, 
                               MessageBoxImage.Error);
                return;
            }
            
            try
            {
                Console.WriteLine("[POSM IMAGE DEBUG] All components initialized - starting image loading process");
                
                // Clear existing images first
                Console.WriteLine("[POSM IMAGE DEBUG] Clearing existing image overlay graphics");
                _inspectionImageOverlay.ClearImages();
                
                // Get configuration values (same as InspectionHighlighter)
                string? layerName = _configurationService.Configuration?.selectedLayer;
                if (string.IsNullOrWhiteSpace(layerName))
                {
                    Console.WriteLine("[POSM IMAGE DEBUG] No selectedLayer configured, cannot proceed");
                    MessageBox.Show("No layer configured in settings. Please configure a selected layer.", 
                                   "Configuration Error", 
                                   MessageBoxButton.OK, 
                                   MessageBoxImage.Warning);
                    return;
                }

                // Find the layer using the same logic as InspectionHighlighter
                FeatureLayer? layer = FindLayerRecursive(MyMapView.Map.OperationalLayers, layerName);
                if (layer == null)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Layer '{layerName}' not found in map");
                    MessageBox.Show($"Layer '{layerName}' not found in map.", "Layer not found", 
                                   MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // GIS side field (same as InspectionHighlighter)
                string gisField = string.IsNullOrWhiteSpace(_configurationService.Configuration?.idField)
                                    ? "AssetID"
                                    : _configurationService.Configuration!.idField;

                Console.WriteLine($"[POSM IMAGE DEBUG] Layer   : {layerName}");
                Console.WriteLine($"[POSM IMAGE DEBUG] GIS fld : {gisField}");

                // Step 1: Get AssetIDs from POSM database (same as InspectionHighlighter)
                Console.WriteLine("[POSM IMAGE DEBUG] Reading AssetIDs from POSM database...");
                List<string> assetIds;
                try 
                { 
                    // Use same database query pattern as InspectionHighlighter
                    assetIds = await ReadAssetIdsFromDatabaseAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Error reading database: {ex.Message}");
                    MessageBox.Show($"Error reading POSM database:\n{ex.Message}", "Database Error", 
                                   MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Console.WriteLine($"[POSM IMAGE DEBUG] Database returned {assetIds.Count} AssetIDs");
                
                if (assetIds.Count == 0)
                {
                    Console.WriteLine("[POSM IMAGE DEBUG] No AssetIDs found in database");
                    MessageBox.Show("No AssetID values found in POSM database.", "Nothing to display", 
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Step 2: Query features using the same chunking pattern as InspectionHighlighter
                Console.WriteLine("[POSM IMAGE DEBUG] Querying features using database AssetIDs...");
                int totalImagesFound = 0;
                int chunkSize = 500;

                for (int i = 0; i < assetIds.Count; i += chunkSize)
                {
                    var chunk = assetIds.Skip(i).Take(chunkSize).ToList();
                    Console.WriteLine($"[POSM IMAGE DEBUG] Processing chunk {i/chunkSize + 1}: {chunk.Count} AssetIDs");

                    var numericVals = new List<string>();
                    var stringVals = new List<string>();

                    // Same value classification as InspectionHighlighter
                    foreach (var v in chunk)
                    {
                        var t = v?.Trim();
                        if (string.IsNullOrEmpty(t)) continue;

                        if (long.TryParse(t, out var li))
                        {
                            numericVals.Add(li.ToString());
                        }
                        else if (double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            numericVals.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            stringVals.Add($"'{t.Replace("'", "''")}'");
                        }
                    }

                    // Query numeric values first (same as InspectionHighlighter)
                    if (numericVals.Count > 0)
                    {
                        string inNums = string.Join(",", numericVals);
                        try
                        {
                            var qpNum = new QueryParameters { WhereClause = $"{gisField} IN ({inNums})", ReturnGeometry = true };
                            Console.WriteLine($"[POSM IMAGE DEBUG] Querying with numeric values: {qpNum.WhereClause}");
                            var features = await layer.FeatureTable.QueryFeaturesAsync(qpNum);
                            await ProcessFeaturesForImages(features, chunk);
                            totalImagesFound += features.Count();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[POSM IMAGE DEBUG] Numeric query failed, trying as strings: {ex.Message}");
                            // Fallback: treat numerics as strings (same as InspectionHighlighter)
                            var quotedNums = string.Join(",", numericVals.Select(n => $"'{n.Replace("'", "''")}'"));
                            if (!string.IsNullOrWhiteSpace(quotedNums))
                            {
                                var qpNumAsStr = new QueryParameters { WhereClause = $"{gisField} IN ({quotedNums})", ReturnGeometry = true };
                                Console.WriteLine($"[POSM IMAGE DEBUG] Fallback query: {qpNumAsStr.WhereClause}");
                                var features = await layer.FeatureTable.QueryFeaturesAsync(qpNumAsStr);
                                await ProcessFeaturesForImages(features, chunk);
                                totalImagesFound += features.Count();
                            }
                        }
                    }

                    // Query string values (same as InspectionHighlighter)
                    if (stringVals.Count > 0)
                    {
                        string inStrs = string.Join(",", stringVals);
                        var qpStr = new QueryParameters { WhereClause = $"{gisField} IN ({inStrs})", ReturnGeometry = true };
                        Console.WriteLine($"[POSM IMAGE DEBUG] Querying with string values: {qpStr.WhereClause}");
                        var features = await layer.FeatureTable.QueryFeaturesAsync(qpStr);
                        await ProcessFeaturesForImages(features, chunk);
                        totalImagesFound += features.Count();
                    }
                }
                
                Console.WriteLine("[POSM IMAGE DEBUG] ========================================");
                Console.WriteLine($"[POSM IMAGE DEBUG] FINAL SUMMARY:");
                Console.WriteLine($"[POSM IMAGE DEBUG] Database AssetIDs processed: {assetIds.Count}");
                Console.WriteLine($"[POSM IMAGE DEBUG] Features matched and processed: {totalImagesFound}");
                
                // Check final overlay state
                var overlayGraphicsCount = _inspectionImageOverlay?.GetGraphicsCount() ?? 0;
                Console.WriteLine($"[POSM IMAGE DEBUG] Final graphics count in overlay: {overlayGraphicsCount}");
                Console.WriteLine("[POSM IMAGE DEBUG] ========================================");
                
                Console.WriteLine("[POSM IMAGE DEBUG] DisplayImagesForAllAssetsAsync COMPLETED");
                
                // Show message to user
                string message = $"Processed {assetIds.Count} assets from database.\nFound and processed {totalImagesFound} matching features.\nTotal image pins displayed: {overlayGraphicsCount}";
                MessageBox.Show(message, 
                               "Images Processing Complete", 
                               MessageBoxButton.OK, 
                               MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] CRITICAL ERROR in DisplayImagesForAllAssetsAsync: {ex.Message}");
                Console.WriteLine($"[POSM IMAGE DEBUG] Stack trace: {ex.StackTrace}");
                MessageBox.Show($"Critical error loading images:\n{ex.Message}\n\nCheck console for details.", 
                               "Critical Error", 
                               MessageBoxButton.OK, 
                               MessageBoxImage.Error);
            }
        }

        // Helper method to read AssetIDs from database (same pattern as InspectionHighlighter)
        private async Task<List<string>> ReadAssetIdsFromDatabaseAsync()
        {
            Console.WriteLine("[POSM IMAGE DEBUG] ReadAssetIdsFromDatabaseAsync - reading ALL AssetIDs from POSM database");
            
            // Same POSM.mdb location logic as InspectionHighlighter
            string exePath = _configurationService.Configuration?.posmExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
                exePath = @"C:\POSM\POSM.exe";

            string mdbPath = exePath.Replace("POSM.exe", "POSM.mdb", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[POSM IMAGE DEBUG] MDB path: {mdbPath}");

            if (!System.IO.File.Exists(mdbPath))
                throw new InvalidOperationException($"POSM.mdb missing at {mdbPath}");

            // Read ALL AssetIDs directly from database (same query as InspectionHighlighter.ReadIdsFromMdb)
            Console.WriteLine("[POSM IMAGE DEBUG] Reading ALL AssetIDs from SpecialFields table");
            
            const string sql = "SELECT [AssetID] FROM SpecialFields"; // Same as InspectionHighlighter but without DISTINCT
            
            foreach (var provider in new[] { "Microsoft.ACE.OLEDB.12.0", "Microsoft.ACE.OLEDB.16.0" })
            {
                try
                {
                    var list = new List<string>();
                    string conn = $"Provider={provider};Data Source={mdbPath};";
                    Console.WriteLine($"[POSM IMAGE DEBUG] Attempting connection with provider: {provider}");

                    using var c = new System.Data.OleDb.OleDbConnection(conn);
                    c.Open(); // Keep synchronous like InspectionHighlighter
                    using var cmd = new System.Data.OleDb.OleDbCommand(sql, c);
                    using var r = cmd.ExecuteReader(); // Keep synchronous like InspectionHighlighter
                    while (r.Read())
                    {
                        var v = r[0]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(v)) 
                        {
                            list.Add(v);
                        }
                    }
                    Console.WriteLine($"[POSM IMAGE DEBUG] Successfully read {list.Count} AssetIDs from database using {provider}");
                    return list;
                }
                catch (System.Data.OleDb.OleDbException ex)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Provider {provider} failed: {ex.Message}");
                    // try next provider
                }
            }

            throw new InvalidOperationException("ACE OLEDB provider not available (tried 12.0, 16.0).");
        }

        // Helper method to process queried features and add images
        private async Task ProcessFeaturesForImages(FeatureQueryResult featureResult, List<string> assetIdsInChunk)
        {
            var features = featureResult.ToList();
            Console.WriteLine($"[POSM IMAGE DEBUG] ProcessFeaturesForImages: {features.Count} features returned from query");

            string gisField = string.IsNullOrWhiteSpace(_configurationService.Configuration?.idField) ? "AssetID" : _configurationService.Configuration!.idField;

            foreach (var feature in features)
            {
                try
                {
                    // Get the AssetID from the feature
                    string assetId = "";
                    if (feature.Attributes.ContainsKey(gisField))
                    {
                        var assetIdValue = feature.Attributes[gisField];
                        assetId = assetIdValue?.ToString()?.Trim('\"') ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(assetId))
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Feature has empty AssetID, skipping");
                        continue;
                    }

                    Console.WriteLine($"[POSM IMAGE DEBUG] Processing feature with AssetID: '{assetId}'");
                    
                    // Add images for this asset (using our existing overlay)
                    await _inspectionImageOverlay.AddImagesForAssetAsync(assetId, feature, clearExisting: false);
                    Console.WriteLine($"[POSM IMAGE DEBUG] Successfully added images for AssetID: '{assetId}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Error processing feature: {ex.Message}");
                }
            }
        }

        // Helper method to find layers recursively (same as InspectionHighlighter)
        private static FeatureLayer? FindLayerRecursive(IEnumerable<Layer> layers, string name)
        {
            foreach (Layer l in layers)
            {
                if (l is FeatureLayer fl &&
                    fl.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return fl;

                if (l is GroupLayer gl)
                {
                    FeatureLayer? nested = FindLayerRecursive(gl.Layers, name);
                    if (nested != null) return nested;
                }
            }
            return null;
        }
        #endregion

        #region ----------- Enhanced Search with Layer-Based Suggestions -----------
        // Configure the SearchView with both layer-based search and world geocoder
        // Initialize LayerSearchSource for optimized autocomplete suggestions
        private async Task InitializeLayerSearchSourceAsync()
        {
#if DEBUG
            Console.WriteLine("[LAYER SEARCH INIT] InitializeLayerSearchSourceAsync START");
#endif
            
            try
            {
                // Ensure we have enabled query layers
                var enabledLayers = _configurationService.Configuration?.queryLayers?.Where(q => q.enabled) ?? Enumerable.Empty<QueryLayerConfig>();
                var layerCount = enabledLayers.Count();
                
                if (layerCount > 0 && MyMapView?.Map != null)
                {
                    var primaryAttribute = _configurationService.Configuration?.idField ?? "AssetID";
                    
                    // Create LayerSearchSource for autocomplete suggestions
                    _layerSearchSource = new LayerSearchSource(MyMapView, primaryAttribute);
                    
                    // Share the index with LayerSearchSource if available
                    if (_searchIndex != null)
                    {
                        LayerSearchSource.SetSharedIndex(_searchIndex);
                    }
                    
#if DEBUG
                    Console.WriteLine($"[LAYER SEARCH INIT] ? Created with {layerCount} enabled layers, idField: '{primaryAttribute}'");
                    // Test the LayerSearchSource by trying a simple suggestion
                    var testSuggestions = await _layerSearchSource.SuggestAsync("M", CancellationToken.None);
                    Console.WriteLine($"[LAYER SEARCH INIT] Test returned: {testSuggestions.Count} results");
#endif
                }
                else
                {
#if DEBUG
                    Console.WriteLine("[LAYER SEARCH INIT] ? No enabled layers or MyMapView is null");
#endif
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                Console.WriteLine($"[LAYER SEARCH INIT] ERROR: {ex.Message}");
#endif
            }
        }

        
        // Legacy search property removed
        
        // Event handler for search text changes
        private async Task OnSearchViewTextChanged(object sender, object e)
        {
            try
            {
                // Legacy SearchView code removed - using unified search system instead
                if (false) // Disabled legacy code
                {
                    var currentText = "";
                    if (currentText != _lastSearchText && currentText.Length >= 3)
                    {
                        _lastSearchText = currentText;
                        Console.WriteLine($"[SEARCH DEBUG] Search text changed to: '{currentText}'");
                        
                        // Perform our custom search
                        await Task.Delay(500); // Debounce
                        if (currentText == _lastSearchText) // Check if text hasn't changed during delay
                        {
                            await PerformHybridSearchAsync(currentText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SEARCH DEBUG] Error in OnSearchViewTextChanged: {ex.Message}");
            }
        }
        
        // Event handler for search completion
        private void OnSearchViewSearchCompleted(object sender, object e)
        {
            Console.WriteLine("[SEARCH DEBUG] SearchView search completed");
        }
        
        // Hybrid search that tries field-based search first, then geocoding
        private async Task<bool> PerformHybridSearchAsync(string searchText)
        {
            Console.WriteLine($"[HYBRID SEARCH] === Starting hybrid search for: '{searchText}' ===");
            
            try
            {
                // First, try field-based search on configured layers
                Console.WriteLine("[HYBRID SEARCH] Step 1: Trying field-based search...");
                var fieldSearchSucceeded = await TryFieldBasedSearchAsync(searchText);
                if (fieldSearchSucceeded)
                {
                    Console.WriteLine("[HYBRID SEARCH] ? Field-based search found results");
                    return true;
                }
                
                Console.WriteLine("[HYBRID SEARCH] No field-based results, geocoding will be handled by SearchView's default behavior");
                // Let the SearchView's default geocoding handle addresses
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HYBRID SEARCH] ? ERROR: {ex.Message}");
                return false;
            }
        }

        // Build SuggestParameters with USA + current extent constraints
        private SuggestParameters BuildSuggestParams()
        {
            var suggestParams = new SuggestParameters
            {
                CountryCode = "USA",
                MaxResults = 8
            };

            // Add current map extent as search area for local relevance
            if (MyMapView?.VisibleArea?.Extent != null)
            {
                suggestParams.SearchArea = MyMapView.VisibleArea;
                suggestParams.PreferredSearchLocation = MyMapView.VisibleArea.Extent.GetCenter();
            }

            return suggestParams;
        }

        // Build GeocodeParameters with same USA + extent constraints
        private GeocodeParameters BuildGeocodeParams()
        {
            var geocodeParams = new GeocodeParameters
            {
                CountryCode = "USA",
                MaxResults = 1
            };

            // Add current map extent as search area for local relevance
            if (MyMapView?.VisibleArea?.Extent != null)
            {
                geocodeParams.SearchArea = MyMapView.VisibleArea;
                geocodeParams.PreferredSearchLocation = MyMapView.VisibleArea.Extent.GetCenter();
            }

            return geocodeParams;
        }

        // Get address suggestions using ArcGIS World Geocoding Service
        private async Task<List<string>> GetAddressSuggestionsAsync(string searchText, CancellationToken cancellationToken)
        {
            var suggestions = new List<string>();
            
            try
            {
                if (_geocodingService == null || string.IsNullOrWhiteSpace(searchText) || searchText.Length < 2)
                {
                    Console.WriteLine($"[GEOCODING] Skipping suggestions - Service: {(_geocodingService == null ? "null" : "available")}, Text: '{searchText}', Length: {searchText?.Length ?? 0}");
                    return suggestions;
                }

                Console.WriteLine($"[GEOCODING] Starting suggestion request for '{searchText}'");
                var suggestParams = BuildSuggestParams();
                var suggestResults = await _geocodingService.SuggestAsync(searchText, suggestParams, cancellationToken);

                // Clear previous suggestion index and build new one
                _geoSuggestionIndex.Clear();
                
                foreach (var suggestResult in suggestResults.Take(8))
                {
                    var displayText = suggestResult.Label;
                    suggestions.Add(displayText);
                    _geoSuggestionIndex[displayText] = suggestResult;
                }

                Console.WriteLine($"[GEOCODING] ✅ Successfully got {suggestions.Count} address suggestions for '{searchText}'");
            }
            catch (OperationCanceledException ocEx)
            {
                Console.WriteLine($"[GEOCODING] ⚠️ Operation cancelled for '{searchText}': {ocEx.Message}");
                // Expected when cancellation token is triggered - not an error
            }
            catch (System.Net.Http.HttpRequestException httpEx)
            {
                Console.WriteLine($"[GEOCODING] ❌ Network error getting suggestions for '{searchText}': {httpEx.Message}");
                Console.WriteLine($"[GEOCODING] HTTP Error Stack Trace: {httpEx.StackTrace}");
                if (httpEx.InnerException != null)
                {
                    Console.WriteLine($"[GEOCODING] HTTP Inner Exception: {httpEx.InnerException.Message}");
                }
            }
            catch (TimeoutException timeoutEx)
            {
                Console.WriteLine($"[GEOCODING] ❌ Timeout error getting suggestions for '{searchText}': {timeoutEx.Message}");
                Console.WriteLine($"[GEOCODING] Timeout Stack Trace: {timeoutEx.StackTrace}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GEOCODING] ❌ Unexpected error getting suggestions for '{searchText}': {ex.Message}");
                Console.WriteLine($"[GEOCODING] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[GEOCODING] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[GEOCODING] Inner Exception: {ex.InnerException.Message}");
                    Console.WriteLine($"[GEOCODING] Inner Stack Trace: {ex.InnerException.StackTrace}");
                }
            }

            return suggestions;
        }

        // Geocode from selected suggestion and zoom to result
        private async Task<bool> GeocodeFromSuggestionAsync(string suggestion, CancellationToken cancellationToken)
        {
            try
            {
                if (_geocodingService == null)
                {
                    Console.WriteLine($"[GEOCODING] ❌ Geocoding service is null for suggestion '{suggestion}'");
                    return false;
                }
                
                if (!_geoSuggestionIndex.TryGetValue(suggestion, out var suggestResult))
                {
                    Console.WriteLine($"[GEOCODING] ❌ Suggestion '{suggestion}' not found in index. Available: {string.Join(", ", _geoSuggestionIndex.Keys.Take(3))}...");
                    return false;
                }

                Console.WriteLine($"[GEOCODING] Starting geocoding for suggestion '{suggestion}'");
                var geocodeParams = BuildGeocodeParams();
                var geocodeResults = await _geocodingService.GeocodeAsync(suggestResult, geocodeParams, cancellationToken);

                if (geocodeResults?.FirstOrDefault() is GeocodeResult result && result.DisplayLocation != null)
                {
                    Console.WriteLine($"[GEOCODING] ✅ Geocoding successful for '{suggestion}', zooming to location");

                    // Zoom to the geocoded location (5000 scale as specified)
                    await MyMapView.SetViewpointCenterAsync(result.DisplayLocation, 5000);
                    
                    Console.WriteLine($"[GEOCODING] ✅ Zoomed to: {result.Label} at {result.DisplayLocation}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[GEOCODING] ⚠️ No valid geocoding result for '{suggestion}' - Results count: {geocodeResults?.Count() ?? 0}");
                    return false;
                }
            }
            catch (OperationCanceledException ocEx)
            {
                Console.WriteLine($"[GEOCODING] ⚠️ Geocoding operation cancelled for '{suggestion}': {ocEx.Message}");
                return false;
            }
            catch (System.Net.Http.HttpRequestException httpEx)
            {
                Console.WriteLine($"[GEOCODING] ❌ Network error geocoding suggestion '{suggestion}': {httpEx.Message}");
                Console.WriteLine($"[GEOCODING] HTTP Error Stack Trace: {httpEx.StackTrace}");
                if (httpEx.InnerException != null)
                {
                    Console.WriteLine($"[GEOCODING] HTTP Inner Exception: {httpEx.InnerException.Message}");
                }
                return false;
            }
            catch (TimeoutException timeoutEx)
            {
                Console.WriteLine($"[GEOCODING] ❌ Timeout error geocoding suggestion '{suggestion}': {timeoutEx.Message}");
                Console.WriteLine($"[GEOCODING] Timeout Stack Trace: {timeoutEx.StackTrace}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GEOCODING] ❌ Unexpected error geocoding suggestion '{suggestion}': {ex.Message}");
                Console.WriteLine($"[GEOCODING] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[GEOCODING] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[GEOCODING] Inner Exception: {ex.InnerException.Message}");
                    Console.WriteLine($"[GEOCODING] Inner Stack Trace: {ex.InnerException.StackTrace}");
                }
                return false;
            }
        }

        #endregion

        #region ----------- (Optional) Online geocoding helpers -----------
        private async Task InitOnlineLocatorAsync()
        {
            // Only initialize if we can set an API key (and are online)
            if (!await _networkService.EnsureOnlineApiKeyIfAvailableAsync()) { _onlineLocator = null; return; }

            var uri = new Uri("https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer");
            _onlineLocator = await LocatorTask.CreateAsync(uri);
        }

        public async Task<bool> TryGeocodeAndZoomAsync(string text, double scale = 5000)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (_onlineLocator == null)
            {
                if (!await _networkService.EnsureOnlineApiKeyIfAvailableAsync()) return false;
                await InitOnlineLocatorAsync();
                if (_onlineLocator == null) return false;
            }

            var results = await _onlineLocator.GeocodeAsync(text);
            var first = results.FirstOrDefault();
            if (first == null) return false;

            var pt = first.DisplayLocation;
            await MyMapView.SetViewpointCenterAsync(pt, scale);
            return true;
        }

        // Comprehensive search method that combines geocoding and field-based search
        public async Task<bool> PerformComprehensiveSearchAsync(string searchText)
        {
            Console.WriteLine($"[COMPREHENSIVE SEARCH] === Starting search for: '{searchText}' ===");
            
            if (string.IsNullOrWhiteSpace(searchText))
            {
                Console.WriteLine("[COMPREHENSIVE SEARCH] Search text is empty, returning false");
                return false;
            }

            try
            {
                // First, try field-based search on configured layers
                Console.WriteLine("[COMPREHENSIVE SEARCH] Step 1: Trying field-based search...");
                var fieldSearchSucceeded = await TryFieldBasedSearchAsync(searchText);
                if (fieldSearchSucceeded)
                {
                    Console.WriteLine("[COMPREHENSIVE SEARCH] ? Field-based search succeeded");
                    return true;
                }

                // If field search didn't find anything, try geocoding
                Console.WriteLine("[COMPREHENSIVE SEARCH] Step 2: Field search failed, trying geocoding...");
                var geocodeSucceeded = await TryGeocodeAndZoomAsync(searchText);
                if (geocodeSucceeded)
                {
                    Console.WriteLine("[COMPREHENSIVE SEARCH] ? Geocoding succeeded");
                    return true;
                }

                // No results found
                Console.WriteLine($"[COMPREHENSIVE SEARCH] ? No results found for: '{searchText}'");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COMPREHENSIVE SEARCH] ? ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error in comprehensive search: {ex.Message}");
                return false;
            }
        }

        // Search for features based on configured fields
        private async Task<bool> TryFieldBasedSearchAsync(string searchText)
        {
#if DEBUG
            Console.WriteLine($"[FIELD SEARCH] === Starting field-based search for: '{searchText}' ===");
#endif
            
            try
            {
                // First try the ultra-fast in-memory index
                if (_searchIndex != null)
                {
                    var indexResults = _searchIndex.Search(searchText, 1);
                    if (indexResults.Any())
                    {
                        var firstResult = indexResults.First();
#if DEBUG
                        Console.WriteLine($"[INDEX] ? Instant result from index: {firstResult.LayerName}.{firstResult.FieldName} = '{firstResult.Value}'");
#endif
                        
                        // Find the layer and select the feature
                        if (MyMapView?.Map != null && firstResult.Feature != null)
                        {
                            var layer = MyMapView.Map.OperationalLayers
                                .OfType<FeatureLayer>()
                                .FirstOrDefault(l => l.Name.Equals(firstResult.LayerName, StringComparison.OrdinalIgnoreCase));
                            
                            if (layer != null)
                            {
                                layer.ClearSelection();
                                layer.SelectFeature(firstResult.Feature);
                                
                                // Enhanced zoom to the feature with highlight
                                if (firstResult.Feature.Geometry != null)
                                {
                                    var geometry = firstResult.Feature.Geometry;

                                    // Set selection color
                                    MyMapView.SelectionProperties.Color = System.Drawing.Color.Cyan;

                                    // For point geometries, zoom to a specific scale
                                    if (geometry is MapPoint point)
                                    {
                                        await MyMapView.SetViewpointCenterAsync(point, 1000);
                                    }
                                    else if (geometry.Extent != null)
                                    {
                                        await MyMapView.SetViewpointGeometryAsync(geometry.Extent, 10);
                                    }

                                    // Add highlight graphic with glow effect
                                    AddHighlightGraphic(geometry);
                                }
                                return true;
                            }
                        }
                    }
                }
                
                // Fallback to database queries if index doesn't have results
                if (MyMapView?.Map == null)
                {
#if DEBUG
                    Console.WriteLine("[FIELD SEARCH] MapView or Map is null");
#endif
                    return false;
                }

                var queryLayers = _configurationService.Configuration?.queryLayers?.Where(q => q.enabled).ToList();
                Console.WriteLine($"[FIELD SEARCH] Found {queryLayers?.Count ?? 0} enabled query layers");
                
                if (queryLayers == null || !queryLayers.Any())
                {
                    Console.WriteLine("[FIELD SEARCH] No enabled query layers configured");
                    return false;
                }

                foreach (var queryConfig in queryLayers)
                {
                    Console.WriteLine($"[FIELD SEARCH] Searching layer: '{queryConfig.layerName}'");
                    
                    var layer = MyMapView.Map.OperationalLayers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(queryConfig.layerName, StringComparison.OrdinalIgnoreCase));

                    if (layer?.FeatureTable == null)
                    {
                        Console.WriteLine($"[FIELD SEARCH] Layer '{queryConfig.layerName}' not found or has no feature table");
                        continue;
                    }
                    
                    Console.WriteLine($"[FIELD SEARCH] Layer '{queryConfig.layerName}' found with feature table");

                    var searchFields = queryConfig.searchFields?.Any() == true 
                        ? queryConfig.searchFields 
                        : new List<string> { _configurationService.Configuration?.idField ?? "AssetID" };
                    
                    Console.WriteLine($"[FIELD SEARCH] Search fields: [{string.Join(", ", searchFields)}]");

                    foreach (var fieldName in searchFields)
                    {
                        try
                        {
                            // Build query for exact or partial match
                            var whereClause = $"UPPER({fieldName}) LIKE UPPER('%{searchText}%')";
                            Console.WriteLine($"[FIELD SEARCH] Querying field '{fieldName}' with: {whereClause}");
                            
                            var queryParams = new QueryParameters
                            {
                                WhereClause = whereClause,
                                ReturnGeometry = true,
                                MaxFeatures = 1
                            };

                            var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams);
                            var resultCount = queryResult.Count();
                            Console.WriteLine($"[FIELD SEARCH] Query returned {resultCount} features");
                            
                            var firstFeature = queryResult.FirstOrDefault();

                            if (firstFeature?.Geometry != null)
                            {
                                var featureValue = firstFeature.Attributes[fieldName]?.ToString() ?? "[null]";
                                Console.WriteLine($"[FIELD SEARCH] ? Found feature with {fieldName}='{featureValue}'");
                                
                                // Clear all selections first
                                foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                                {
                                    lyr.ClearSelection();
                                }

                                // Select the feature
                                layer.SelectFeature(firstFeature as Feature);
                                Console.WriteLine($"[FIELD SEARCH] Feature selected");

                                // Set selection color
                                MyMapView.SelectionProperties.Color = System.Drawing.Color.Cyan;

                                // Enhanced zoom to the feature
                                var geometry = firstFeature.Geometry;
                                if (geometry != null)
                                {
                                    // For point geometries, zoom to a specific scale
                                    if (geometry is MapPoint point)
                                    {
                                        await MyMapView.SetViewpointCenterAsync(point, 1000);
                                    }
                                    else if (geometry.Extent != null)
                                    {
                                        await MyMapView.SetViewpointGeometryAsync(geometry.Extent, 10);
                                    }
                                    Console.WriteLine($"[FIELD SEARCH] Zoomed to feature with enhanced view");

                                    // Add highlight graphic with glow effect
                                    AddHighlightGraphic(geometry);
                                }

                                Console.WriteLine($"[FIELD SEARCH] ? SUCCESS: Found in {queryConfig.layerName}.{fieldName}");
                                return true;
                            }
                            else
                            {
                                Console.WriteLine($"[FIELD SEARCH] No features found in field '{fieldName}'");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FIELD SEARCH] ? ERROR querying field '{fieldName}': {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"Error searching {fieldName}: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine("[FIELD SEARCH] No matching features found in any configured layer/field");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FIELD SEARCH] ? ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error in field-based search: {ex.Message}");
                return false;
            }
        }
        #endregion
        
        #region ----------- Unified Search Event Handlers -----------
        // Toggle button event handlers
        private void SearchModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("[TOGGLE EVENT] SearchModeToggle_Checked fired");
                _isAddressMode = true;
                UpdateSearchMode();
                Console.WriteLine("[UNIFIED SEARCH] Switched to Address mode");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TOGGLE ERROR] Exception in SearchModeToggle_Checked: {ex.Message}");
                Console.WriteLine($"[TOGGLE ERROR] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[TOGGLE ERROR] Inner Exception: {ex.InnerException.Message}");
                }
                
                // Ensure mode is still set correctly
                _isAddressMode = true;
                Console.WriteLine("[TOGGLE ERROR] Forced _isAddressMode to true after exception");
            }
        }
        
        private void SearchModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("[TOGGLE EVENT] SearchModeToggle_Unchecked fired");
                _isAddressMode = false;
                UpdateSearchMode();
                Console.WriteLine("[UNIFIED SEARCH] Switched to Asset mode");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TOGGLE ERROR] Exception in SearchModeToggle_Unchecked: {ex.Message}");
                Console.WriteLine($"[TOGGLE ERROR] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[TOGGLE ERROR] Inner Exception: {ex.InnerException.Message}");
                }
                
                // Ensure mode is still set correctly
                _isAddressMode = false;
                Console.WriteLine("[TOGGLE ERROR] Forced _isAddressMode to false after exception");
            }
        }
        
        // Unified search button click
        private async void UnifiedSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformUnifiedSearchAsync();
        }
        
        // Enter key handler
        private async void UnifiedSearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                
                // If suggestions/results visible, act on selection
                if (SuggestionsListBox?.Visibility == Visibility.Visible && HasSuggestions())
                {
                    if (_showingResults)
                    {
                        // Execute selected result
                        var idx = SuggestionsListBox.SelectedIndex >= 0 ? SuggestionsListBox.SelectedIndex : 0;
                        if (idx >= 0 && idx < _lastResults.Count)
                        {
                            await ExecuteSelectedResultAsync(_lastResults[idx]);
                            return;
                        }
                    }
                    else
                    {
                        // Execute selected suggestion using safe helper method
                        string selectedSuggestion = GetSelectedSuggestion();

                        // Execute the selected suggestion (fills textbox + searches + zooms)
                        if (!string.IsNullOrEmpty(selectedSuggestion))
                        {
                            if (_isAddressMode)
                            {
                                await ExecuteSelectedSuggestionAsync(selectedSuggestion);
                            }
                            else
                            {
                                await OnAssetSuggestionChosenAsync(selectedSuggestion);
                            }
                            return;
                        }
                    }
                }
                
                HideSuggestions();

                // Handle direct text input based on mode
                if (_isAddressMode)
                {
                    await PerformUnifiedSearchAsync();
                }
                else
                {
                    // Asset mode: parse manual input and execute
                    var inputText = UnifiedSearchTextBox?.Text ?? "";
                    if (!string.IsNullOrWhiteSpace(inputText))
                    {
                        await ExecuteAssetFromTextAsync(inputText);
                    }
                }
            }
            else if (e.Key == Key.Down && SuggestionsListBox?.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                NavigateSuggestions(1); // Move down
            }
            else if (e.Key == Key.Up && SuggestionsListBox?.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                NavigateSuggestions(-1); // Move up
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideSuggestions();
            }
        }
        
        // Helper method to extract search text from suggestion
        private string ExtractSearchTextFromSuggestion(string suggestion)
        {
            if (string.IsNullOrEmpty(suggestion)) return "";
            
            // Suggestions are in format "LayerName: Value"
            var parts = suggestion.Split(new[] { ": " }, 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[1] : suggestion;
        }
        
        // Update UI based on search mode
        private void UpdateSearchMode()
        {
            try
            {
                // Debug toggle button state
                if (SearchModeToggle != null)
                {
                    Console.WriteLine($"[TOGGLE DEBUG] _isAddressMode: {_isAddressMode}, ToggleButton.IsChecked: {SearchModeToggle.IsChecked}, Content: {SearchModeToggle.Content}");
                }
                else
                {
                    Console.WriteLine($"[TOGGLE DEBUG] SearchModeToggle is null, _isAddressMode: {_isAddressMode}");
                }
                
                if (UnifiedSearchTextBox != null)
                {
                    try
                    {
                        // Update placeholder text in code since we can't easily access template elements
                        UnifiedSearchTextBox.Tag = _isAddressMode ? "Search addresses..." : "Search assets (AssetID, StartID)...";
                        
                        // Clear current text when mode changes
                        UnifiedSearchTextBox.Text = "";
                        Console.WriteLine($"[UPDATE MODE] ✅ Updated search textbox for {(_isAddressMode ? "Address" : "Asset")} mode");
                    }
                    catch (Exception textBoxEx)
                    {
                        Console.WriteLine($"[UPDATE MODE] ❌ Error updating textbox: {textBoxEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[UPDATE MODE] ⚠️ UnifiedSearchTextBox is null");
                }
                
                if (UnifiedSearchButton != null)
                {
                    try
                    {
                        UnifiedSearchButton.ToolTip = _isAddressMode ? "Search addresses" : "Search assets";
                        UnifiedSearchButton.Background = new SolidColorBrush(_isAddressMode 
                            ? System.Windows.Media.Color.FromRgb(0x21, 0x96, 0xF3)  // Blue for addresses
                            : System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // Green for assets
                        Console.WriteLine($"[UPDATE MODE] ✅ Updated search button for {(_isAddressMode ? "Address" : "Asset")} mode");
                    }
                    catch (Exception buttonEx)
                    {
                        Console.WriteLine($"[UPDATE MODE] ❌ Error updating button: {buttonEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[UPDATE MODE] ⚠️ UnifiedSearchButton is null");
                }
                
                if (SearchModeIndicator != null)
                {
                    try
                    {
                        SearchModeIndicator.Text = _isAddressMode
                            ? "Address search mode - toggle to switch to Assets"
                            : "Asset search mode - toggle to switch to Addresses";
                        SearchModeIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
                        Console.WriteLine($"[UPDATE MODE] ✅ Updated mode indicator for {(_isAddressMode ? "Address" : "Asset")} mode");
                    }
                    catch (Exception indicatorEx)
                    {
                        Console.WriteLine($"[UPDATE MODE] ❌ Error updating mode indicator: {indicatorEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[UPDATE MODE] ⚠️ SearchModeIndicator is null");
                }
                
                // Hide suggestions when switching modes
                try
                {
                    HideSuggestions();
                    Console.WriteLine($"[UPDATE MODE] ✅ Hidden suggestions for mode switch");
                }
                catch (Exception hideEx)
                {
                    Console.WriteLine($"[UPDATE MODE] ❌ Error hiding suggestions: {hideEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UPDATE MODE] ❌ Critical error in UpdateSearchMode: {ex.Message}");
                Console.WriteLine($"[UPDATE MODE] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[UPDATE MODE] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[UPDATE MODE] Inner Exception: {ex.InnerException.Message}");
                }
            }
        }
        
        // Main unified search method
        private async Task PerformUnifiedSearchAsync()
        {
            var searchText = UnifiedSearchTextBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(searchText))
            {
                Console.WriteLine("[UNIFIED SEARCH] Search text is empty");
                return;
            }
            
            Console.WriteLine($"[UNIFIED SEARCH] === Searching for: '{searchText}' (Mode: {(_isAddressMode ? "Address" : "Asset")}) ===");
            
            // Clear previous pin overlay/results
            try { _searchOverlay?.Graphics.Clear(); } catch { }
            _showingResults = false;
            HideSuggestions();

            // Show searching indicator
            if (SearchModeIndicator != null)
            {
                SearchModeIndicator.Text = $"Searching for: {searchText}...";
                SearchModeIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Orange);
            }
            
            try
            {
                bool searchSuccess = false;
                
                if (_isAddressMode)
                {
                    // Perform address geocoding
                    searchSuccess = await PerformGeocodingSearchAsync(searchText);
                }
                else
                {
                    // Enhanced asset search using new ILayerSearchService and replica cache
                    try
                    {
                        // First try the replica cache for instant results
                        var cacheResults = _replicaCacheService.Search(searchText, 50);
                        List<SearchResultItem> searchResults;

                        if (cacheResults.Any())
                        {
                            // Convert cache results to SearchResultItem format
                            searchResults = cacheResults.Select(entry => new SearchResultItem
                            {
                                LayerName = entry.LayerName,
                                Feature = entry.Feature!,
                                DisplayText = entry.DisplayValue
                            }).ToList();

                            Console.WriteLine($"[REPLICA CACHE] Found {searchResults.Count} cached results");
                        }
                        else
                        {
                            // Fallback to live search and lazy loading
                            Console.WriteLine($"[LAZY LOAD] Cache miss, performing live search for '{searchText}'");
                            searchResults = await _replicaCacheService.LazyLoadAndCacheAsync(searchText);
                        }

                        _lastResults = searchResults;
                        if (searchResults.Any())
                        {
                            // Display results in the suggestions panel
                            var texts = searchResults.Select(r => $"{r.LayerName}: {r.DisplayText}").Distinct().Take(50).ToList();
                            DisplaySuggestions(texts);
                            _showingResults = true;

                            // Zoom/highlight first result for quick UX
                            await ZoomToResultAsync(searchResults.First());
                            searchSuccess = true;
                        }
                        else
                        {
                            searchSuccess = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ASSET SEARCH] Error: {ex.Message}");
                        _logger.LogError(ex, "[ASSET SEARCH] Search failed: {Error}", ex.Message);
                        searchSuccess = false;
                    }
                }
                
                // Handle search result feedback
                if (searchSuccess)
                {
                    if (SearchModeIndicator != null)
                    {
                        SearchModeIndicator.Text = $"? Found: {searchText}";
                        SearchModeIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Green);
                    }
                    
                    // Clear the search box for next search
                    if (UnifiedSearchTextBox != null)
                    {
                        UnifiedSearchTextBox.Text = "";
                    }
                    
                    Console.WriteLine($"[UNIFIED SEARCH] ? Successfully found: '{searchText}' in {(_isAddressMode ? "address" : "asset")} search");
                }
                else
                {
                    if (SearchModeIndicator != null)
                    {
                        SearchModeIndicator.Text = $"? Not found: {searchText}";
                        SearchModeIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red);
                    }
                    Console.WriteLine($"[UNIFIED SEARCH] ? Not found: '{searchText}' in {(_isAddressMode ? "address" : "asset")} search");
                }
                
                // Reset indicator after 3 seconds
                await Task.Delay(3000);
                UpdateSearchMode(); // This will reset the indicator text
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNIFIED SEARCH] ? Error during search: {ex.Message}");
                
                if (SearchModeIndicator != null)
                {
                    SearchModeIndicator.Text = "Search error occurred";
                    SearchModeIndicator.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red);
                }
                
                // Reset indicator after 3 seconds
                await Task.Delay(3000);
                UpdateSearchMode();
            }
        }

        private Esri.ArcGISRuntime.UI.GraphicsOverlay? _searchOverlay;
        private void EnsureSearchOverlay()
        {
            if (_searchOverlay != null && MyMapView.GraphicsOverlays.Contains(_searchOverlay)) return;
            _searchOverlay = new Esri.ArcGISRuntime.UI.GraphicsOverlay { Id = "SearchResultOverlay" };
            // Keep on top
            if (MyMapView.GraphicsOverlays.Contains(_searchOverlay))
            {
                MyMapView.GraphicsOverlays.Remove(_searchOverlay);
            }
            MyMapView.GraphicsOverlays.Add(_searchOverlay);
        }

        private async Task ZoomToResultAsync(SearchResultItem item)
        {
            try
            {
                if (item?.Feature?.Geometry == null || MyMapView?.Map == null) return;

                // Clear all selections first
                foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                {
                    lyr.ClearSelection();
                }

                // Select in its layer
                var layer = MyMapView.Map.OperationalLayers.OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(item.LayerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                {
                    layer.SelectFeature(item.Feature);

                    // Set selection color to bright cyan for visibility
                    MyMapView.SelectionProperties.Color = System.Drawing.Color.Cyan;
                }

                // Enhanced zoom - zoom in much closer for better visibility
                var geometry = item.Feature.Geometry;
                if (geometry != null)
                {
                    // For point geometries, zoom to a specific scale
                    if (geometry is MapPoint point)
                    {
                        // Zoom to scale 1:1000 for points (very close)
                        await MyMapView.SetViewpointCenterAsync(point, 1000);
                    }
                    else if (geometry.Extent != null)
                    {
                        // For lines/polygons, zoom to extent with minimal padding (10 units)
                        await MyMapView.SetViewpointGeometryAsync(geometry.Extent, 10);
                    }

                    // Add a highlight graphic with glow effect
                    AddHighlightGraphic(geometry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RESULTS] Error zooming to result: {ex.Message}");
            }
        }

        private void AddHighlightGraphic(Esri.ArcGISRuntime.Geometry.Geometry geometry)
        {
            try
            {
                EnsureSearchOverlay();
                _searchOverlay?.Graphics.Clear();

                // Create appropriate symbol based on geometry type
                Esri.ArcGISRuntime.Symbology.Symbol symbol = null;

                if (geometry is MapPoint)
                {
                    // Create a glowing marker for points
                    var markerSymbol = new Esri.ArcGISRuntime.Symbology.SimpleMarkerSymbol
                    {
                        Style = Esri.ArcGISRuntime.Symbology.SimpleMarkerSymbolStyle.Circle,
                        Color = System.Drawing.Color.FromArgb(150, 0, 255, 255), // Semi-transparent cyan
                        Size = 20,
                        Outline = new Esri.ArcGISRuntime.Symbology.SimpleLineSymbol
                        {
                            Color = System.Drawing.Color.Cyan,
                            Width = 3
                        }
                    };
                    symbol = markerSymbol;
                }
                else if (geometry is Polyline)
                {
                    // Create a glowing line for polylines
                    symbol = new Esri.ArcGISRuntime.Symbology.SimpleLineSymbol
                    {
                        Style = Esri.ArcGISRuntime.Symbology.SimpleLineSymbolStyle.Solid,
                        Color = System.Drawing.Color.Cyan,
                        Width = 5
                    };
                }
                else if (geometry is Polygon)
                {
                    // Create a glowing fill for polygons
                    symbol = new Esri.ArcGISRuntime.Symbology.SimpleFillSymbol
                    {
                        Style = Esri.ArcGISRuntime.Symbology.SimpleFillSymbolStyle.Solid,
                        Color = System.Drawing.Color.FromArgb(100, 0, 255, 255), // Semi-transparent cyan
                        Outline = new Esri.ArcGISRuntime.Symbology.SimpleLineSymbol
                        {
                            Color = System.Drawing.Color.Cyan,
                            Width = 3
                        }
                    };
                }

                if (symbol != null)
                {
                    var graphic = new Esri.ArcGISRuntime.UI.Graphic(geometry, symbol);
                    _searchOverlay.Graphics.Add(graphic);

                    // Animate the highlight (pulsing effect)
                    StartPulsingAnimation();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HIGHLIGHT] Error adding highlight graphic: {ex.Message}");
            }
        }

        private System.Windows.Threading.DispatcherTimer? _pulseTimer;
        private void StartPulsingAnimation()
        {
            try
            {
                // Stop any existing animation
                _pulseTimer?.Stop();

                _pulseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };

                int pulseCount = 0;
                bool increasing = true;

                _pulseTimer.Tick += (s, e) =>
                {
                    if (_searchOverlay?.Graphics.Count > 0)
                    {
                        var graphic = _searchOverlay.Graphics[0];
                        var symbol = graphic.Symbol;

                        // Adjust opacity for pulsing effect
                        if (symbol is Esri.ArcGISRuntime.Symbology.SimpleMarkerSymbol marker)
                        {
                            int alpha = increasing ? 150 + (pulseCount * 10) : 250 - (pulseCount * 10);
                            marker.Color = System.Drawing.Color.FromArgb(alpha, 0, 255, 255);
                        }
                        else if (symbol is Esri.ArcGISRuntime.Symbology.SimpleFillSymbol fill)
                        {
                            int alpha = increasing ? 100 + (pulseCount * 5) : 150 - (pulseCount * 5);
                            fill.Color = System.Drawing.Color.FromArgb(alpha, 0, 255, 255);
                        }

                        pulseCount++;
                        if (pulseCount > 10)
                        {
                            pulseCount = 0;
                            increasing = !increasing;
                        }
                    }
                };

                _pulseTimer.Start();

                // Stop pulsing after 3 seconds
                Task.Delay(3000).ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _pulseTimer?.Stop();
                    });
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ANIMATION] Error starting pulse animation: {ex.Message}");
            }
        }
        
        // Initialize geocoding service
        private async Task InitializeGeocodingServiceAsync()
        {
            try
            {
                Console.WriteLine("[GEOCODING] Initializing geocoding service...");
                
                // Use ArcGIS World Geocoding Service with API key
                var geocodingServiceUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer";
                _geocodingService = await LocatorTask.CreateAsync(new Uri(geocodingServiceUrl));
                
                Console.WriteLine("[GEOCODING] ? Geocoding service initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GEOCODING] ? Error initializing geocoding service: {ex.Message}");
                _geocodingService = null;
            }
        }
        
        // Perform geocoding search
        private async Task<bool> PerformGeocodingSearchAsync(string address)
        {
            try
            {
                Console.WriteLine($"[GEOCODING] === Geocoding address: '{address}' ===");
                
                if (_geocodingService == null)
                {
                    Console.WriteLine("[GEOCODING] ? Geocoding service not available");
                    return false;
                }
                
                // Get current map extent for local geocoding
                Envelope? searchExtent = MyMapView?.GetCurrentViewpoint(ViewpointType.BoundingGeometry)?.TargetGeometry as Envelope;
                
                var geocodeParams = new GeocodeParameters
                {
                    MaxResults = 5,
                    ResultAttributeNames = { "*" },
                    PreferredSearchLocation = searchExtent?.GetCenter(),
                    SearchArea = searchExtent,
                    CountryCode = "USA"  // Constrain to United States
                };
                
                Console.WriteLine($"[GEOCODING] Searching within current map extent with USA constraint");
                var geocodeResults = await _geocodingService.GeocodeAsync(address, geocodeParams);
                
                if (geocodeResults?.Any() == true)
                {
                    // Filter results to only those within the current extent or nearby
                    var bestResult = geocodeResults.First(); // Take the best match within the search area
                    Console.WriteLine($"[GEOCODING] ? Found local address: {bestResult.Label}");
                    Console.WriteLine($"[GEOCODING] Location: {bestResult.DisplayLocation}");
                    
                    // Enhanced zoom to the result
                    if (MyMapView != null && bestResult.DisplayLocation != null)
                    {
                        await MyMapView.SetViewpointCenterAsync(bestResult.DisplayLocation, 1000);
                        Console.WriteLine("[GEOCODING] ? Map zoomed to geocoded location with enhanced view");

                        // Drop a glowing pin at the geocoded location
                        try
                        {
                            AddHighlightGraphic(bestResult.DisplayLocation);
                        }
                        catch { }
                    }
                    
                    return true;
                }
                else
                {
                    Console.WriteLine($"[GEOCODING] ? No local geocoding results found for: '{address}' within current area");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GEOCODING] ? Error during geocoding: {ex.Message}");
                return false;
            }
        }
        
        // Event handler for text changes in unified search textbox (autocomplete)
        // Optimized TextBox event handler with debouncing and performance improvements
        private void UnifiedSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;
            
            var searchText = textBox.Text?.Trim() ?? "";
#if DEBUG
            if (searchText.Length >= 2)
                Console.WriteLine($"[AUTOCOMPLETE] Text: '{searchText}' (Mode: {(_isAddressMode ? "Address" : "Asset")})");
#endif
            
            // Cancel any existing search operations immediately
            _currentSearchCts?.Cancel();
            
            // Stop and restart the debounce timer
            _debounceTimer?.Stop();
            
            // Show autocomplete suggestions in both Address and Asset modes
            if (string.IsNullOrEmpty(searchText) || searchText.Length < 2)
            {
                HideSuggestions();
                _showingResults = false;
                return;
            }
            
            // Initialize debounce timer if needed
            if (_debounceTimer == null)
            {
                _debounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300) // 300ms debounce delay
                };
                _debounceTimer.Tick += async (s, args) =>
                {
                    _debounceTimer.Stop();
                    var currentSearchText = UnifiedSearchTextBox.Text?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(currentSearchText) && currentSearchText.Length >= 2)
                    {
                        await ShowSuggestionsAsync(currentSearchText);
                    }
                };
            }
            
#if DEBUG
            Console.WriteLine($"[DEBOUNCE] Timer started: '{searchText}'");
#endif
            _debounceTimer.Start();
            _showingResults = false;
        }
        
        // Event handler for when textbox loses focus (hide suggestions)
        private async void UnifiedSearchTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                Console.WriteLine("[AUTOCOMPLETE] TextBox lost focus");
                // Delay hiding to allow for selection with proper async/await
                await Task.Delay(200);
                
                // Use Dispatcher.BeginInvoke for better thread safety
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        HideSuggestions();
                        Console.WriteLine("[AUTOCOMPLETE] ✅ Suggestions hidden after focus lost");
                    }
                    catch (Exception hidEx)
                    {
                        Console.WriteLine($"[AUTOCOMPLETE] ❌ Error hiding suggestions on focus lost: {hidEx.Message}");
                        Console.WriteLine($"[AUTOCOMPLETE] Hide Error Stack Trace: {hidEx.StackTrace}");
                    }
                }));
            }
            catch (TaskCanceledException tcEx)
            {
                Console.WriteLine($"[AUTOCOMPLETE] ⚠️ Focus lost delay was cancelled: {tcEx.Message}");
            }
            catch (ObjectDisposedException odEx)
            {
                Console.WriteLine($"[AUTOCOMPLETE] ⚠️ UI object disposed during focus lost: {odEx.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTOCOMPLETE] ❌ Unexpected error in UnifiedSearchTextBox_LostFocus: {ex.Message}");
                Console.WriteLine($"[AUTOCOMPLETE] Error Type: {ex.GetType().Name}");
                Console.WriteLine($"[AUTOCOMPLETE] Stack Trace: {ex.StackTrace}");
                
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[AUTOCOMPLETE] Inner Exception: {ex.InnerException.Message}");
                }
                
                // Try to hide suggestions anyway as a fallback
                try
                {
                    HideSuggestions();
                    Console.WriteLine("[AUTOCOMPLETE] ✅ Fallback: Suggestions hidden despite exception");
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"[AUTOCOMPLETE] ❌ Fallback hide suggestions also failed: {fallbackEx.Message}");
                }
            }
        }
        
        // Event handler for selection in suggestions listbox
        private async void SuggestionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Only process if there are added items (user selected something)
            if (e.AddedItems.Count == 0) return;

            var listBox = sender as ListBox;
            if (_showingResults && listBox?.SelectedIndex >= 0 && listBox.SelectedIndex < _lastResults.Count)
            {
                var item = _lastResults[listBox.SelectedIndex];
                Console.WriteLine($"[RESULTS] Selected: {item.LayerName}: {item.DisplayText}");
                // Fill textbox and zoom on single click for results
                await ExecuteSelectedResultAsync(item);
            }
            else if (listBox?.SelectedItem is string selectedSuggestion)
            {
                Console.WriteLine($"[AUTOCOMPLETE] Suggestion selected: '{selectedSuggestion}'");
                // Fill textbox and execute search on single click
                await ExecuteSelectedSuggestionAsync(selectedSuggestion);
            }
        }

        // Event handler for double-click on suggestion (now just delegates to selection handler)
        private async void SuggestionsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Double-click now handled by SelectionChanged for consistency
            // This prevents duplicate execution
            e.Handled = true;
        }
        
        // High-performance suggestions with caching, debouncing, and cancellation
        private async Task ShowSuggestionsAsync(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText) || searchText.Length < 2)
            {
                HideSuggestions();
                return;
            }
            
            // Prevent duplicate searches
            lock (_searchLock)
            {
                if (_lastSearchText == searchText)
                {
    #if DEBUG
                Console.WriteLine($"[PERFORMANCE] Duplicate skipped: '{searchText}'");
#endif
                    return;
                }
                _lastSearchText = searchText;
            }
            
            try
            {
#if DEBUG
                Console.WriteLine($"[AUTOCOMPLETE] Getting suggestions: '{searchText}'");
#endif
                
                // Check cache first for instant results
                if (_suggestionCache.TryGetValue(searchText.ToLowerInvariant(), out var cachedSuggestions))
                {
#if DEBUG
                    Console.WriteLine($"[CACHE] Hit: {cachedSuggestions.Count} items");
#endif
                    DisplaySuggestions(cachedSuggestions);
                    return;
                }
                
                // Get suggestions based on search mode
                List<string> cacheSuggestions = new List<string>();
                
                if (_isAddressMode)
                {
                    // Address mode: get geocoding suggestions
                    _currentSearchCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    cacheSuggestions = await GetAddressSuggestionsAsync(searchText, _currentSearchCts.Token);
                }
                else
                {
                    // Asset mode: use new structured suggestions
                    _currentSearchCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var structuredSuggestions = await GetAssetSuggestionsAsync(searchText, _currentSearchCts.Token);

                    if (structuredSuggestions.Any())
                    {
                        DisplayStructuredSuggestions(structuredSuggestions);
                        return;
                    }

                    // Fallback to legacy cache for backward compatibility
                    cacheSuggestions = _replicaCacheService.GetSuggestions(searchText, 10);

                    if (!cacheSuggestions.Any() && _searchIndex != null)
                    {
                        cacheSuggestions = _searchIndex.GetSuggestions(searchText, 10);
                    }
                }
                
                if (cacheSuggestions.Any())
                {
#if DEBUG
                    Console.WriteLine($"[SUGGESTIONS] Found {cacheSuggestions.Count} suggestions");
#endif
                    // Cache and display suggestions
                    lock (_searchLock)
                    {
                        _suggestionCache[searchText.ToLowerInvariant()] = cacheSuggestions;
                        
                        // Clean cache if it gets too large
                        if (_suggestionCache.Count > 100)
                        {
                            var keysToRemove = _suggestionCache.Keys.Take(_suggestionCache.Count - 50).ToList();
                            foreach (var key in keysToRemove)
                            {
                                _suggestionCache.Remove(key);
                            }
                        }
                    }
                    
                    DisplaySuggestions(cacheSuggestions);
                    return;
                }
                
                // Fallback to LayerSearchSource if index is not available
                if (_layerSearchSource == null)
                {
                    await InitializeLayerSearchSourceAsync();
                    
                    if (_layerSearchSource == null)
                    {
                        HideSuggestions();
                        return;
                    }
                }
                
                // Create new cancellation token for this search
                _currentSearchCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // Reduced timeout for faster response
                
                var startTime = DateTime.Now;
                var suggestions = await _layerSearchSource.SuggestAsync(searchText, _currentSearchCts.Token);
                var elapsed = DateTime.Now - startTime;
                
#if DEBUG
                Console.WriteLine($"[PERFORMANCE] Query: {elapsed.TotalMilliseconds:F0}ms, {suggestions.Count} results");
#endif
                
                if (_currentSearchCts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[AUTOCOMPLETE] Search cancelled for: '{searchText}'");
                    return;
                }
                
                if (suggestions.Any())
                {
                    // Extract and clean suggestion texts with improved performance
                    var suggestionTexts = suggestions
                        .Select(s => s.DisplayTitle ?? s.ToString())
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(text => {
                            var index = text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                            return index == -1 ? int.MaxValue : index; // Exact matches first
                        })
                        .ThenBy(text => text.Length) // Shorter matches preferred
                        .Take(8) // Reduced to 8 for better performance
                        .ToList();
                    
                    // Cache the results for future use
                    _suggestionCache[searchText.ToLowerInvariant()] = suggestionTexts;
                    
                    // Limit cache size to prevent memory issues
                    if (_suggestionCache.Count > 50)
                    {
                        var oldestKey = _suggestionCache.Keys.First();
                        _suggestionCache.Remove(oldestKey);
                        Console.WriteLine($"[CACHE] Removed oldest cached entry: '{oldestKey}'");
                    }
                    
                    DisplaySuggestions(suggestionTexts);
#if DEBUG
                    Console.WriteLine($"[PERFORMANCE] Total: {(DateTime.Now - startTime).TotalMilliseconds:F0}ms");
#endif
                }
                else
                {
                    Console.WriteLine("[AUTOCOMPLETE] No suggestions found");
                    HideSuggestions();
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[AUTOCOMPLETE] Search cancelled/timed out for: '{searchText}'");
                // Don't hide suggestions on cancellation - user might still be typing
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTOCOMPLETE] ? ERROR getting suggestions: {ex.Message}");
                HideSuggestions();
            }
        }
        
        // Optimized UI update method
        private void DisplaySuggestions(List<string> suggestionTexts)
        {
            Dispatcher.Invoke(() =>
            {
                if (SuggestionsListBox != null && SuggestionsBorder != null)
                {
                    SuggestionsListBox.ItemsSource = suggestionTexts;
                    SuggestionsBorder.Visibility = Visibility.Visible;
#if DEBUG
                    Console.WriteLine($"[AUTOCOMPLETE] Displaying {suggestionTexts.Count} suggestions");
#endif
                }
            });
        }
        
        // Optimized hide suggestions with cleanup
        private void HideSuggestions()
        {
            // Cancel any pending operations
            _currentSearchCts?.Cancel();
            _debounceTimer?.Stop();
            
            Dispatcher.Invoke(() =>
            {
                if (SuggestionsBorder != null)
                {
                    SuggestionsBorder.Visibility = Visibility.Collapsed;
                    // Clear the source to free memory
                    if (SuggestionsListBox != null)
                    {
                        SuggestionsListBox.ItemsSource = null;
                    }
                }
            });
            
            // Only log when actually hiding (reduce console spam)
            if (SuggestionsBorder?.Visibility == Visibility.Visible)
            {
                Console.WriteLine("[AUTOCOMPLETE] Hiding suggestions");
            }
        }
        
        // Apply the selected suggestion to the textbox
        // NEW: Unified method for executing selected suggestion - fills textbox and performs action
        private async Task ExecuteSelectedSuggestionAsync(string suggestion)
        {
            Console.WriteLine($"[SEARCH] Executing selected suggestion: '{suggestion}'");

            // 1) Always fill the textbox first
            if (UnifiedSearchTextBox != null)
            {
                UnifiedSearchTextBox.Text = suggestion;
                UnifiedSearchTextBox.CaretIndex = suggestion.Length;
                UnifiedSearchTextBox.Focus();
            }

            // 2) Execute the search based on mode
            if (_isAddressMode)
            {
                // Address mode: Use geocoder suggestion index to geocode and zoom
                if (_geoSuggestionIndex.TryGetValue(suggestion, out var suggestResult))
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var success = await GeocodeFromSuggestionAsync(suggestion, cts.Token);

                    if (success)
                    {
                        Console.WriteLine($"[SEARCH] ✅ Address geocoded and zoomed: '{suggestion}'");
                    }
                    else
                    {
                        Console.WriteLine($"[SEARCH] ⚠️ Failed to geocode address: '{suggestion}'");
                    }
                }
                else
                {
                    Console.WriteLine($"[SEARCH] ⚠️ Address suggestion not found in index: '{suggestion}'");
                }
            }
            else
            {
                // Asset mode: Handle structured suggestions or manual input
                await OnAssetSuggestionChosenAsync(suggestion);
            }

            // 3) Hide suggestions after execution
            HideSuggestions();
        }

        // NEW: Execute selected result (when clicking on a search result)
        private async Task ExecuteSelectedResultAsync(SearchResultItem item)
        {
            Console.WriteLine($"[SEARCH] Executing selected result: {item.LayerName}: {item.DisplayText}");

            // Fill textbox with the display text
            if (UnifiedSearchTextBox != null)
            {
                UnifiedSearchTextBox.Text = item.DisplayText;
                UnifiedSearchTextBox.CaretIndex = item.DisplayText.Length;
                UnifiedSearchTextBox.Focus();
            }

            // Zoom to the result
            await ZoomToResultAsync(item);

            // Hide suggestions
            HideSuggestions();
            _showingResults = false;
        }

        // NEW: OnAssetSuggestionChosenAsync as specified in requirements
        private async Task OnAssetSuggestionChosenAsync(string suggestion)
        {
            Console.WriteLine($"[ASSET SUGGESTION] Processing suggestion: '{suggestion}'");
            Console.WriteLine($"[ASSET SUGGESTION] Current asset suggestions count: {_currentAssetSuggestions.Count}");

            // Try to find the structured suggestion that matches this display string
            var selected = _currentAssetSuggestions.FirstOrDefault(s => s.FormattedDisplay.Equals(suggestion, StringComparison.OrdinalIgnoreCase));

            if (selected != null)
            {
                Console.WriteLine($"[ASSET SUGGESTION] ✅ Found structured suggestion: {selected.LayerName}.{selected.FieldName} = '{selected.DisplayText}'");

                // 1) Fill textbox with the clean VALUE only
                if (UnifiedSearchTextBox != null)
                {
                    UnifiedSearchTextBox.Text = selected.DisplayText;
                    UnifiedSearchTextBox.CaretIndex = UnifiedSearchTextBox.Text.Length;
                    UnifiedSearchTextBox.Focus();
                    Console.WriteLine($"[ASSET SUGGESTION] ✅ Filled textbox with: '{selected.DisplayText}'");
                }

                // 2) Execute using structured selection
                await ExecuteAssetSelectionAsync(selected);
            }
            else
            {
                Console.WriteLine("[ASSET SUGGESTION] ⚠️ No structured suggestion found, treating as manual input");

                // Check if it's a formatted suggestion that we need to parse
                var parsed = ParseAssetInput(suggestion);
                var cleanValue = parsed.Value;

                // Manual input - parse and execute
                if (UnifiedSearchTextBox != null)
                {
                    UnifiedSearchTextBox.Text = cleanValue;
                    UnifiedSearchTextBox.CaretIndex = cleanValue.Length;
                    UnifiedSearchTextBox.Focus();
                    Console.WriteLine($"[ASSET SUGGESTION] ✅ Filled textbox with parsed value: '{cleanValue}'");
                }

                // Parse the manual input and execute
                await ExecuteAssetFromTextAsync(suggestion);
            }
        }

        // ExecuteAssetSelectionAsync for structured selections
        private async Task ExecuteAssetSelectionAsync(AssetSearchResult selected)
        {
            Console.WriteLine($"[ASSET SELECTION] Executing structured selection: {selected.LayerName}.{selected.FieldName} = '{selected.DisplayText}'");
            Console.WriteLine($"[ASSET SELECTION] Has Geometry: {selected.Geometry != null}, Has FeatureId: {selected.FeatureId != null}");

            try
            {
                if (selected.Geometry != null)
                {
                    // Use existing geometry for zoom
                    Console.WriteLine("[ASSET SELECTION] Using cached geometry for zoom");
                    await MyMapView.SetViewpointGeometryAsync(selected.Geometry, 40);
                    await HighlightSearchResult(selected);
                    Console.WriteLine("[ASSET SELECTION] ✅ Successfully zoomed and highlighted using cached geometry");
                }
                else if (selected.FeatureId != null)
                {
                    // Fetch geometry if needed
                    Console.WriteLine("[ASSET SELECTION] Fetching geometry for structured selection");
                    var result = await _layerSearchService.FindFirstAsync(
                        selected.LayerName,
                        new List<string> { selected.FieldName },
                        selected.DisplayText);

                    if (result?.Geometry != null)
                    {
                        Console.WriteLine("[ASSET SELECTION] ✅ Successfully fetched geometry, zooming");
                        await MyMapView.SetViewpointGeometryAsync(result.Geometry, 40);
                        await HighlightSearchResult(result);
                        Console.WriteLine("[ASSET SELECTION] ✅ Successfully zoomed and highlighted using fetched geometry");
                    }
                    else
                    {
                        Console.WriteLine("[ASSET SELECTION] ⚠️ Could not fetch geometry for structured selection, falling back to text search");
                        await ExecuteAssetFromTextAsync(selected.DisplayText);
                    }
                }
                else
                {
                    // Fallback to text-based search
                    Console.WriteLine("[ASSET SELECTION] No geometry or FeatureId available, falling back to text-based search");
                    await ExecuteAssetFromTextAsync(selected.DisplayText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASSET SELECTION] ❌ Error: {ex.Message}");
                Console.WriteLine($"[ASSET SELECTION] ❌ Stack trace: {ex.StackTrace}");
                _logger.LogError(ex, "[ASSET SELECTION] Error executing structured selection: {Error}", ex.Message);

                // Final fallback
                Console.WriteLine("[ASSET SELECTION] Attempting final fallback to text search");
                try
                {
                    await ExecuteAssetFromTextAsync(selected.DisplayText);
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"[ASSET SELECTION] ❌ Final fallback also failed: {fallbackEx.Message}");
                }
            }
        }

        // DEPRECATED: Keep for backward compatibility but redirect to new method
        private async Task ApplySelectedSuggestion(string suggestion)
        {
            await ExecuteSelectedSuggestionAsync(suggestion);
        }

        // NEW: Get structured Asset suggestions using LayerSearchService
        private async Task<List<AssetSearchResult>> GetAssetSuggestionsAsync(string searchText, CancellationToken cancellationToken)
        {
            try
            {
                // Check cache first
                string cacheKey = $"asset_{searchText.ToLowerInvariant()}";
                lock (_searchLock)
                {
                    if (_assetSuggestionCache.TryGetValue(cacheKey, out var cached))
                    {
                        Console.WriteLine($"[ASSET CACHE] Using cached suggestions for '{searchText}'");
                        return cached;
                    }
                }

                Console.WriteLine($"[ASSET SEARCH] Getting structured suggestions for '{searchText}'");
                var suggestions = await _layerSearchService.GetStructuredSuggestionsAsync(searchText, 8, cancellationToken);

                // Cache the results
                lock (_searchLock)
                {
                    _assetSuggestionCache[cacheKey] = suggestions;

                    // Limit cache size
                    if (_assetSuggestionCache.Count > 20)
                    {
                        var oldestKey = _assetSuggestionCache.Keys.First();
                        _assetSuggestionCache.Remove(oldestKey);
                    }
                }

                Console.WriteLine($"[ASSET SEARCH] Found {suggestions.Count} structured suggestions");
                return suggestions;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[ASSET SEARCH] Search cancelled for '{searchText}'");
                return new List<AssetSearchResult>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASSET SEARCH] Error: {ex.Message}");
                _logger.LogError(ex, "[ASSET SEARCH] Error getting structured suggestions: {Error}", ex.Message);
                return new List<AssetSearchResult>();
            }
        }

        // NEW: Display structured suggestions (Asset mode)
        private void DisplayStructuredSuggestions(List<AssetSearchResult> suggestions)
        {
            Dispatcher.Invoke(() =>
            {
                if (SuggestionsListBox != null && SuggestionsBorder != null)
                {
                    // Store current structured suggestions for selection handling
                    _currentAssetSuggestions = suggestions;

                    // Display formatted suggestions showing "Layer • Field • Value"
                    var displayItems = suggestions.Select(s => s.FormattedDisplay).ToList();
                    SuggestionsListBox.ItemsSource = displayItems;
                    SuggestionsBorder.Visibility = Visibility.Visible;

                    Console.WriteLine($"[AUTOCOMPLETE] Displaying {suggestions.Count} structured suggestions");
                }
            });
        }

        // ParseAssetInput parser as specified in requirements
        private sealed record ParsedQuery(string? Layer, string? Field, string Value);

        private static ParsedQuery ParseAssetInput(string input)
        {
            var norm = input.Trim();

            // Try "Layer • Field • Value"
            var bullet = norm.Split('•').Select(s => s.Trim()).ToArray();
            if (bullet.Length == 3)
                return new ParsedQuery(bullet[0], bullet[1], bullet[2]);

            // Try "Layer:Value" or "Field:Value"
            var colon = norm.Split(':').Select(s => s.Trim()).ToArray();
            if (colon.Length == 2)
            {
                return new ParsedQuery(colon[0], null, colon[1]);
            }

            // Fallback: just a value
            return new ParsedQuery(null, null, norm);
        }

        // ExecuteAssetFromTextAsync as specified in requirements
        private async Task ExecuteAssetFromTextAsync(string input, CancellationToken cancellationToken = default)
        {
            var parsed = ParseAssetInput(input);
            Console.WriteLine($"[ASSET EXECUTION] Parsed input: Layer='{parsed.Layer}', Field='{parsed.Field}', Value='{parsed.Value}'");

            // Resolve layer/field if provided
            var cfg = _configurationService.Configuration;
            var enabledLayers = cfg?.queryLayers?.Where(q => q.enabled).ToList() ?? new List<QueryLayerConfig>();

            if (!enabledLayers.Any())
            {
                Console.WriteLine("[ASSET EXECUTION] No enabled layers configured");
                return;
            }

            // If parsed.Layer matches a configured layerName → restrict to that layer
            var targetLayers = !string.IsNullOrWhiteSpace(parsed.Layer)
                ? enabledLayers.Where(l => string.Equals(l.layerName, parsed.Layer, StringComparison.OrdinalIgnoreCase)).ToList()
                : enabledLayers;

            // If parsed.Layer didn't match any layer AND colon form was used, treat colon[0] as a FIELD hint
            string? explicitField = parsed.Field ?? (!string.IsNullOrWhiteSpace(parsed.Layer) && !targetLayers.Any() ? parsed.Layer : null);

            Console.WriteLine($"[ASSET EXECUTION] Target layers: {targetLayers.Count}, Explicit field: '{explicitField}'");

            // Search through target layers
            foreach (var layer in targetLayers)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var fields = new List<string>();

                // Use explicit field if provided and exists in layer
                if (!string.IsNullOrWhiteSpace(explicitField) &&
                    layer.searchFields.Contains(explicitField, StringComparer.OrdinalIgnoreCase))
                {
                    fields.Add(explicitField);
                }

                // If no explicit field or field not found, use all searchFields
                if (fields.Count == 0)
                {
                    fields.AddRange(layer.searchFields);
                }

                // Add global idField if not already included
                if (!fields.Contains(cfg.idField, StringComparer.OrdinalIgnoreCase))
                {
                    fields.Add(cfg.idField);
                }

                Console.WriteLine($"[ASSET EXECUTION] Searching layer '{layer.layerName}' in fields: {string.Join(", ", fields)}");

                var result = await _layerSearchService.FindFirstAsync(layer.layerName, fields, parsed.Value, cancellationToken);
                if (result?.Geometry != null)
                {
                    Console.WriteLine($"[ASSET EXECUTION] ✅ Found feature in layer '{layer.layerName}', zooming to geometry");
                    await MyMapView.SetViewpointGeometryAsync(result.Geometry, 40);

                    // Highlight the feature (similar to existing ZoomToResultAsync logic)
                    await HighlightSearchResult(result);
                    return;
                }
            }

            // If nothing found, run a global fallback across all enabled layers/fields
            Console.WriteLine("[ASSET EXECUTION] No specific match found, trying global fallback search");
            var fallback = await _layerSearchService.FindFirstAcrossAsync(enabledLayers, parsed.Value, cancellationToken);
            if (fallback?.Geometry != null)
            {
                Console.WriteLine("[ASSET EXECUTION] ✅ Found feature in global fallback, zooming to geometry");
                await MyMapView.SetViewpointGeometryAsync(fallback.Geometry, 40);
                await HighlightSearchResult(fallback);
            }
            else
            {
                Console.WriteLine($"[ASSET EXECUTION] ⚠️ No asset found for input '{input}'");
                _logger.LogWarning("No asset found for input '{Input}'", input);
            }
        }

        // Helper method to highlight search results
        private async Task HighlightSearchResult(AssetSearchResult result)
        {
            try
            {
                if (MyMapView?.Map == null || result.Geometry == null) return;

                // Clear all selections first
                foreach (var lyr in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
                {
                    lyr.ClearSelection();
                }

                // Find and select the feature in its layer
                var layer = MyMapView.Map.OperationalLayers.OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(result.LayerName, StringComparison.OrdinalIgnoreCase));

                if (layer != null && result.FeatureId != null)
                {
                    // Try to select by FeatureId if available
                    var queryParams = new QueryParameters
                    {
                        WhereClause = $"OBJECTID = {result.FeatureId}",
                        ReturnGeometry = false
                    };

                    var features = await layer.FeatureTable.QueryFeaturesAsync(queryParams);
                    var feature = features.FirstOrDefault();
                    if (feature != null)
                    {
                        layer.SelectFeature(feature);
                        MyMapView.SelectionProperties.Color = System.Drawing.Color.Cyan;
                        Console.WriteLine($"[ASSET EXECUTION] ✅ Highlighted feature {result.FeatureId} in layer {result.LayerName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ASSET EXECUTION] Error highlighting result: {ex.Message}");
                _logger.LogWarning(ex, "[ASSET EXECUTION] Error highlighting search result: {Error}", ex.Message);
            }
        }
        
        #endregion

        // Launch a video window for the first selected/matched asset ID with available videos
        private async Task ShowVideosForMatchedInspectionsAsync()
        {
            try
            {
                Console.WriteLine("[POSM VIDEO DEBUG] === Starting ShowVideosForMatchedInspectionsAsync ===");
                
                var cfg = _configurationService.Configuration;
                if (cfg == null || MyMapView?.Map == null)
                {
                    Console.WriteLine("[POSM VIDEO DEBUG] Configuration or MapView is null - cannot show videos");
                    return;
                }

                var idField = string.IsNullOrWhiteSpace(cfg.idField) ? "AssetID" : cfg.idField;
                Console.WriteLine($"[POSM VIDEO DEBUG] Using ID field: '{idField}'");

                // Find the configured feature layer
                var fl = MyMapView.Map.OperationalLayers
                          .OfType<FeatureLayer>()
                          .FirstOrDefault(l => l.Name.Equals(cfg.selectedLayer, StringComparison.OrdinalIgnoreCase));
                
                if (fl == null)
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] Feature layer '{cfg.selectedLayer}' not found");
                    return;
                }
                Console.WriteLine($"[POSM VIDEO DEBUG] Found feature layer: '{fl.Name}'");

                var selected = await fl.GetSelectedFeaturesAsync();
                if (selected == null)
                {
                    Console.WriteLine("[POSM VIDEO DEBUG] No selected features returned");
                    return;
                }
                
                var selectedCount = selected.Count();
                Console.WriteLine($"[POSM VIDEO DEBUG] Found {selectedCount} selected features");

                // Collect unique, non-empty asset IDs from selected features
                var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in selected)
                {
                    if (f.Attributes.TryGetValue(idField, out var val) && val != null)
                    {
                        var s = val.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            assetIds.Add(s);
                            Console.WriteLine($"[POSM VIDEO DEBUG] Found asset ID: '{s}'");
                        }
                    }
                }

                Console.WriteLine($"[POSM VIDEO DEBUG] Total unique asset IDs: {assetIds.Count}");
                if (assetIds.Count == 0)
                {
                    Console.WriteLine("[POSM VIDEO DEBUG] No valid asset IDs found in selected features");
                    return;
                }

                var db = new PosmDatabaseService(cfg);
                Console.WriteLine("[POSM VIDEO DEBUG] Created database service");

                // Try each asset until we find videos on disk
                foreach (var aid in assetIds)
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] Checking videos for asset: '{aid}'");
                    var videos = await db.GetVideoPathsForAssetAsync(aid);
                    Console.WriteLine($"[POSM VIDEO DEBUG] Found {videos.Count} video records for asset '{aid}'");
                    
                    var existingVideos = videos.Where(v => System.IO.File.Exists(v.FilePath)).ToList();
                    Console.WriteLine($"[POSM VIDEO DEBUG] {existingVideos.Count} videos exist on disk for asset '{aid}'");
                    
                    if (existingVideos.Count > 0)
                    {
                        Console.WriteLine($"[POSM VIDEO DEBUG] Opening video player for asset '{aid}' with {existingVideos.Count} videos");
                        var win = new PosmVideoPlayerWindow(aid, existingVideos) { Owner = this };
                        win.Show();
                        return; // open only one window
                    }
                }

                // If here, no videos found for any matched asset; inform in debug/console
                Console.WriteLine("[POSM VIDEO DEBUG] No POSM videos found for any matched assets");
                System.Diagnostics.Debug.WriteLine("[MainWindow] No POSM videos found for matched assets.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] Error showing matched inspection videos: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Error showing matched inspection videos: {ex.Message}");
            }
        }

        #region ----------- POSM Videos Button -----------
        private void InitializePosmVideoButton()
        {
            try
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] Initializing POSM Video Button");
                Console.WriteLine($"[POSM VIDEO DEBUG] Configuration is null: {_configurationService.Configuration == null}");
                
                if (_configurationService.Configuration != null)
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] Creating PosmVideoPopupButton with config");
                    _posmVideoButton = new PosmVideoPopupButton(_configurationService.Configuration);
                    Console.WriteLine($"[POSM VIDEO DEBUG] PosmVideoPopupButton created successfully");
                }
                else
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] Cannot create POSM Video Button - Configuration is null");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] Error initializing POSM Videos button: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Error initializing POSM Videos button: {ex.Message}");
            }
        }
        #endregion
        
        #region ----------- POSM Database Service -----------
        private void InitializeDatabaseService()
        {
            try
            {
                if (_configurationService.Configuration != null)
                {
                    _databaseService = new PosmDatabaseService(_configurationService.Configuration);
                    Console.WriteLine($"[POSM IMAGE DEBUG] Database service initialized");
                }
                else
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Cannot initialize database service - Configuration is null");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error initializing database service: {ex.Message}");
            }
        }
        #endregion
        
        #region ----------- POSM Inspection Images Overlay -----------
        private void InitializeInspectionImageOverlay()
        {
            try
            {
                if (MyMapView != null && _databaseService != null)
                {
                    _inspectionImageOverlay = new InspectionImageOverlay(MyMapView, _databaseService);
                    Console.WriteLine($"[POSM IMAGE DEBUG] Inspection image overlay initialized");
                }
                else
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Cannot initialize image overlay - MapView or DatabaseService is null");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error initializing inspection image overlay: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles offline map generation completion and automatically switches to the generated offline map
        /// </summary>
        private async void OnOfflineMapCompleted(object? sender, OfflineMapCompletedEventArgs e)
        {
            try
            {
                _logger.LogInformation("[AUTO OFFLINE] Offline map generation completed, switching to offline map: {Path}", e.OfflineMapPath);
                
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        // Load the generated offline map
                        var mobileMapPackage = await MobileMapPackage.OpenAsync(e.OfflineMapPath);
                        if (mobileMapPackage?.Maps?.FirstOrDefault() != null)
                        {
                            var offlineMap = mobileMapPackage.Maps.First();
                            MyMapView.Map = offlineMap;
                            
                            // Update status indicators
                            _isOfflineMap = true;
                            UpdateMapStatusIndicator();
                            
                            _logger.LogInformation("[AUTO OFFLINE] Successfully switched to offline map with {LayerCount} layers", 
                                offlineMap.OperationalLayers.Count);
                                
                            // Show success message to user
                            var message = $"Offline map loaded successfully!\n\nLocation: {e.OfflineMapPath}";
                            if (e.HasLayerErrors)
                            {
                                message += $"\n\nNote: {e.LayerErrorCount} layer(s) had errors but map was generated successfully.";
                            }
                            
                            MessageBox.Show(message, "Offline Map Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            _logger.LogError("[AUTO OFFLINE] Failed to load offline map - no maps found in package");
                            MessageBox.Show("Failed to load the generated offline map.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[AUTO OFFLINE] Error loading offline map: {Error}", ex.Message);
                        MessageBox.Show($"Error loading offline map: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUTO OFFLINE] Error in OnOfflineMapCompleted: {Error}", ex.Message);
            }
        }

        private async Task LoadSelectedOfflineMapAsync(string offlineMapPath)
        {
            try
            {
                _logger.LogInformation("[LOAD EXISTING] Loading existing offline map: {Path}", offlineMapPath);
                
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var mobileMapPackage = await MobileMapPackage.OpenAsync(offlineMapPath);
                        if (mobileMapPackage?.Maps?.FirstOrDefault() != null)
                        {
                            var offlineMap = mobileMapPackage.Maps.First();
                            MyMapView.Map = offlineMap;
                            
                            _isOfflineMap = true;
                            UpdateMapStatusIndicator();
                            
                            _logger.LogInformation("[LOAD EXISTING] Successfully loaded existing offline map with {LayerCount} layers", 
                                offlineMap.OperationalLayers.Count);
                            
                            MessageBox.Show($"Successfully loaded existing offline map with {offlineMap.OperationalLayers.Count} layers.", 
                                "Offline Map Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            _logger.LogError("[LOAD EXISTING] Failed to load the selected offline map - no maps found in package");
                            MessageBox.Show("Failed to load the selected offline map.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[LOAD EXISTING] Error loading existing offline map: {Error}", ex.Message);
                        MessageBox.Show($"Error loading existing offline map: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LOAD EXISTING] Error in LoadSelectedOfflineMapAsync: {Error}", ex.Message);
            }
        }

        #endregion

        #region Suggestion Management Helper Methods

        // Helper method to check if suggestions are available without triggering ItemCollection exceptions
        private bool HasSuggestions()
        {
            try
            {
                if (SuggestionsListBox?.ItemsSource == null)
                    return false;

                if (_isAddressMode)
                {
                    // Address mode uses List<string>
                    return SuggestionsListBox.ItemsSource is IList<string> addressList && addressList.Count > 0;
                }
                else
                {
                    // Asset mode - check structured suggestions cache
                    return _currentAssetSuggestions.Count > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // Helper method to get the currently selected suggestion without ItemCollection issues
        private string GetSelectedSuggestion()
        {
            try
            {
                // First try to get the selected item directly
                if (SuggestionsListBox?.SelectedItem != null)
                {
                    var selected = SuggestionsListBox.SelectedItem.ToString();
                    Console.WriteLine($"[AUTOCOMPLETE] Using selected suggestion: '{selected}'");
                    return selected;
                }

                // If no selection, get from ItemsSource based on mode
                if (_isAddressMode)
                {
                    if (SuggestionsListBox?.ItemsSource is IList<string> addressList && addressList.Count > 0)
                    {
                        SuggestionsListBox.SelectedIndex = 0;
                        var suggestion = addressList[0];
                        Console.WriteLine($"[AUTOCOMPLETE] Auto-selecting first address suggestion: '{suggestion}'");
                        return suggestion;
                    }
                }
                else
                {
                    // Asset mode - use structured suggestions
                    if (_currentAssetSuggestions.Count > 0)
                    {
                        SuggestionsListBox.SelectedIndex = 0;
                        var suggestion = _currentAssetSuggestions[0].FormattedDisplay;
                        Console.WriteLine($"[AUTOCOMPLETE] Auto-selecting first asset suggestion: '{suggestion}'");
                        return suggestion;
                    }
                }

                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTOCOMPLETE] Error getting suggestion: {ex.Message}");

                // Ultimate fallback
                if (!_isAddressMode && _currentAssetSuggestions.Count > 0)
                {
                    var fallback = _currentAssetSuggestions[0].FormattedDisplay;
                    Console.WriteLine($"[AUTOCOMPLETE] Using fallback suggestion: '{fallback}'");
                    return fallback;
                }

                return "";
            }
        }

        // Helper method to navigate suggestions safely
        private void NavigateSuggestions(int direction)
        {
            try
            {
                if (!HasSuggestions())
                    return;

                int currentIndex = SuggestionsListBox.SelectedIndex;
                int maxIndex = _isAddressMode
                    ? (SuggestionsListBox.ItemsSource as IList<string>)?.Count - 1 ?? 0
                    : _currentAssetSuggestions.Count - 1;

                int newIndex;
                if (direction > 0) // Down
                {
                    newIndex = currentIndex + 1;
                    if (newIndex > maxIndex) newIndex = 0;
                }
                else // Up
                {
                    newIndex = currentIndex - 1;
                    if (newIndex < 0) newIndex = maxIndex;
                }

                SuggestionsListBox.SelectedIndex = newIndex;
                if (SuggestionsListBox.SelectedItem != null)
                {
                    SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUTOCOMPLETE] Navigation error: {ex.Message}");
            }
        }

        #endregion
    }
}