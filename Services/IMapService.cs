using System;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;

namespace WpfMapApp1.Services
{
    public interface IMapService
    {
        /// <summary>
        /// Load a map from either WebMap ID (GUID) or MMPK file path
        /// </summary>
        Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null);

        /// <summary>
        /// Initialize MapView with the loaded map and proper extent
        /// </summary>
        Task InitializeMapViewAsync(MapView mapView, IProgress<string>? progress = null);

        /// <summary>
        /// Geocode an address and zoom the map to the result
        /// </summary>
        Task<bool> TryGeocodeAndZoomAsync(MapView mapView, string text, double scale = 5000);

        /// <summary>
        /// Determine if the mapId is a file path (MMPK) or WebMap ID (GUID)
        /// </summary>
        bool IsFilePathSource(string mapId);

        /// <summary>
        /// Validate that a WebMap ID is a proper GUID format
        /// </summary>
        bool IsValidWebMapId(string mapId);
    }
}