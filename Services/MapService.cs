using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Tasks.Geocoding;
using Esri.ArcGISRuntime.UI.Controls;
using Microsoft.Extensions.Logging;

namespace WpfMapApp1.Services
{
    public class MapService : IMapService
    {
        private readonly IConfigurationService _configurationService;
        private readonly INetworkService _networkService;
        private readonly ILogger<MapService> _logger;
        private LocatorTask? _onlineLocator;

        public MapService(
            IConfigurationService configurationService,
            INetworkService networkService,
            ILogger<MapService> logger)
        {
            _configurationService = configurationService;
            _networkService = networkService;
            _logger = logger;
        }

        public async Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(mapId))
            {
                throw new ArgumentException("Map ID cannot be empty", nameof(mapId));
            }

            progress?.Report("Checking map source...");
            
            bool isMmpk = File.Exists(mapId);
            bool online = await _networkService.IsArcGisOnlineReachableAsync();
            Map map;

            if (isMmpk)
            {
                progress?.Report("Loading mobile map package...");
                _logger.LogInformation("Loading MMPK from {Path}", mapId);
                
                var mmpk = await MobileMapPackage.OpenAsync(mapId);
                if (!mmpk.Maps.Any())
                {
                    throw new InvalidOperationException("The mobile map package contains no maps.");
                }
                
                map = mmpk.Maps.First();
                progress?.Report("Mobile map package loaded");

                // If online, set API key for additional services
                if (online)
                {
                    await _networkService.EnsureOnlineApiKeyIfAvailableAsync();
                }
            }
            else
            {
                if (!online)
                {
                    throw new InvalidOperationException("Internet connection required to load a WebMap ID.");
                }

                progress?.Report("Loading web map...");
                _logger.LogInformation("Loading WebMap with ID {MapId}", mapId);

                // Set API key for premium content
                await _networkService.EnsureOnlineApiKeyIfAvailableAsync();

                var portal = await ArcGISPortal.CreateAsync();
                var item = await PortalItem.CreateAsync(portal, mapId);
                map = new Map(item);
                
                progress?.Report("Loading map layers...");
                await map.LoadAsync();
                progress?.Report("Web map loaded");
            }

            // Handle reprojection if needed and online
            if (map.SpatialReference?.Wkid != 102100 && online)
            {
                progress?.Report("Reprojecting map...");
                map = await ReprojectMapAsync(map);
                progress?.Report("Map reprojection complete");
            }

            // Load all operational layers
            progress?.Report("Loading operational layers...");
            await LoadOperationalLayersAsync(map, progress);
            
            return map;
        }

        public async Task InitializeMapViewAsync(MapView mapView, IProgress<string>? progress = null)
        {
            if (mapView?.Map == null)
            {
                throw new ArgumentNullException(nameof(mapView), "MapView or Map cannot be null");
            }

            progress?.Report("Calculating extent...");
            
            var extent = await CalculateCombinedExtentAsync(mapView.Map.OperationalLayers);
            if (extent != null)
            {
                var viewpoint = new Viewpoint(extent);
                mapView.Map.InitialViewpoint = viewpoint;
                
                progress?.Report("Setting viewpoint...");
                await mapView.SetViewpointAsync(viewpoint, TimeSpan.FromSeconds(1));
            }

            // Initialize geocoding if online
            if (await _networkService.IsArcGisOnlineReachableAsync())
            {
                progress?.Report("Initializing geocoding service...");
                await InitOnlineLocatorAsync();
            }

            progress?.Report("Map initialization complete");
        }

        public async Task<bool> TryGeocodeAndZoomAsync(MapView mapView, string text, double scale = 5000)
        {
            if (string.IsNullOrWhiteSpace(text) || mapView == null)
            {
                return false;
            }

            if (_onlineLocator == null)
            {
                if (!await _networkService.EnsureOnlineApiKeyIfAvailableAsync())
                {
                    return false;
                }
                
                await InitOnlineLocatorAsync();
                if (_onlineLocator == null)
                {
                    return false;
                }
            }

            try
            {
                var results = await _onlineLocator.GeocodeAsync(text);
                var first = results.FirstOrDefault();
                
                if (first != null && first.DisplayLocation != null)
                {
                    await mapView.SetViewpointCenterAsync(first.DisplayLocation, scale);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geocoding failed for text: {Text}", text);
            }

            return false;
        }

        private async Task<Map> ReprojectMapAsync(Map sourceMap)
        {
            _logger.LogInformation("Reprojecting map to Web Mercator (102100)");

            await _networkService.EnsureOnlineApiKeyIfAvailableAsync();

            var newMap = new Map(SpatialReference.Create(102100))
            {
                Basemap = new Basemap(new ArcGISTiledLayer(
                    new Uri("https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer")))
            };

            // Move operational layers
            foreach (var layer in sourceMap.OperationalLayers.ToList())
            {
                sourceMap.OperationalLayers.Remove(layer);
                newMap.OperationalLayers.Add(layer);
            }

            await newMap.LoadAsync();
            
            foreach (var layer in newMap.OperationalLayers)
            {
                if (layer.LoadStatus != LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }
            }

            var combined = await CalculateCombinedExtentAsync(newMap.OperationalLayers);
            if (combined != null)
            {
                var projected = GeometryEngine.Project(combined, newMap.SpatialReference!) as Envelope;
                if (projected != null)
                {
                    newMap.InitialViewpoint = new Viewpoint(projected);
                }
            }

            return newMap;
        }

        private async Task LoadOperationalLayersAsync(Map map, IProgress<string>? progress)
        {
            int totalLayers = map.OperationalLayers.Count;
            int loadedLayers = 0;

            foreach (var layer in map.OperationalLayers)
            {
                if (layer.LoadStatus != LoadStatus.Loaded)
                {
                    try
                    {
                        await layer.LoadAsync();
                        loadedLayers++;
                        
                        var percentage = (loadedLayers * 100.0) / totalLayers;
                        progress?.Report($"Loading layer {layer.Name} ({loadedLayers}/{totalLayers})");
                        
                        _logger.LogDebug("Layer '{LayerName}' loaded", layer.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load layer '{LayerName}'", layer.Name);
                    }
                }
            }
        }

        private async Task<Envelope?> CalculateCombinedExtentAsync(IEnumerable<Layer> layers)
        {
            Envelope? combined = null;
            const double WORLD_WIDTH = 25_000_000;

            foreach (var layer in layers)
            {
                if (layer.LoadStatus != LoadStatus.Loaded)
                {
                    try
                    {
                        await layer.LoadAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error loading layer '{LayerName}'", layer.Name);
                        continue;
                    }
                }

                Envelope? env = null;

                if (layer is FeatureLayer fl)
                {
                    try
                    {
                        if (fl.FeatureTable is ServiceFeatureTable sft)
                        {
                            var result = await sft.QueryExtentAsync(new QueryParameters { WhereClause = "1=1" });
                            env = result.Extent;
                        }
                        else
                        {
                            env = fl.FullExtent;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Extent fetch failed for layer '{LayerName}'", fl.Name);
                        env = fl.FullExtent;
                    }
                }
                else
                {
                    env = layer.FullExtent;
                }

                if (env == null || env.Width > WORLD_WIDTH || env.Height > WORLD_WIDTH)
                {
                    continue;
                }

                combined = combined == null
                    ? env
                    : new Envelope(
                        Math.Min(combined.XMin, env.XMin),
                        Math.Min(combined.YMin, env.YMin),
                        Math.Max(combined.XMax, env.XMax),
                        Math.Max(combined.YMax, env.YMax),
                        combined.SpatialReference ?? env.SpatialReference);
            }

            return combined;
        }

        /// <summary>
        /// Determine if the mapId is a file path (MMPK) or WebMap ID (GUID)
        /// </summary>
        public bool IsFilePathSource(string mapId)
        {
            if (string.IsNullOrWhiteSpace(mapId))
            {
                return false;
            }

            // Check if it's a file path (contains path separators and/or file extension)
            return File.Exists(mapId) || 
                   mapId.Contains('\\') || 
                   mapId.Contains('/') ||
                   Path.HasExtension(mapId);
        }

        /// <summary>
        /// Validate that a WebMap ID is a proper GUID format
        /// </summary>
        public bool IsValidWebMapId(string mapId)
        {
            if (string.IsNullOrWhiteSpace(mapId))
            {
                return false;
            }

            // Don't validate as GUID if it looks like a file path
            if (IsFilePathSource(mapId))
            {
                return false;
            }

            // Check if it's a valid GUID (32 hex characters, optionally with hyphens)
            return Guid.TryParse(mapId, out _);
        }

        private async Task InitOnlineLocatorAsync()
        {
            try
            {
                var uri = new Uri("https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer");
                _onlineLocator = await LocatorTask.CreateAsync(uri);
                _logger.LogInformation("Online locator initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize online locator");
                _onlineLocator = null;
            }
        }
    }
}