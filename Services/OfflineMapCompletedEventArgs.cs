using System;

namespace WpfMapApp1.Services
{
    /// <summary>
    /// Event arguments for when offline map generation completes successfully
    /// </summary>
    public class OfflineMapCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// Path to the generated offline map folder
        /// </summary>
        public string OfflineMapPath { get; set; } = string.Empty;

        /// <summary>
        /// Whether the generation had layer errors but still succeeded
        /// </summary>
        public bool HasLayerErrors { get; set; }

        /// <summary>
        /// Number of layer errors encountered
        /// </summary>
        public int LayerErrorCount { get; set; }
    }
}