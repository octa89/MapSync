using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Toolkit.UI.Controls;
using System;
using System.ComponentModel;
using System.Windows;

namespace WpfMapApp1
{
    public partial class BasemapGalleryWindow : Window
    {
        private Map? _map;

        public Map? Map
        {
            get => _map;
            set
            {
                _map = value;
                MyBasemapGallery.GeoModel = _map;
            }
        }

        public BasemapGalleryWindow()
        {
            InitializeComponent();

            Loaded += BasemapGalleryWindow_Loaded;
            Activated += BasemapGalleryWindow_Activated;

            // Subscribe to changes on the SelectedBasemap dependency property.
            var dpd = DependencyPropertyDescriptor.FromProperty(
                BasemapGallery.SelectedBasemapProperty,
                typeof(BasemapGallery));
            if (dpd != null)
            {
                dpd.AddValueChanged(MyBasemapGallery, OnSelectedBasemapChanged);
            }
        }

        private void BasemapGalleryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            MyBasemapGallery.GeoModel = _map;
            MyBasemapGallery.SelectedBasemap = null;
        }

        private void BasemapGalleryWindow_Activated(object? sender, EventArgs e)
        {
            MyBasemapGallery.GeoModel = _map;
            MyBasemapGallery.SelectedBasemap = null;
        }

        private void OnSelectedBasemapChanged(object? sender, EventArgs e)
        {
            // When a new basemap is selected, update the main map's basemap.
            if (MyBasemapGallery.SelectedBasemap != null && _map != null)
            {
                // MyBasemapGallery.SelectedBasemap is a BasemapGalleryItem.
                // Its Basemap property holds the actual Basemap.
                _map.Basemap = MyBasemapGallery.SelectedBasemap.Basemap.Clone();
                try
                {
                    // Persist selection by friendly name if available
                    var name = MyBasemapGallery.SelectedBasemap?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name) && App.Configuration != null)
                    {
                        App.Configuration.defaultBasemap = name;
                        var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "config.json");
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(App.Configuration, Newtonsoft.Json.Formatting.Indented);
                        System.IO.File.WriteAllText(path, json);
                    }
                }
                catch { }
            }
        }
    }
}
