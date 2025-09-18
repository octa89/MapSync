using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;
using Microsoft.Extensions.Logging;
using POSM_MR3_2;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Enhanced replica cache service with startup warming and lazy loading
    /// </summary>
    public class ReplicaCacheService : IReplicaCacheService
    {
        private readonly ILogger<ReplicaCacheService> _logger;
        private readonly IConfigurationService _configurationService;
        private readonly LayerSearchService _layerSearchService;
        private InMemorySearchIndex? _index;
        private MapView? _mapView;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, List<SearchResultItem>> _lazyCache = new Dictionary<string, List<SearchResultItem>>();

        public event EventHandler<ProgressEventArgs>? ProgressChanged;

        public ReplicaCacheService(
            ILogger<ReplicaCacheService> logger,
            IConfigurationService configurationService)
        {
            _logger = logger;
            _configurationService = configurationService;
            _layerSearchService = new LayerSearchService(configurationService, null); // Will be initialized with MapView later
        }

        /// <summary>
        /// Initialize the cache with a MapView instance
        /// </summary>
        public void Initialize(MapView mapView)
        {
            _mapView = mapView;
            _index = new InMemorySearchIndex();
            
            // Initialize the LayerSearchService with the MapView
            _layerSearchService.Initialize(mapView);

            _logger.LogInformation("[REPLICA CACHE] Service initialized with MapView");
        }

        /// <summary>
        /// Warm the cache by pre-fetching lightweight attribute data on startup
        /// </summary>
        public async Task WarmCacheAsync(CancellationToken cancellationToken = default)
        {
            if (_mapView?.Map == null || _index == null)
            {
                _logger.LogWarning("[REPLICA CACHE] Cannot warm cache - MapView or index not initialized");
                return;
            }

            try
            {
                _logger.LogInformation("[REPLICA CACHE] Starting cache warming...");
                ProgressChanged?.Invoke(this, new ProgressEventArgs("Warming search cache...", 0));

                var progress = new Progress<string>(message =>
                {
                    _logger.LogDebug("[REPLICA CACHE] {Message}", message);
                    ProgressChanged?.Invoke(this, new ProgressEventArgs(message));
                });

                await _index.BuildIndexAsync(_mapView, progress);

                var stats = _index.GetStatistics();
                _logger.LogInformation("[REPLICA CACHE] Cache warmed successfully: {TotalEntries} entries, {IndexKeys} index keys", 
                    stats.totalEntries, stats.indexKeys);

                ProgressChanged?.Invoke(this, new ProgressEventArgs($"Cache ready: {stats.totalEntries} entries indexed", 100)
                {
                    IsComplete = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPLICA CACHE] Error warming cache: {Error}", ex.Message);
                ProgressChanged?.Invoke(this, new ProgressEventArgs($"Cache warming failed: {ex.Message}")
                {
                    IsError = true
                });
                throw;
            }
        }

        /// <summary>
        /// Search the cache for entries matching the search text
        /// </summary>
        public List<InMemorySearchIndex.IndexEntry> Search(string searchText, int maxResults = 20)
        {
            if (_index == null || string.IsNullOrWhiteSpace(searchText))
            {
                return new List<InMemorySearchIndex.IndexEntry>();
            }

            try
            {
                var results = _index.Search(searchText, maxResults);
                _logger.LogDebug("[REPLICA CACHE] Cache search for '{SearchText}' returned {Count} results", 
                    searchText, results.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPLICA CACHE] Error searching cache: {Error}", ex.Message);
                return new List<InMemorySearchIndex.IndexEntry>();
            }
        }

        /// <summary>
        /// Get autocomplete suggestions from the cache
        /// </summary>
        public List<string> GetSuggestions(string searchText, int maxSuggestions = 10)
        {
            if (_index == null || string.IsNullOrWhiteSpace(searchText))
            {
                return new List<string>();
            }

            try
            {
                var suggestions = _index.GetSuggestions(searchText, maxSuggestions);
                _logger.LogDebug("[REPLICA CACHE] Cache suggestions for '{SearchText}': {Count} found", 
                    searchText, suggestions.Count);
                return suggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPLICA CACHE] Error getting suggestions: {Error}", ex.Message);
                return new List<string>();
            }
        }

        /// <summary>
        /// Stream new search results into the cache (lazy loading)
        /// </summary>
        public async Task<List<SearchResultItem>> LazyLoadAndCacheAsync(string searchText, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return new List<SearchResultItem>();
            }

            var cacheKey = searchText.ToLowerInvariant();

            // Check lazy cache first
            lock (_cacheLock)
            {
                if (_lazyCache.TryGetValue(cacheKey, out var cachedResults))
                {
                    _logger.LogDebug("[REPLICA CACHE] Lazy cache hit for '{SearchText}': {Count} results", 
                        searchText, cachedResults.Count);
                    return cachedResults;
                }
            }

            try
            {
                _logger.LogInformation("[REPLICA CACHE] Lazy loading search results for '{SearchText}'", searchText);
                ProgressChanged?.Invoke(this, new ProgressEventArgs($"Loading search results for '{searchText}'..."));

                // Perform live search using LayerSearchService
                var results = await _layerSearchService.SearchLayersAsync(searchText);

                // Stream results into the main index for future instant access
                foreach (var result in results)
                {
                    if (result.Feature?.Attributes != null)
                    {
                        var layerConfig = _configurationService.Configuration?.queryLayers?
                            .FirstOrDefault(q => q.layerName.Equals(result.LayerName, StringComparison.OrdinalIgnoreCase));

                        var searchFields = layerConfig?.searchFields ?? new List<string> { _configurationService.Configuration?.idField ?? "AssetID" };

                        foreach (var fieldName in searchFields)
                        {
                            if (result.Feature.Attributes.ContainsKey(fieldName))
                            {
                                var value = result.Feature.Attributes[fieldName]?.ToString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    _index?.Upsert(result.LayerName, fieldName, value, result.DisplayText, result.Feature, 
                                        result.Feature.Attributes.ContainsKey("OBJECTID") ? result.Feature.Attributes["OBJECTID"] : null);
                                }
                            }
                        }
                    }
                }

                // Cache the results for future requests
                lock (_cacheLock)
                {
                    _lazyCache[cacheKey] = results;

                    // Clean up old cache entries if we have too many
                    if (_lazyCache.Count > 100)
                    {
                        var oldestKeys = _lazyCache.Keys.Take(_lazyCache.Count - 50).ToList();
                        foreach (var key in oldestKeys)
                        {
                            _lazyCache.Remove(key);
                        }
                    }
                }

                _logger.LogInformation("[REPLICA CACHE] Lazy loaded and cached {Count} results for '{SearchText}'", 
                    results.Count, searchText);

                ProgressChanged?.Invoke(this, new ProgressEventArgs($"Found {results.Count} results") { IsComplete = true });

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPLICA CACHE] Error in lazy loading: {Error}", ex.Message);
                ProgressChanged?.Invoke(this, new ProgressEventArgs($"Search failed: {ex.Message}") { IsError = true });
                return new List<SearchResultItem>();
            }
        }

        /// <summary>
        /// Check if a search term is cached
        /// </summary>
        public bool IsSearchCached(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return false;

            var cacheKey = searchText.ToLowerInvariant();
            
            lock (_cacheLock)
            {
                return _lazyCache.ContainsKey(cacheKey);
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public (int totalEntries, int indexKeys, bool isInitialized, DateTime lastIndexTime) GetStatistics()
        {
            if (_index == null)
            {
                return (0, 0, false, DateTime.MinValue);
            }

            return _index.GetStatistics();
        }

        /// <summary>
        /// Clear the cache to free memory
        /// </summary>
        public void ClearCache()
        {
            try
            {
                _index?.Clear();
                
                lock (_cacheLock)
                {
                    _lazyCache.Clear();
                }

                _logger.LogInformation("[REPLICA CACHE] Cache cleared successfully");
                ProgressChanged?.Invoke(this, new ProgressEventArgs("Cache cleared") { IsComplete = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[REPLICA CACHE] Error clearing cache: {Error}", ex.Message);
            }
        }
    }
}