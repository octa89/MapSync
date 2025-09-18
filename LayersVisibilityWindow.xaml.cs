using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Newtonsoft.Json;

namespace WpfMapApp1
{
    public partial class LayersVisibilityWindow : Window
    {
        private readonly Map _map;
        private LayerVisibilityConfig _visibilityConfig = new LayerVisibilityConfig();
        private readonly string _configPath;

        public ObservableCollection<LayerViewModel> LayerViewModels { get; } = new ObservableCollection<LayerViewModel>();

        public LayersVisibilityWindow(Map map)
        {
            InitializeComponent();
            _map = map;
            _configPath = Path.Combine(Directory.GetCurrentDirectory(), "layer-visibility.json");
            
            DataContext = this;
            LayersTreeView.ItemsSource = LayerViewModels;

            LoadConfiguration();
            _ = InitializeLayersAsync();
        }

        private void LoadConfiguration()
        {
            try
            {
                // First, try to load from main App.Configuration
                if (App.Configuration?.layerVisibilities != null && App.Configuration.layerVisibilities.Count > 0)
                {
                    _visibilityConfig = new LayerVisibilityConfig
                    {
                        LayerVisibilities = new Dictionary<string, bool>(App.Configuration.layerVisibilities)
                    };
                    Console.WriteLine($"[LAYER VISIBILITY] Loaded configuration from main config with {_visibilityConfig.LayerVisibilities.Count} layer settings");
                    return;
                }

                // Fallback to separate layer-visibility.json file
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _visibilityConfig = JsonConvert.DeserializeObject<LayerVisibilityConfig>(json) ?? new LayerVisibilityConfig();
                    Console.WriteLine($"[LAYER VISIBILITY] Loaded configuration from separate file with {_visibilityConfig.LayerVisibilities.Count} layer settings");
                }
                else
                {
                    _visibilityConfig = new LayerVisibilityConfig();
                    Console.WriteLine("[LAYER VISIBILITY] No existing configuration found, using defaults");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAYER VISIBILITY] Error loading configuration: {ex.Message}");
                _visibilityConfig = new LayerVisibilityConfig();
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                // Update configuration with current visibility states
                foreach (var layerViewModel in GetAllLayerViewModels())
                {
                    _visibilityConfig.LayerVisibilities[layerViewModel.LayerId] = layerViewModel.IsVisible;
                }

                // Save to separate layer visibility file
                var json = JsonConvert.SerializeObject(_visibilityConfig, Formatting.Indented);
                File.WriteAllText(_configPath, json);
                Console.WriteLine($"[LAYER VISIBILITY] Saved configuration with {_visibilityConfig.LayerVisibilities.Count} layer settings to: {_configPath}");

                // Also update the main application configuration
                if (App.Configuration != null)
                {
                    App.Configuration.layerVisibilities = new Dictionary<string, bool>(_visibilityConfig.LayerVisibilities);
                    
                    // Save main configuration
                    string mainConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
                    File.WriteAllText(mainConfigPath, JsonConvert.SerializeObject(App.Configuration, Formatting.Indented));
                    Console.WriteLine($"[LAYER VISIBILITY] Updated main configuration with layer visibility settings");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAYER VISIBILITY] Error saving configuration: {ex.Message}");
                MessageBox.Show($"Error saving layer visibility configuration: {ex.Message}", "Save Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task InitializeLayersAsync()
        {
            try
            {
                Console.WriteLine("[LAYER VISIBILITY] Initializing layer tree...");
                LayerViewModels.Clear();

                await Task.Run(async () =>
                {
                    foreach (Layer layer in _map.OperationalLayers)
                    {
                        var layerViewModel = await CreateLayerViewModelAsync(layer);
                        if (layerViewModel != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => LayerViewModels.Add(layerViewModel));
                        }
                    }
                });

                // Apply saved visibility settings
                ApplySavedVisibility();
                Console.WriteLine("[LAYER VISIBILITY] Layer tree initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAYER VISIBILITY] Error initializing layers: {ex.Message}");
                MessageBox.Show($"Error loading layers: {ex.Message}", "Load Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task<LayerViewModel?> CreateLayerViewModelAsync(Layer layer)
        {
            try
            {
                // Load layer if needed
                if (layer is ILoadable loadable && loadable.LoadStatus == LoadStatus.NotLoaded)
                {
                    await loadable.LoadAsync();
                }

                var layerId = GetLayerId(layer);
                var layerViewModel = new LayerViewModel
                {
                    Name = string.IsNullOrEmpty(layer.Name) ? "Unnamed Layer" : layer.Name,
                    LayerId = layerId,
                    IsVisible = layer.IsVisible,
                    ActualLayer = layer
                };

                // Add nested layers/sublayers
                await AddNestedLayersAsync(layerViewModel, layer);

                return layerViewModel;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAYER VISIBILITY] Error creating view model for layer {layer?.Name}: {ex.Message}");
                return null;
            }
        }

        private async Task AddNestedLayersAsync(LayerViewModel parentViewModel, Layer parentLayer)
        {
            // Handle GroupLayer children
            if (parentLayer is GroupLayer groupLayer)
            {
                foreach (Layer childLayer in groupLayer.Layers)
                {
                    var childViewModel = await CreateLayerViewModelAsync(childLayer);
                    if (childViewModel != null)
                    {
                        parentViewModel.Children.Add(childViewModel);
                    }
                }
            }

            // Handle ILayerContent sublayers
            if (parentLayer is ILayerContent layerContent && layerContent.SublayerContents?.Count > 0)
            {
                foreach (ILayerContent sublayer in layerContent.SublayerContents)
                {
                    if (sublayer is Layer childLayer)
                    {
                        var childViewModel = await CreateLayerViewModelAsync(childLayer);
                        if (childViewModel != null)
                        {
                            parentViewModel.Children.Add(childViewModel);
                        }
                    }
                }
            }
        }

        private string GetLayerId(Layer layer)
        {
            // Create a stable identifier for the layer (without hash codes that change between runs)
            var layerName = layer.Name ?? "Unnamed";
            var layerType = layer.GetType().Name;
            
            // For now, use just layer type and name for stability
            // This should be consistent across app runs
            return $"{layerType}_{layerName}";
        }

        private void ApplySavedVisibility()
        {
            foreach (var layerViewModel in GetAllLayerViewModels())
            {
                if (_visibilityConfig.LayerVisibilities.TryGetValue(layerViewModel.LayerId, out bool savedVisibility))
                {
                    layerViewModel.IsVisible = savedVisibility;
                    if (layerViewModel.ActualLayer != null)
                    {
                        layerViewModel.ActualLayer.IsVisible = savedVisibility;
                    }
                }
            }
        }

        private IEnumerable<LayerViewModel> GetAllLayerViewModels()
        {
            foreach (var rootLayer in LayerViewModels)
            {
                yield return rootLayer;
                foreach (var nestedLayer in GetNestedLayerViewModels(rootLayer))
                {
                    yield return nestedLayer;
                }
            }
        }

        private IEnumerable<LayerViewModel> GetNestedLayerViewModels(LayerViewModel parentLayer)
        {
            foreach (var child in parentLayer.Children)
            {
                yield return child;
                foreach (var nested in GetNestedLayerViewModels(child))
                {
                    yield return nested;
                }
            }
        }

        private void LayerCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is LayerViewModel layerViewModel)
            {
                SetLayerVisibility(layerViewModel, true);
            }
        }

        private void LayerCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is LayerViewModel layerViewModel)
            {
                SetLayerVisibility(layerViewModel, false);
            }
        }

        private void SetLayerVisibility(LayerViewModel layerViewModel, bool isVisible)
        {
            layerViewModel.IsVisible = isVisible;
            if (layerViewModel.ActualLayer != null)
            {
                layerViewModel.ActualLayer.IsVisible = isVisible;
                Console.WriteLine($"[LAYER VISIBILITY] Set layer '{layerViewModel.Name}' visibility to: {isVisible}");
            }
        }

        private void ShowAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var layerViewModel in GetAllLayerViewModels())
            {
                SetLayerVisibility(layerViewModel, true);
            }
            Console.WriteLine("[LAYER VISIBILITY] All layers set to visible");
        }

        private void HideAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var layerViewModel in GetAllLayerViewModels())
            {
                SetLayerVisibility(layerViewModel, false);
            }
            Console.WriteLine("[LAYER VISIBILITY] All layers set to hidden");
        }

        private void SaveAndCloseButton_Click(object sender, RoutedEventArgs e)
        {
            SaveConfiguration();
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            SaveConfiguration();
            base.OnClosing(e);
        }
    }

    public class LayerViewModel : INotifyPropertyChanged
    {
        private bool _isVisible;
        private string _name = "";

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public string LayerId { get; set; } = "";

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public Layer? ActualLayer { get; set; }

        public ObservableCollection<LayerViewModel> Children { get; } = new ObservableCollection<LayerViewModel>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class LayerVisibilityConfig
    {
        public Dictionary<string, bool> LayerVisibilities { get; set; } = new Dictionary<string, bool>();
    }
}