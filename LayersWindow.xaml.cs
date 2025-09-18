using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;

namespace WpfMapApp1
{
    public partial class LayersWindow : Window
    {
        public Map Map { get; }

        public LayersWindow(Map map)
        {
            InitializeComponent();
            Map = map;
            DataContext = this;

            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            // Pre-load layer names so TreeView shows them immediately
            await PreloadLayerNamesAsync(Map.OperationalLayers);
            await ApplySavedLayerVisibilitiesAsync();
        }

        // ───────────────────────────────────────────────
        // Recursively load every layer/sublayer
        // ───────────────────────────────────────────────
        private static async Task PreloadLayerNamesAsync(IEnumerable<ILayerContent> items)
        {
            foreach (ILayerContent item in items)
            {
                // Only if the object supports ILoadable
                if (item is ILoadable loadable &&
                    loadable.LoadStatus == LoadStatus.NotLoaded)
                {
                    try { await loadable.LoadAsync(); }
                    catch { /* ignore individual load failures */ }
                }

                // Recurse into child sublayers
                if (item.SublayerContents?.Count > 0)
                    await PreloadLayerNamesAsync(
                        item.SublayerContents.OfType<ILayerContent>());
            }
        }

        // ───────────────────────────────────────────────
        // Checkbox toggled → cascade visibility
        // ───────────────────────────────────────────────
        private void LayerCheckBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is Layer layer)
            {
                bool vis = cb.IsChecked == true;
                SetVisibilityRecursive(layer, vis);
            }
        }

        private static void SetVisibilityRecursive(Layer layer, bool visible)
        {
            layer.IsVisible = visible;

            // GroupLayer children
            if (layer is GroupLayer g)
                foreach (Layer child in g.Layers)
                    SetVisibilityRecursive(child, visible);

            // Any ILayerContent sublayers
            if (layer is ILayerContent ilc && ilc.SublayerContents?.Count > 0)
                foreach (ILayerContent child in ilc.SublayerContents)
                    if (child is Layer childLayer)
                        SetVisibilityRecursive(childLayer, visible);
        }

        private static string GetStableLayerId(Layer layer)
        {
            var name = string.IsNullOrWhiteSpace(layer.Name) ? "Unnamed" : layer.Name;
            return $"{layer.GetType().Name}_{name}";
        }

        private async Task ApplySavedLayerVisibilitiesAsync()
        {
            try
            {
                var vis = App.Configuration?.layerVisibilities;
                if (vis == null || vis.Count == 0) return;

                await ApplyToCollectionAsync(Map.OperationalLayers);

                async Task ApplyToCollectionAsync(IEnumerable<Layer> layers)
                {
                    foreach (var layer in layers)
                    {
                        if (layer is ILoadable load && load.LoadStatus == LoadStatus.NotLoaded)
                        {
                            try { await load.LoadAsync(); } catch { }
                        }
                        var key = GetStableLayerId(layer);
                        if (vis.TryGetValue(key, out bool isVisible))
                        {
                            layer.IsVisible = isVisible;
                        }

                        if (layer is GroupLayer g)
                        {
                            await ApplyToCollectionAsync(g.Layers);
                        }
                        if (layer is ILayerContent lc && lc.SublayerContents?.Count > 0)
                        {
                            foreach (var sub in lc.SublayerContents)
                            {
                                if (sub is Layer subLayer)
                                {
                                    await ApplyToCollectionAsync(new[] { subLayer });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private Dictionary<string, bool> CollectLayerVisibilities()
        {
            var dict = new Dictionary<string, bool>();
            void Walk(IEnumerable<Layer> layers)
            {
                foreach (var layer in layers)
                {
                    dict[GetStableLayerId(layer)] = layer.IsVisible;
                    if (layer is GroupLayer g) Walk(g.Layers);
                    if (layer is ILayerContent lc && lc.SublayerContents?.Count > 0)
                    {
                        foreach (var sub in lc.SublayerContents)
                            if (sub is Layer subLayer) Walk(new[] { subLayer });
                    }
                }
            }
            Walk(Map.OperationalLayers);
            return dict;
        }

        private void ShowAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var layer in Map.OperationalLayers) SetVisibilityRecursive(layer, true);
        }

        private void HideAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var layer in Map.OperationalLayers) SetVisibilityRecursive(layer, false);
        }

        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var vis = CollectLayerVisibilities();
                if (App.Configuration != null)
                {
                    App.Configuration.layerVisibilities = vis;
                    var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "config.json");
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(App.Configuration, Newtonsoft.Json.Formatting.Indented);
                    System.IO.File.WriteAllText(path, json);
                }
            }
            catch { }
            Close();
        }
    }
}
