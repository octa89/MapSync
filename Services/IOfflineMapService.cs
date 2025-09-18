using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Tasks.Offline;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Service for handling offline map generation with enhanced basemap support
    /// </summary>
    public interface IOfflineMapService
    {
        /// <summary>
        /// Event raised when offline map generation progress changes
        /// </summary>
        event EventHandler<OfflineMapProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Event raised when offline map generation completes successfully
        /// </summary>
        event EventHandler<OfflineMapCompletedEventArgs> OfflineMapCompleted;

        /// <summary>
        /// Generate an offline map with enhanced basemap detection and handling
        /// </summary>
        /// <param name="map">The map to take offline</param>
        /// <param name="areaOfInterest">The area of interest to download</param>
        /// <param name="outputPath">The output directory path</param>
        /// <param name="options">Offline map generation options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The generated offline map result</returns>
        Task<GenerateOfflineMapResult> GenerateOfflineMapAsync(
            Map map,
            Envelope areaOfInterest,
            string outputPath,
            OfflineMapOptions? options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validate and detect available local basemap files
        /// </summary>
        /// <param name="basemapDirectory">Directory to search for basemap files</param>
        /// <returns>List of detected basemap files with metadata</returns>
        Task<List<BasemapFileInfo>> DetectLocalBasemapsAsync(string basemapDirectory);

        /// <summary>
        /// Get default parameters for offline map generation with enhanced basemap handling
        /// </summary>
        /// <param name="map">The map to take offline</param>
        /// <param name="areaOfInterest">The area of interest</param>
        /// <returns>Enhanced offline map parameters</returns>
        Task<GenerateOfflineMapParameters> CreateEnhancedParametersAsync(Map map, Envelope areaOfInterest);

        /// <summary>
        /// Cancel the current offline map generation job
        /// </summary>
        Task CancelCurrentJobAsync();

        /// <summary>
        /// Check if an offline map generation job is currently running
        /// </summary>
        bool IsGenerationInProgress { get; }

        /// <summary>
        /// Detect existing offline maps in the output directory
        /// </summary>
        /// <param name="outputDirectory">Directory to search for offline maps</param>
        /// <returns>List of existing offline maps with metadata</returns>
        Task<List<ExistingOfflineMap>> DetectExistingOfflineMapsAsync(string outputDirectory);
    }

    /// <summary>
    /// Progress event arguments for offline map generation
    /// </summary>
    public class OfflineMapProgressEventArgs : EventArgs
    {
        public int PercentComplete { get; set; }
        public string? StatusMessage { get; set; }
        public OfflineMapJobStatus Status { get; set; }
        public bool IsComplete { get; set; }
        public bool HasErrors { get; set; }
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Offline map generation options
    /// </summary>
    public class OfflineMapOptions
    {
        /// <summary>
        /// Whether to include basemap in the offline package
        /// </summary>
        public bool IncludeBasemap { get; set; } = true;

        /// <summary>
        /// Preferred local basemap directory path
        /// </summary>
        public string? LocalBasemapDirectory { get; set; }

        /// <summary>
        /// Preferred local basemap filename
        /// </summary>
        public string? LocalBasemapFilename { get; set; }

        /// <summary>
        /// Whether to force use of local basemap even if online is available
        /// </summary>
        public bool ForceLocalBasemap { get; set; } = false;

        /// <summary>
        /// Whether to return schema only for editable layers (smaller download)
        /// </summary>
        public bool SchemaOnlyForEditableLayers { get; set; } = false;

        /// <summary>
        /// Whether to include attachments
        /// </summary>
        public bool IncludeAttachments { get; set; } = true;

        /// <summary>
        /// Maximum features per layer to include
        /// </summary>
        public int? MaxFeaturesPerLayer { get; set; }

        /// <summary>
        /// Whether to show user confirmation dialogs
        /// </summary>
        public bool ShowUserConfirmation { get; set; } = true;
    }

    /// <summary>
    /// Information about a detected basemap file
    /// </summary>
    public class BasemapFileInfo
    {
        public string FilePath { get; set; } = ""; // Required
        public string FileName { get; set; } = ""; // Required
        public string FileType { get; set; } = ""; // Required // tpk, tpkx, vtpk, mmpk
        public long FileSizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsValid { get; set; }
        public string? ValidationError { get; set; }
        public string DisplayName => $"{FileName} ({FileType.ToUpper()}) - {FileSizeBytes / 1024 / 1024:F1} MB";
    }

    /// <summary>
    /// Information about an existing offline map
    /// </summary>
    public class ExistingOfflineMap
    {
        public string FolderPath { get; set; } = "";
        public string FolderName { get; set; } = "";
        public DateTime CreatedDate { get; set; }
        public long TotalSizeBytes { get; set; }
        public int LayerCount { get; set; }
        public bool IsValid { get; set; }
        public string? ValidationError { get; set; }
        public string DisplayName => $"{FolderName} - {CreatedDate:MMM dd, yyyy HH:mm} ({TotalSizeBytes / 1024 / 1024:F1} MB)";
        public string ShortDisplayName => $"{CreatedDate:MMM dd, HH:mm} ({TotalSizeBytes / 1024 / 1024:F1} MB)";
    }

    /// <summary>
    /// Status of offline map generation job
    /// </summary>
    public enum OfflineMapJobStatus
    {
        NotStarted,
        Started,
        Paused,
        Succeeded,
        Failed,
        Canceling,
        Canceled
    }
}