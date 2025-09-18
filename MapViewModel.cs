using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Location;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Toolkit.UI.Controls;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.UI;

namespace POSM_MR3_2
{
    public class MapViewModel : INotifyPropertyChanged
    {
        public MapViewModel()
        {
            // API key should only be set when online and available from config
            // Removed hardcoded API key for better offline support
            _map = new Map(SpatialReferences.WebMercator)
            {
                InitialViewpoint = new Viewpoint(new Envelope(-180, -85, 180, 85, SpatialReferences.Wgs84)),
                Basemap = new Basemap(BasemapStyle.ArcGISTopographic) // Use offline-safe basemap
            };
        }

        private Map? _map;

        public Map? Map
        {
            get { return _map; }
            set
            {
                _map = value;
                OnPropertyChanged();
            }
        }

        private GraphicsOverlayCollection? _graphicsOverlayCollection;
        public GraphicsOverlayCollection? GraphicsOverlays
        {
            get { return _graphicsOverlayCollection; }
            set
            {
                _graphicsOverlayCollection = value;
                OnPropertyChanged();
            }
        }

        public MapPoint? SearchAddress(string address, SpatialReference spatialReference)
        {
            // Implementation of search logic
            return null;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
