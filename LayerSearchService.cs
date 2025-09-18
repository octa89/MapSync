using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;
using WpfMapApp1;
using WpfMapApp1.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using System;

namespace POSM_MR3_2
{
    public class LayerSearchService : ILayerSearchService
    {
        private MapView? _mapView;
        private readonly IConfigurationService? _configurationService;
        private readonly ILogger<LayerSearchService>? _logger;
        private static readonly Dictionary<string, CachedSearchResult> _searchCache = new Dictionary<string, CachedSearchResult>();
        private static readonly object _cacheLock = new object();

        public LayerSearchService(IConfigurationService? configurationService = null, ILogger<LayerSearchService>? logger = null)
        {
            _configurationService = configurationService;
            _logger = logger;
        }

        public void Initialize(MapView mapView)
        {
            _mapView = mapView;
        }

        /// <summary>
        /// Resolves layer name to actual FeatureLayer with robust alias fallback for both MMPK and Web Map sources
        /// </summary>
        private FeatureLayer? ResolveLayer(string layerName)
        {
            if (_mapView?.Map == null || string.IsNullOrWhiteSpace(layerName))
                return null;

            var operationalLayers = _mapView.Map.OperationalLayers.OfType<FeatureLayer>().ToList();

            // First try exact name match (case-insensitive)
            var layer = operationalLayers.FirstOrDefault(l =>
                l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

            if (layer != null)
            {
                _logger?.LogDebug("[LAYER RESOLVE] ✅ Exact match: '{LayerName}' -> '{ActualName}' (Source: {MapSource})",
                    layerName, layer.Name, GetMapSourceType());
                return layer;
            }

            // Try alias matching with enhanced patterns for both MMPK and Web Maps
            foreach (var candidate in operationalLayers)
            {
                // Check PortalItem title (common in Web Maps)
                var item = candidate.Item;
                if (item != null && !string.IsNullOrWhiteSpace(item.Title))
                {
                    if (item.Title.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation("[LAYER RESOLVE] ✅ Title match: '{LayerName}' -> '{ActualName}' (via title '{Title}', Source: {MapSource})",
                            layerName, candidate.Name, item.Title, GetMapSourceType());
                        return candidate;
                    }
                }

                // Check FeatureTable name (common in MMPK files)
                if (candidate.FeatureTable?.TableName != null &&
                    candidate.FeatureTable.TableName.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogInformation("[LAYER RESOLVE] ✅ Table name match: '{LayerName}' -> '{ActualName}' (via table '{TableName}', Source: {MapSource})",
                        layerName, candidate.Name, candidate.FeatureTable.TableName, GetMapSourceType());
                    return candidate;
                }

                // Check common alias patterns
                if (IsAliasMatch(layerName, candidate.Name))
                {
                    _logger?.LogInformation("[LAYER RESOLVE] ✅ Pattern match: '{LayerName}' -> '{ActualName}' (Source: {MapSource})",
                        layerName, candidate.Name, GetMapSourceType());
                    return candidate;
                }

                // Additional pattern matching for MMPK-style names
                if (IsMMPKStyleMatch(layerName, candidate.Name))
                {
                    _logger?.LogInformation("[LAYER RESOLVE] ✅ MMPK pattern match: '{LayerName}' -> '{ActualName}' (Source: {MapSource})",
                        layerName, candidate.Name, GetMapSourceType());
                    return candidate;
                }
            }

            _logger?.LogWarning("[LAYER RESOLVE] ⚠️ Layer not found: '{LayerName}' (Source: {MapSource}). Available layers: {AvailableLayers}",
                layerName, GetMapSourceType(), string.Join(", ", operationalLayers.Select(l => $"'{l.Name}'")));
            return null;
        }

        /// <summary>
        /// Checks if layer names match through common alias patterns
        /// </summary>
        private static bool IsAliasMatch(string configName, string actualName)
        {
            // Handle common patterns like "ssGravityMain" <-> "Sewer Gravity Main"
            var configNormalized = NormalizeLayerName(configName);
            var actualNormalized = NormalizeLayerName(actualName);

            return configNormalized.Equals(actualNormalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes layer names for pattern matching
        /// </summary>
        private static string NormalizeLayerName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            // Remove common prefixes and normalize
            var normalized = name
                .Replace("ss", "sewer ", StringComparison.OrdinalIgnoreCase)
                .Replace("_", " ")
                .Replace("-", " ");

            // Remove extra spaces and trim
            return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// Determines the map source type for logging and resolution context
        /// </summary>
        private string GetMapSourceType()
        {
            try
            {
                if (_mapView?.Map?.Item is Esri.ArcGISRuntime.Portal.PortalItem portalItem)
                {
                    // Check type keywords first
                    if (portalItem.TypeKeywords?.Contains("Mobile Map Package") == true)
                    {
                        return "MMPK";
                    }
                    if (portalItem.TypeKeywords?.Contains("Web Map") == true)
                    {
                        return "WebMap";
                    }

                    // Check item type as fallback
                    if (portalItem.Type == Esri.ArcGISRuntime.Portal.PortalItemType.WebMap)
                    {
                        return "WebMap";
                    }
                    if (portalItem.Type == Esri.ArcGISRuntime.Portal.PortalItemType.MobileMapPackage)
                    {
                        return "MMPK";
                    }
                }

                // Check if it's a local MMPK without portal item
                if (_mapView?.Map != null && _mapView.Map.Item == null)
                {
                    return "LocalMMPK";
                }

                return "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Enhanced pattern matching for MMPK-style layer names
        /// </summary>
        private static bool IsMMPKStyleMatch(string configName, string actualName)
        {
            if (string.IsNullOrWhiteSpace(configName) || string.IsNullOrWhiteSpace(actualName))
                return false;

            // MMPK files often have table-style names without spaces
            // Try removing common separators and comparing
            var configNormalized = configName.Replace("_", "").Replace("-", "").Replace(" ", "");
            var actualNormalized = actualName.Replace("_", "").Replace("-", "").Replace(" ", "");

            // Check for partial matches (useful for MMPK table names)
            return configNormalized.Contains(actualNormalized, StringComparison.OrdinalIgnoreCase) ||
                   actualNormalized.Contains(configNormalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves search fields for a layer config with proper fallback logic
        /// </summary>
        private List<string> ResolveSearchFields(QueryLayerConfig config, FeatureLayer layer)
        {
            var fields = new List<string>();
            var globalIdField = (_configurationService?.Configuration?.idField ?? App.Configuration?.idField) ?? "AssetID";

            // Use configured searchFields if available
            if (config.searchFields != null && config.searchFields.Any())
            {
                fields.AddRange(config.searchFields);
                _logger?.LogDebug("[FIELD RESOLVE] Using configured fields for '{LayerName}': {Fields}",
                    config.layerName, string.Join(", ", config.searchFields));
            }
            else
            {
                // Fallback to global idField
                fields.Add(globalIdField);
                _logger?.LogInformation("[FIELD RESOLVE] Search: {LayerName} using fallback idField {IdField}",
                    config.layerName, globalIdField);
            }

            // Validate fields exist in layer schema (case-insensitive)
            var validatedFields = new List<string>();
            var layerFields = layer.FeatureTable?.Fields?.ToDictionary(f => f.Name.ToUpperInvariant(), f => f.Name) ?? new Dictionary<string, string>();

            foreach (var configField in fields)
            {
                var upperField = configField.ToUpperInvariant();
                if (layerFields.TryGetValue(upperField, out var actualFieldName))
                {
                    validatedFields.Add(actualFieldName);
                }
                else
                {
                    _logger?.LogWarning("[FIELD RESOLVE] ⚠️ Field '{Field}' not found in layer '{LayerName}'. Available fields: {AvailableFields}",
                        configField, config.layerName, string.Join(", ", layerFields.Values));
                }
            }

            return validatedFields;
        }

        /// <summary>
        /// Resolves display field with proper fallback hierarchy
        /// </summary>
        private string ResolveDisplayField(QueryLayerConfig config, string matchedField)
        {
            var globalIdField = (_configurationService?.Configuration?.idField ?? App.Configuration?.idField) ?? "AssetID";

            // 1. Use layer's displayField if specified
            if (!string.IsNullOrWhiteSpace(config.displayField))
            {
                return config.displayField;
            }

            // 2. Fall back to global idField
            if (!string.IsNullOrWhiteSpace(globalIdField))
            {
                return globalIdField;
            }

            // 3. Use the matched field as last resort
            return matchedField;
        }

        /// <summary>
        /// Validates configuration and logs resolved layers and field lists at startup
        /// </summary>
        public void ValidateConfigurationAndLogResolutions()
        {
            if (_mapView?.Map == null)
            {
                _logger?.LogWarning("[CONFIG VALIDATION] MapView not initialized, cannot validate configuration");
                return;
            }

            var queryLayers = (_configurationService?.Configuration?.queryLayers ?? App.Configuration?.queryLayers) ?? new List<QueryLayerConfig>();
            var globalIdField = (_configurationService?.Configuration?.idField ?? App.Configuration?.idField) ?? "AssetID";

            _logger?.LogInformation("[CONFIG VALIDATION] Starting configuration validation with global idField: '{IdField}'", globalIdField);

            var enabledConfigs = queryLayers.Where(q => q.enabled).ToList();
            _logger?.LogInformation("[CONFIG VALIDATION] Found {EnabledCount} enabled layers out of {TotalCount} configured layers",
                enabledConfigs.Count, queryLayers.Count);

            foreach (var queryConfig in enabledConfigs)
            {
                // Resolve layer
                var layer = ResolveLayer(queryConfig.layerName);
                if (layer == null)
                {
                    _logger?.LogWarning("[CONFIG VALIDATION] ⚠️ Layer '{LayerName}' could not be resolved", queryConfig.layerName);
                    continue;
                }

                // Resolve search fields
                var searchFields = ResolveSearchFields(queryConfig, layer);
                if (!searchFields.Any())
                {
                    _logger?.LogWarning("[CONFIG VALIDATION] ⚠️ No valid search fields for layer '{LayerName}'", queryConfig.layerName);
                    continue;
                }

                // Check for fallback scenarios
                if (queryConfig.searchFields == null || !queryConfig.searchFields.Any())
                {
                    if (string.IsNullOrWhiteSpace(queryConfig.displayField))
                    {
                        _logger?.LogInformation("[CONFIG VALIDATION] Search: {LayerName} using fallback idField {IdField}",
                            queryConfig.layerName, globalIdField);
                    }
                }

                // Log resolved configuration
                var displayField = ResolveDisplayField(queryConfig, searchFields.First());
                _logger?.LogInformation("[CONFIG VALIDATION] ✅ Layer '{LayerName}' -> '{ActualName}': " +
                    "searchFields=[{SearchFields}], displayField='{DisplayField}'",
                    queryConfig.layerName, layer.Name, string.Join(", ", searchFields), displayField);
            }

            _logger?.LogInformation("[CONFIG VALIDATION] Configuration validation complete");
        }

        // Enhanced method that supports configurable query layers with caching and parallel execution
        public async Task<List<SearchResultItem>> SearchLayersAsync(string searchText, CancellationToken cancellationToken = default)
        {
            if (_mapView?.Map == null)
            {
                _logger?.LogWarning("[LAYER SEARCH] MapView not initialized");
                return new List<SearchResultItem>();
            }

            // Check cache first
            var cacheKey = $"config_{searchText.ToLowerInvariant()}";
            lock (_cacheLock)
            {
                if (_searchCache.TryGetValue(cacheKey, out var cached) && 
                    cached.Timestamp > DateTime.Now.AddMinutes(-5)) // 5 minute cache
                {
                    return cached.Results;
                }
            }

            var results = new List<SearchResultItem>();
            var queryLayers = (_configurationService?.Configuration?.queryLayers ?? App.Configuration?.queryLayers) ?? new List<QueryLayerConfig>();
            
            if (!queryLayers.Any())
            {
                // Fallback to current behavior with selected layer
                var idField = (_configurationService?.Configuration?.idField ?? App.Configuration?.idField) ?? "AssetID";
                return await SearchLayersAsync(searchText, idField);
            }

            var enabledConfigs = queryLayers.Where(q => q.enabled).ToList();
            
            // Process layers in parallel for better performance
            var layerTasks = new List<Task<List<SearchResultItem>>>();
            
            foreach (var queryConfig in enabledConfigs)
            {
                // Use robust layer resolution
                var layer = ResolveLayer(queryConfig.layerName);

                if (layer == null)
                {
                    continue; // Layer resolution already logged the warning
                }

                // Make sure the layer is loaded
                if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }

                // Resolve search fields with proper fallback logic
                var searchFields = ResolveSearchFields(queryConfig, layer);
                if (!searchFields.Any())
                {
                    _logger?.LogWarning("[LAYER SEARCH] ⚠️ No valid search fields for layer '{LayerName}', skipping", queryConfig.layerName);
                    continue;
                }

                // Create a task for this layer to enable parallel processing
                var layerTask = ProcessLayerAsync(layer, queryConfig, searchFields, searchText);
                layerTasks.Add(layerTask);
            }
            
            // Wait for all layer searches to complete
            if (layerTasks.Any())
            {
                var allResults = await Task.WhenAll(layerTasks);
                foreach (var layerResults in allResults)
                {
                    results.AddRange(layerResults);
                }
            }

            // Cache the results
            lock (_cacheLock)
            {
                _searchCache[cacheKey] = new CachedSearchResult { Results = results, Timestamp = DateTime.Now };
                
                // Clean old cache entries
                var oldKeys = _searchCache.Where(kvp => kvp.Value.Timestamp < DateTime.Now.AddMinutes(-10))
                    .Select(kvp => kvp.Key).ToList();
                foreach (var key in oldKeys)
                {
                    _searchCache.Remove(key);
                }
            }

            return results;
        }

        private async Task<List<SearchResultItem>> ProcessLayerAsync(FeatureLayer layer, QueryLayerConfig queryConfig, List<string> searchFields, string searchText)
        {
            var layerResults = new List<SearchResultItem>();

            foreach (var fieldName in searchFields)
            {
                // Build the WHERE clause for partial matching with proper SQL escaping
                var escapedSearchText = searchText.Replace("'", "''");
                var whereClause = $"UPPER({fieldName}) LIKE UPPER('%{escapedSearchText}%')";

                var queryParams = new QueryParameters
                {
                    WhereClause = whereClause,
                    MaxFeatures = 20,  // Limit results per field
                    ReturnGeometry = true
                };

                try
                {
                    var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams);

                    foreach (var feature in queryResult)
                    {
                        // Use proper display field resolution hierarchy
                        var displayFieldName = ResolveDisplayField(queryConfig, fieldName);
                        var displayValue = feature.Attributes.ContainsKey(displayFieldName)
                            ? feature.Attributes[displayFieldName]?.ToString()
                            : feature.Attributes[fieldName]?.ToString(); // Final fallback to matched field

                        layerResults.Add(new SearchResultItem
                        {
                            LayerName = layer.Name,
                            Feature = feature,
                            DisplayText = displayValue ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"Error searching field {fieldName} in layer {layer.Name}: {ex.Message}");
#endif
                }
            }
            
            return layerResults;
        }

        // Legacy method for backward compatibility
        public async Task<List<SearchResultItem>> SearchLayersAsync(string searchText, string attributeName)
        {
            if (_mapView?.Map == null)
            {
                _logger?.LogWarning("[LAYER SEARCH] MapView not initialized");
                return new List<SearchResultItem>();
            }

            // Check cache first
            var cacheKey = $"legacy_{searchText.ToLowerInvariant()}_{attributeName}";
            lock (_cacheLock)
            {
                if (_searchCache.TryGetValue(cacheKey, out var cached) && 
                    cached.Timestamp > DateTime.Now.AddMinutes(-5))
                {
                    return cached.Results;
                }
            }

            var results = new List<SearchResultItem>();
            string selectedLayerName = (_configurationService?.Configuration?.selectedLayer ?? App.Configuration?.selectedLayer) ?? "";

            foreach (var layer in _mapView.Map.OperationalLayers.OfType<FeatureLayer>())
            {
                // If you only want to search the "selectedLayer", skip other layers
                if (!string.IsNullOrEmpty(selectedLayerName) &&
                    !layer.Name.Equals(selectedLayerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Searching layer: {layer.Name} for field: {attributeName} with text: {searchText}");
#endif

                // Make sure the layer is loaded
                if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }

                // Build the WHERE clause for partial matching
                var queryParams = new QueryParameters
                {
                    WhereClause = $"UPPER({attributeName}) LIKE UPPER('%{searchText}%')",
                    MaxFeatures = 50,
                    ReturnGeometry = true
                };

                var featureTable = layer.FeatureTable;
                var queryResult = await featureTable.QueryFeaturesAsync(queryParams);

                int count = 0;
                foreach (var feature in queryResult)
                {
                    results.Add(new SearchResultItem
                    {
                        LayerName = layer.Name,
                        Feature = feature,
                        DisplayText = feature.Attributes[attributeName]?.ToString() ?? ""
                    });
                    count++;
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Found {count} features in layer {layer.Name}");
#endif
            }

            // Cache the results
            lock (_cacheLock)
            {
                _searchCache[cacheKey] = new CachedSearchResult { Results = results, Timestamp = DateTime.Now };
            }

            return results;
        }

        /// <summary>
        /// Get autocomplete suggestions from live layer queries
        /// </summary>
        public async Task<List<string>> GetSuggestionsAsync(string searchText, int maxSuggestions = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchText) || _mapView?.Map == null)
            {
                return new List<string>();
            }

            try
            {
                _logger?.LogDebug("[LAYER SEARCH] Getting suggestions for '{SearchText}'", searchText);

                var results = await SearchLayersAsync(searchText, cancellationToken);
                var suggestions = results
                    .Take(maxSuggestions)
                    .Select(r => $"{r.LayerName}: {r.DisplayText}")
                    .Distinct()
                    .ToList();

                _logger?.LogDebug("[LAYER SEARCH] Generated {Count} suggestions for '{SearchText}'", suggestions.Count, searchText);
                return suggestions;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LAYER SEARCH] Error getting suggestions: {Error}", ex.Message);
                return new List<string>();
            }
        }

        /// <summary>
        /// Search a specific layer with specified fields
        /// </summary>
        public async Task<List<SearchResultItem>> SearchSpecificLayerAsync(string layerName, List<string> searchFields, string searchText, int maxResults = 200, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchText) || _mapView?.Map == null)
            {
                _logger?.LogWarning("[LAYER SEARCH] Cannot search - MapView not initialized or invalid input");
                return new List<SearchResultItem>();
            }

            try
            {
                _logger?.LogInformation("[LAYER SEARCH] Searching specific layer '{LayerName}' for '{SearchText}'", layerName, searchText);

                var layer = _mapView.Map.OperationalLayers.OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

                if (layer == null)
                {
                    _logger?.LogWarning("[LAYER SEARCH] Layer '{LayerName}' not found", layerName);
                    return new List<SearchResultItem>();
                }

                // Ensure layer is loaded
                if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }

                var queryConfig = new QueryLayerConfig
                {
                    layerName = layerName,
                    searchFields = searchFields,
                    displayField = searchFields.FirstOrDefault() ?? "OBJECTID",
                    enabled = true
                };

                var results = await ProcessLayerAsync(layer, queryConfig, searchFields, searchText, maxResults);
                
                _logger?.LogInformation("[LAYER SEARCH] Found {Count} results in layer '{LayerName}'", results.Count, layerName);
                return results;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LAYER SEARCH] Error searching specific layer '{LayerName}': {Error}", layerName, ex.Message);
                return new List<SearchResultItem>();
            }
        }

        /// <summary>
        /// Validate if configured layers exist in the current map
        /// </summary>
        public async Task<List<string>> ValidateConfiguredLayersAsync()
        {
            var issues = new List<string>();

            try
            {
                if (_mapView?.Map == null)
                {
                    issues.Add("MapView or Map is not initialized");
                    return issues;
                }

                var queryLayers = (_configurationService?.Configuration?.queryLayers ?? App.Configuration?.queryLayers) ?? new List<QueryLayerConfig>();
                var enabledLayers = queryLayers.Where(q => q.enabled).ToList();

                _logger?.LogInformation("[LAYER SEARCH] Validating {Count} configured layers", enabledLayers.Count);

                var mapLayerNames = _mapView.Map.OperationalLayers.OfType<FeatureLayer>()
                    .Select(l => l.Name)
                    .ToList();

                foreach (var queryConfig in enabledLayers)
                {
                    if (!mapLayerNames.Any(name => name.Equals(queryConfig.layerName, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add($"Configured layer '{queryConfig.layerName}' not found in map");
                        continue;
                    }

                    var layer = _mapView.Map.OperationalLayers.OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(queryConfig.layerName, StringComparison.OrdinalIgnoreCase));

                    if (layer == null) continue;

                    // Ensure layer is loaded to check fields
                    if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                    {
                        await layer.LoadAsync();
                    }

                    // Check if configured search fields exist
                    var layerFields = layer.FeatureTable?.Fields?.Select(f => f.Name).ToList() ?? new List<string>();
                    var missingFields = queryConfig.searchFields?.Where(f => !layerFields.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList() ?? new List<string>();

                    if (missingFields.Any())
                    {
                        issues.Add($"Layer '{queryConfig.layerName}' missing fields: {string.Join(", ", missingFields)}");
                    }

                    // Check display field
                    if (!string.IsNullOrWhiteSpace(queryConfig.displayField) && !layerFields.Contains(queryConfig.displayField, StringComparer.OrdinalIgnoreCase))
                    {
                        issues.Add($"Layer '{queryConfig.layerName}' missing display field: {queryConfig.displayField}");
                    }
                }

                if (issues.Any())
                {
                    _logger?.LogWarning("[LAYER SEARCH] Validation found {Count} issues", issues.Count);
                }
                else
                {
                    _logger?.LogInformation("[LAYER SEARCH] All configured layers validated successfully");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[LAYER SEARCH] Error validating configured layers: {Error}", ex.Message);
                issues.Add($"Validation error: {ex.Message}");
            }

            return issues;
        }

        private async Task<List<SearchResultItem>> ProcessLayerAsync(FeatureLayer layer, QueryLayerConfig queryConfig, List<string> searchFields, string searchText, int maxResults = 20)
        {
            var layerResults = new List<SearchResultItem>();
            
            foreach (var fieldName in searchFields)
            {
                // Build the WHERE clause for partial matching with proper SQL escaping
                var escapedSearchText = searchText.Replace("'", "''"); // Basic SQL escaping
                var whereClause = $"UPPER({fieldName}) LIKE UPPER('%{escapedSearchText}%')";
                
                var queryParams = new QueryParameters
                {
                    WhereClause = whereClause,
                    MaxFeatures = maxResults / searchFields.Count, // Distribute results across fields
                    ReturnGeometry = true
                };

                try
                {
                    var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams);
                    
                    foreach (var feature in queryResult)
                    {
                        var displayValue = !string.IsNullOrWhiteSpace(queryConfig.displayField) && feature.Attributes.ContainsKey(queryConfig.displayField)
                            ? feature.Attributes[queryConfig.displayField]?.ToString()
                            : feature.Attributes[fieldName]?.ToString();

                        layerResults.Add(new SearchResultItem
                        {
                            LayerName = layer.Name,
                            Feature = feature,
                            DisplayText = displayValue ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[LAYER SEARCH] Error searching field {FieldName} in layer {LayerName}: {Error}", fieldName, layer.Name, ex.Message);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"Error searching field {fieldName} in layer {layer.Name}: {ex.Message}");
#endif
                }
            }
            
            return layerResults;
        }

        private class CachedSearchResult
        {
            public List<SearchResultItem> Results { get; set; } = new List<SearchResultItem>();
            public DateTime Timestamp { get; set; }
        }

        // NEW: SearchResult-based methods for structured Asset mode suggestions
        public async Task<List<AssetSearchResult>> GetStructuredSuggestionsAsync(string searchText, int maxSuggestions = 8, CancellationToken cancellationToken = default)
        {
            if (_mapView?.Map == null || string.IsNullOrWhiteSpace(searchText))
            {
                return new List<AssetSearchResult>();
            }

            var results = new List<AssetSearchResult>();
            var queryLayers = (_configurationService?.Configuration?.queryLayers ?? App.Configuration?.queryLayers) ?? new List<QueryLayerConfig>();
            var enabledConfigs = queryLayers.Where(q => q.enabled).ToList();

            foreach (var queryConfig in enabledConfigs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Use robust layer resolution
                var layer = ResolveLayer(queryConfig.layerName);
                if (layer?.FeatureTable == null) continue;

                // Make sure the layer is loaded
                if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }

                // Resolve search fields with proper fallback logic
                var searchFields = ResolveSearchFields(queryConfig, layer);
                if (!searchFields.Any()) continue;

                // Build search query for all resolved fields
                var escapedSearchText = searchText.Replace("'", "''");
                var conditions = searchFields
                    .Select(field => $"UPPER({field}) LIKE UPPER('%{escapedSearchText}%')")
                    .ToList();

                var whereClause = $"({string.Join(" OR ", conditions)})";

                try
                {
                    var queryParams = new QueryParameters
                    {
                        WhereClause = whereClause,
                        MaxFeatures = maxSuggestions,
                        ReturnGeometry = true
                    };

                    var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams, cancellationToken);

                    foreach (var feature in queryResult.Take(maxSuggestions))
                    {
                        // Find the field that matched
                        string matchedField = "";
                        string displayValue = "";

                        foreach (var field in searchFields)
                        {
                            if (feature.Attributes.TryGetValue(field, out var value) && value != null)
                            {
                                var strValue = value.ToString() ?? "";
                                if (strValue.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedField = field;
                                    // Use proper display field resolution for final display value
                                    var displayFieldName = ResolveDisplayField(queryConfig, field);
                                    displayValue = feature.Attributes.ContainsKey(displayFieldName)
                                        ? feature.Attributes[displayFieldName]?.ToString() ?? ""
                                        : strValue;
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(displayValue))
                        {
                            results.Add(new AssetSearchResult
                            {
                                DisplayText = displayValue,
                                LayerName = queryConfig.layerName,
                                FieldName = matchedField,
                                FeatureId = feature.Attributes.ContainsKey("OBJECTID") ? feature.Attributes["OBJECTID"] : null,
                                Geometry = feature.Geometry,
                                Attributes = feature.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[LAYER SEARCH] Error getting structured suggestions from layer {LayerName}", queryConfig.layerName);
                }
            }

            return results.Take(maxSuggestions).ToList();
        }

        public async Task<AssetSearchResult?> FindFirstAsync(string layerName, List<string> searchFields, string searchValue, CancellationToken cancellationToken = default)
        {
            if (_mapView?.Map == null || string.IsNullOrWhiteSpace(searchValue))
            {
                return null;
            }

            var layer = ResolveLayer(layerName);

            if (layer?.FeatureTable == null) return null;

            var escapedSearchValue = searchValue.Replace("'", "''"); // Basic SQL escaping
            var conditions = searchFields
                .Select(field => $"UPPER({field}) LIKE UPPER('%{escapedSearchValue}%')")
                .ToList();

            var whereClause = $"({string.Join(" OR ", conditions)})";

            try
            {
                var queryParams = new QueryParameters
                {
                    WhereClause = whereClause,
                    MaxFeatures = 1,
                    ReturnGeometry = true
                };

                var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams, cancellationToken);
                var feature = queryResult.FirstOrDefault();

                if (feature != null)
                {
                    // Find which field matched
                    string matchedField = "";
                    foreach (var field in searchFields)
                    {
                        if (feature.Attributes.TryGetValue(field, out var value) && value != null)
                        {
                            var strValue = value.ToString() ?? "";
                            if (strValue.Contains(searchValue, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedField = field;
                                break;
                            }
                        }
                    }

                    return new AssetSearchResult
                    {
                        DisplayText = searchValue,
                        LayerName = layerName,
                        FieldName = matchedField,
                        FeatureId = feature.Attributes.ContainsKey("OBJECTID") ? feature.Attributes["OBJECTID"] : null,
                        Geometry = feature.Geometry,
                        Attributes = feature.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value)
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[LAYER SEARCH] Error finding feature in layer {LayerName}", layerName);
            }

            return null;
        }

        public async Task<AssetSearchResult?> FindFirstAcrossAsync(List<QueryLayerConfig> enabledLayers, string searchValue, CancellationToken cancellationToken = default)
        {
            foreach (var layerConfig in enabledLayers)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var result = await FindFirstAsync(layerConfig.layerName, layerConfig.searchFields, searchValue, cancellationToken);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }

    public class SearchResultItem
    {
        public string LayerName { get; set; } = string.Empty;
        public Feature Feature { get; set; } = default!;
        public string DisplayText { get; set; } = string.Empty;
    }

    // Enhanced AssetSearchResult model for structured Asset mode suggestions
    public sealed class AssetSearchResult
    {
        public string DisplayText { get; set; } = "";           // e.g., "12345" (clean value)
        public string LayerName { get; set; } = "";             // e.g., "ssGravityMain"
        public string FieldName { get; set; } = "";             // e.g., "AssetID"
        public object? FeatureId { get; set; }                  // Primary key/ObjectID
        public Esri.ArcGISRuntime.Geometry.Geometry? Geometry { get; set; }       // Feature geometry for zooming
        public IReadOnlyDictionary<string, object?> Attributes { get; set; } = new Dictionary<string, object?>();

        // UI display formatted as "Layer • Field • Value"
        public string FormattedDisplay => $"{LayerName} • {FieldName} • {DisplayText}";

        // Short display for suggestions (just the value)
        public string SuggestionDisplay => DisplayText;
    }
}