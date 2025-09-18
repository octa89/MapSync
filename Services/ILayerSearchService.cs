using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.UI.Controls;
using POSM_MR3_2;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Service interface for asset mode layer searching with enhanced performance
    /// </summary>
    public interface ILayerSearchService
    {
        /// <summary>
        /// Initialize the service with a MapView instance
        /// </summary>
        void Initialize(MapView mapView);
        /// <summary>
        /// Search across all enabled query layers using configuration-driven approach
        /// </summary>
        Task<List<SearchResultItem>> SearchLayersAsync(string searchText, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get autocomplete suggestions from live layer queries
        /// </summary>
        Task<List<string>> GetSuggestionsAsync(string searchText, int maxSuggestions = 10, CancellationToken cancellationToken = default);

        /// <summary>
        /// Search a specific layer with specified fields
        /// </summary>
        Task<List<SearchResultItem>> SearchSpecificLayerAsync(string layerName, List<string> searchFields, string searchText, int maxResults = 200, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validate if configured layers exist in the current map
        /// </summary>
        Task<List<string>> ValidateConfiguredLayersAsync();

        // NEW: SearchResult-based methods for structured Asset mode suggestions

        /// <summary>
        /// Get structured suggestions as AssetSearchResult objects for Asset mode
        /// </summary>
        Task<List<AssetSearchResult>> GetStructuredSuggestionsAsync(string searchText, int maxSuggestions = 8, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find first matching feature using structured search
        /// </summary>
        Task<AssetSearchResult?> FindFirstAsync(string layerName, List<string> searchFields, string searchValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Find first matching feature across all enabled layers
        /// </summary>
        Task<AssetSearchResult?> FindFirstAcrossAsync(List<QueryLayerConfig> enabledLayers, string searchValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates configuration and logs resolved layers and field lists at startup
        /// </summary>
        void ValidateConfigurationAndLogResolutions();
    }
}