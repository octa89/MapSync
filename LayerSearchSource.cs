using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Toolkit.UI.Controls;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WpfMapApp1;

namespace POSM_MR3_2
{
    public class LayerSearchSource : ISearchSource
    {
        // Required properties from ISearchSource
        public string DisplayName { get; set; } = "Layer Search";
        public bool IsVisible { get; set; } = true;
        public double DefaultZoomScale { get; set; } = double.NaN;
        public CalloutDefinition? DefaultCalloutDefinition { get; set; } = null;
        public Symbol? DefaultSymbol { get; set; } = null;
        public int MaximumResults { get; set; } = 10;
        public int MaximumSuggestions { get; set; } = 5;
        public Geometry? SearchArea { get; set; } = null;
        public MapPoint? PreferredSearchLocation { get; set; } = null;
        public string? Placeholder { get; set; } = "Search here...";

        private readonly MapView _mapView;
        private readonly string _attributeName;
        private static InMemorySearchIndex? _sharedIndex;

        public LayerSearchSource(MapView mapView) : this(mapView, App.Configuration.idField)
        {
        }

        public LayerSearchSource(MapView mapView, string attributeName)
        {
            _mapView = mapView;
            _attributeName = attributeName;
        }
        
        // Set the shared index for ultra-fast searching
        public static void SetSharedIndex(InMemorySearchIndex index)
        {
            _sharedIndex = index;
        }

        // SuggestAsync returns suggestions based on enabled query layers
        public async Task<IList<SearchSuggestion>> SuggestAsync(string searchText, CancellationToken cancellationToken)
        {
#if DEBUG
            Console.WriteLine($"[LAYER SEARCH] SuggestAsync: '{searchText}'");
#endif
            var suggestions = new List<SearchSuggestion>();
            
            if (string.IsNullOrWhiteSpace(searchText) || searchText.Length < 2)
            {
                return suggestions;
            }

            try
            {
                // First try the ultra-fast in-memory index
                if (_sharedIndex != null)
                {
                    var indexSuggestions = _sharedIndex.GetSuggestions(searchText, MaximumSuggestions);
                    if (indexSuggestions.Any())
                    {
#if DEBUG
                        Console.WriteLine($"[LAYER SEARCH] Using {indexSuggestions.Count} instant results from index");
#endif
                        return indexSuggestions.Select(s => new SearchSuggestion(s, this)).ToList();
                    }
                }
                
                // Fallback to database queries if index is not available
                // Get enabled query layers from configuration
                var queryLayers = App.Configuration?.queryLayers?.Where(q => q.enabled) ?? Enumerable.Empty<QueryLayerConfig>();
                var uniqueValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                // Process layers in parallel for better performance
                var layerTasks = new List<Task<List<SearchSuggestion>>>();
                
                foreach (var queryConfig in queryLayers)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    
                    var layer = _mapView.Map?.OperationalLayers?.OfType<FeatureLayer>()
                        ?.FirstOrDefault(l => l.Name.Equals(queryConfig.layerName, StringComparison.OrdinalIgnoreCase));
                        
                    if (layer?.FeatureTable == null)
                    {
                        continue;
                    }

                    var searchFields = queryConfig.searchFields ?? new List<string> { _attributeName };
                    
                    foreach (var fieldName in searchFields)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        try
                        {
                            // Query for values that contain the search text
                            var whereClause = $"UPPER({fieldName}) LIKE UPPER('%{searchText}%')";
                            
                            var queryParams = new QueryParameters
                            {
                                WhereClause = whereClause,
                                MaxFeatures = 5, // Increased back to 5 for better results
                                ReturnGeometry = false // Don't return geometry for suggestions to improve performance
                            };

                            var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams);
                            
                            foreach (var feature in queryResult)
                            {
                                var value = feature.Attributes[fieldName]?.ToString();
                                
                                if (!string.IsNullOrWhiteSpace(value) && 
                                    value.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
                                    uniqueValues.Add(value))
                                {
                                    var suggestion = new SearchSuggestion($"{queryConfig.layerName}: {value}", this);
                                    // Stream into replica cache if available
                                    _sharedIndex?.Upsert(queryConfig.layerName, fieldName, value,
                                        !string.IsNullOrWhiteSpace(queryConfig.displayField) && feature.Attributes.ContainsKey(queryConfig.displayField)
                                            ? feature.Attributes[queryConfig.displayField]?.ToString() ?? value
                                            : value,
                                        feature,
                                        feature.Attributes.ContainsKey("OBJECTID") ? feature.Attributes["OBJECTID"] : feature.GetHashCode());
                                    suggestions.Add(suggestion);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
#if DEBUG
                            System.Diagnostics.Debug.WriteLine($"Error getting suggestions for {fieldName}: {ex.Message}");
#endif
                        }
                    }
                }

                // Return top 10 suggestions sorted by relevance
                var finalSuggestions = suggestions
                    .OrderBy(s => {
                        var index = s.DisplayTitle.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                        return index == -1 ? int.MaxValue : index; // Prioritize exact matches
                    })
                    .ThenBy(s => s.DisplayTitle.Length)
                    .Take(10)
                    .ToList();
                
#if DEBUG
                Console.WriteLine($"[LAYER SEARCH] Returning {finalSuggestions.Count} suggestions");
#endif
                
                return finalSuggestions;
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Error in SuggestAsync: {ex.Message}");
#endif
                return suggestions;
            }
        }

        // SearchAsync (by string): queries the layers and returns results.
        public async Task<IList<SearchResult>> SearchAsync(string searchText, CancellationToken cancellationToken)
        {
            var results = new List<SearchResult>();
            var service = new LayerSearchService();
            service.Initialize(_mapView);
            
            // Use the new configurable search method first
            var layerResults = await service.SearchLayersAsync(searchText);
            
            // If no results from configurable search, fall back to attribute-based search
            if (!layerResults.Any())
            {
                layerResults = await service.SearchLayersAsync(searchText, _attributeName);
            }

            foreach (var item in layerResults)
            {
                string title = !string.IsNullOrWhiteSpace(item.DisplayText) 
                    ? $"{item.LayerName}: {item.DisplayText}"
                    : $"{item.LayerName}: {item.Feature.Attributes[_attributeName]}";
                    
                Envelope? extent = item.Feature.Geometry?.Extent;
                Viewpoint? viewpoint = extent != null ? new Viewpoint(extent) : null;

                // Use the constructor with a subtitle (set to null) as required:
                // SearchResult(string title, string? subtitle, ISearchSource source, GeoElement? geoElement, Viewpoint? viewpoint)
                var sr = new SearchResult(title, null, this, item.Feature, viewpoint);
                results.Add(sr);
            }
            
            return results;
        }

        // SearchAsync (by suggestion): uses suggestion.DisplayText for the search text.
        public Task<IList<SearchResult>> SearchAsync(SearchSuggestion suggestion, CancellationToken cancellationToken)
        {
            // Use DisplayTitle property for suggestion text.
            var text = suggestion.DisplayTitle;
            return SearchAsync(text, cancellationToken);
        }

        // RepeatSearchAsync: re-calls SearchAsync (area filtering not implemented)
        public Task<IList<SearchResult>> RepeatSearchAsync(string searchText, Envelope area, CancellationToken cancellationToken)
        {
            return SearchAsync(searchText, cancellationToken);
        }

        // Optional methods to notify the source when a result is selected or deselected.
        public void NotifySelected(SearchResult searchResult)
        {
            // Optionally highlight the feature or display a callout.
        }

        public void NotifyDeselected(SearchResult? searchResult)
        {
            // Optionally clear any highlighting.
        }
    }
}
