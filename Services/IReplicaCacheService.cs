using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using POSM_MR3_2;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Service for managing in-memory attribute replica cache with lazy loading
    /// </summary>
    public interface IReplicaCacheService
    {
        /// <summary>
        /// Triggered when the cache needs to report progress
        /// </summary>
        event EventHandler<ProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Warm the cache by pre-fetching lightweight attribute data
        /// </summary>
        Task WarmCacheAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Search the cache for entries matching the search text
        /// </summary>
        List<InMemorySearchIndex.IndexEntry> Search(string searchText, int maxResults = 20);

        /// <summary>
        /// Get autocomplete suggestions from the cache
        /// </summary>
        List<string> GetSuggestions(string searchText, int maxSuggestions = 10);

        /// <summary>
        /// Stream new search results into the cache (lazy loading)
        /// </summary>
        Task<List<SearchResultItem>> LazyLoadAndCacheAsync(string searchText, CancellationToken cancellationToken = default);

        /// <summary>
        /// Check if a search term is cached
        /// </summary>
        bool IsSearchCached(string searchText);

        /// <summary>
        /// Get cache statistics
        /// </summary>
        (int totalEntries, int indexKeys, bool isInitialized, DateTime lastIndexTime) GetStatistics();

        /// <summary>
        /// Clear the cache to free memory
        /// </summary>
        void ClearCache();
    }

}