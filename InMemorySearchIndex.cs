using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;
using WpfMapApp1;

namespace POSM_MR3_2
{
    /// <summary>
    /// High-performance in-memory search index for instant asset searching
    /// </summary>
    public class InMemorySearchIndex
    {
        private readonly Dictionary<string, List<IndexEntry>> _index = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndexEntry> _exactMatchIndex = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _indexLock = new object();
        private bool _isInitialized = false;
        private DateTime _lastIndexTime = DateTime.MinValue;

        public class IndexEntry
        {
            public string LayerName { get; set; } = string.Empty;
            public string FieldName { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string DisplayValue { get; set; } = string.Empty;
            public Feature? Feature { get; set; }
            public object? FeatureId { get; set; }
        }

        // Allow streaming new entries from live queries into the cache
        public void Upsert(string layerName, string fieldName, string value, string displayValue, Feature? feature, object? featureId)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var entry = new IndexEntry
            {
                LayerName = layerName,
                FieldName = fieldName,
                Value = value,
                DisplayValue = string.IsNullOrWhiteSpace(displayValue) ? value : displayValue,
                Feature = feature,
                FeatureId = featureId ?? ((feature?.Attributes != null && feature.Attributes.ContainsKey("OBJECTID"))
                    ? feature.Attributes["OBJECTID"]
                    : feature?.GetHashCode())
            };

            lock (_indexLock)
            {
                _exactMatchIndex[value] = entry;
                AddToSubstringIndex(value.ToLowerInvariant(), entry);
            }
        }

        /// <summary>
        /// Build the search index from all configured layers
        /// </summary>
        public async Task BuildIndexAsync(MapView mapView, IProgress<string>? progress = null)
        {
            if (mapView?.Map == null)
            {
                progress?.Report("Map not ready for indexing");
                return;
            }

            progress?.Report("Starting search index build...");
            
            var queryLayers = App.Configuration?.queryLayers?.Where(q => q.enabled) ?? Enumerable.Empty<QueryLayerConfig>();
            var totalLayers = queryLayers.Count();
            var currentLayer = 0;

            // Clear existing index
            lock (_indexLock)
            {
                _index.Clear();
                _exactMatchIndex.Clear();
            }

            // Process each configured layer
            foreach (var queryConfig in queryLayers)
            {
                currentLayer++;
                progress?.Report($"Indexing layer {currentLayer}/{totalLayers}: {queryConfig.layerName}");

                var layer = mapView.Map.OperationalLayers.OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(queryConfig.layerName, StringComparison.OrdinalIgnoreCase));

                if (layer?.FeatureTable == null)
                    continue;

                // Ensure layer is loaded
                if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                {
                    await layer.LoadAsync();
                }

                var searchFields = queryConfig.searchFields ?? new List<string> { App.Configuration?.idField ?? "AssetID" };
                
                // Query all features for indexing (no WHERE clause)
                var queryParams = new QueryParameters
                {
                    WhereClause = "1=1", // Get all features
                    ReturnGeometry = false, // Don't need geometry for index
                    MaxFeatures = 10000 // Reasonable limit to prevent memory issues
                };

                try
                {
                    var queryResult = await layer.FeatureTable.QueryFeaturesAsync(queryParams);
                    
                    foreach (var feature in queryResult)
                    {
                        foreach (var fieldName in searchFields)
                        {
                            if (!feature.Attributes.ContainsKey(fieldName))
                                continue;

                            var value = feature.Attributes[fieldName]?.ToString();
                            if (string.IsNullOrWhiteSpace(value))
                                continue;

                            var displayValue = !string.IsNullOrWhiteSpace(queryConfig.displayField) && 
                                             feature.Attributes.ContainsKey(queryConfig.displayField)
                                ? feature.Attributes[queryConfig.displayField]?.ToString() ?? value
                                : value;

                            var entry = new IndexEntry
                            {
                                LayerName = layer.Name,
                                FieldName = fieldName,
                                Value = value,
                                DisplayValue = displayValue,
                                Feature = feature,
                                FeatureId = feature.Attributes.ContainsKey("OBJECTID") 
                                    ? feature.Attributes["OBJECTID"] 
                                    : feature.GetHashCode()
                            };

                            // Add to exact match index
                            lock (_indexLock)
                            {
                                _exactMatchIndex[value] = entry;

                                // Add to substring index (create n-grams for fast substring matching)
                                AddToSubstringIndex(value.ToLowerInvariant(), entry);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Error indexing layer {queryConfig.layerName}: {ex.Message}");
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"Error indexing layer {queryConfig.layerName}: {ex.Message}");
#endif
                }
            }

            lock (_indexLock)
            {
                _isInitialized = true;
                _lastIndexTime = DateTime.Now;
            }

            var totalEntries = _exactMatchIndex.Count;
            progress?.Report($"Index built successfully: {totalEntries} entries indexed from {currentLayer} layers");

#if DEBUG
            Console.WriteLine($"[SEARCH INDEX] Built index with {totalEntries} entries");
            Console.WriteLine($"[SEARCH INDEX] Substring index has {_index.Count} keys");
#endif
        }

        /// <summary>
        /// Add value to substring index using trigrams for efficient substring matching
        /// </summary>
        private void AddToSubstringIndex(string value, IndexEntry entry)
        {
            // For values shorter than 3 characters, index as-is
            if (value.Length <= 3)
            {
                if (!_index.ContainsKey(value))
                    _index[value] = new List<IndexEntry>();
                _index[value].Add(entry);
                return;
            }

            // Create trigrams (3-character substrings) for efficient substring matching
            for (int i = 0; i <= value.Length - 3; i++)
            {
                var trigram = value.Substring(i, 3);
                if (!_index.ContainsKey(trigram))
                    _index[trigram] = new List<IndexEntry>();
                
                // Avoid duplicates in the same trigram list
                if (!_index[trigram].Any(e => e.FeatureId?.Equals(entry.FeatureId) == true))
                    _index[trigram].Add(entry);
            }

            // Also index the first 2 characters for short searches
            if (value.Length >= 2)
            {
                var prefix = value.Substring(0, 2);
                if (!_index.ContainsKey(prefix))
                    _index[prefix] = new List<IndexEntry>();
                
                if (!_index[prefix].Any(e => e.FeatureId?.Equals(entry.FeatureId) == true))
                    _index[prefix].Add(entry);
            }
        }

        /// <summary>
        /// Search the index for matching entries (ultra-fast)
        /// </summary>
        public List<IndexEntry> Search(string searchText, int maxResults = 20)
        {
            if (string.IsNullOrWhiteSpace(searchText) || searchText.Length < 2)
                return new List<IndexEntry>();

            lock (_indexLock)
            {
                if (!_isInitialized)
                    return new List<IndexEntry>();

                var searchLower = searchText.ToLowerInvariant();
                var results = new Dictionary<object, IndexEntry>();

                // First, check for exact matches
                if (_exactMatchIndex.TryGetValue(searchText, out var exactMatch))
                {
                    results[exactMatch.FeatureId ?? exactMatch.GetHashCode()] = exactMatch;
                }

                // Then search using trigrams
                if (searchLower.Length >= 3)
                {
                    // Get all entries that contain any trigram from the search text
                    var candidates = new HashSet<IndexEntry>();
                    
                    for (int i = 0; i <= searchLower.Length - 3; i++)
                    {
                        var trigram = searchLower.Substring(i, 3);
                        if (_index.TryGetValue(trigram, out var entries))
                        {
                            foreach (var entry in entries)
                            {
                                // Verify the entry actually contains the search text
                                if (entry.Value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    candidates.Add(entry);
                                }
                            }
                        }
                    }

                    // Add candidates to results (avoiding duplicates)
                    foreach (var candidate in candidates.Take(maxResults))
                    {
                        var key = candidate.FeatureId ?? candidate.GetHashCode();
                        if (!results.ContainsKey(key))
                            results[key] = candidate;
                    }
                }
                else
                {
                    // For short search terms (2 chars), use prefix search
                    if (_index.TryGetValue(searchLower, out var entries))
                    {
                        foreach (var entry in entries.Take(maxResults))
                        {
                            if (entry.Value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var key = entry.FeatureId ?? entry.GetHashCode();
                                if (!results.ContainsKey(key))
                                    results[key] = entry;
                            }
                        }
                    }
                }

                // Sort results by relevance (exact matches first, then by position of match)
                return results.Values
                    .OrderBy(e => 
                    {
                        var index = e.Value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                        return index == 0 ? 0 : index == -1 ? int.MaxValue : index + 1000;
                    })
                    .ThenBy(e => e.Value.Length)
                    .Take(maxResults)
                    .ToList();
            }
        }

        /// <summary>
        /// Get suggestions for autocomplete (instant results from index)
        /// </summary>
        public List<string> GetSuggestions(string searchText, int maxSuggestions = 10)
        {
            var entries = Search(searchText, maxSuggestions);
            return entries
                .Select(e => $"{e.LayerName}: {e.DisplayValue}")
                .Distinct()
                .Take(maxSuggestions)
                .ToList();
        }

        /// <summary>
        /// Check if index needs rebuilding (e.g., after layer changes)
        /// </summary>
        public bool NeedsRebuilding(TimeSpan maxAge)
        {
            lock (_indexLock)
            {
                return !_isInitialized || (DateTime.Now - _lastIndexTime) > maxAge;
            }
        }

        /// <summary>
        /// Get index statistics
        /// </summary>
        public (int totalEntries, int indexKeys, bool isInitialized, DateTime lastIndexTime) GetStatistics()
        {
            lock (_indexLock)
            {
                return (_exactMatchIndex.Count, _index.Count, _isInitialized, _lastIndexTime);
            }
        }

        /// <summary>
        /// Clear the index to free memory
        /// </summary>
        public void Clear()
        {
            lock (_indexLock)
            {
                _index.Clear();
                _exactMatchIndex.Clear();
                _isInitialized = false;
                _lastIndexTime = DateTime.MinValue;
            }
        }
    }
}
