using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace WpfMapApp1
{
    public partial class ConfigWindow : Window
    {
        public Config Configuration { get; private set; }
        public ObservableCollection<FeatureLayer> AvailableLayers { get; } = new ObservableCollection<FeatureLayer>();
        
        private Map? _currentMap;
        private readonly List<LayerConfigControl> _layerConfigControls = new List<LayerConfigControl>();
        private bool _isInitializing = true; // Flag to prevent event handlers during initialization

        public ConfigWindow(Config currentConfig, Map map)
        {
            InitializeComponent();
            
            // Initialize ComboBox items
            cmbInspectionType.ItemsSource = new List<string>
            {
                "NASSCO PACP",
                "NASSCO LACP", 
                "NASSCO MACP Level 1",
                "NASSCO MACP Level 2",
                "POSM",
                "Custom"
            };

            Configuration = currentConfig ?? new Config();
            _currentMap = map;

            LoadCurrentConfig();
            InitializeBasemapDropdown();
            InitializeUI();
            LoadAvailableLayers();
            _isInitializing = false; // Enable event handlers after initialization
        }

        private sealed class BasemapOption
        {
            public string Name { get; }
            public string Description { get; }
            public BasemapOption(string name, string description)
            {
                Name = name; Description = description;
            }
            public override string ToString() => Name;
        }

        private void InitializeBasemapDropdown()
        {
            // Populate a curated list matching ArcGIS Basemap styles
            var options = new List<BasemapOption>
            {
                new("World Imagery", "ArcGISImagery"),
                new("World Imagery Hybrid", "ArcGISImageryLabels"),
                new("World Street Map", "ArcGISStreets"),
                new("World Topographic Map", "ArcGISTopographic"),
                new("World Navigation Map", "ArcGISNavigation"),
                new("World Dark Gray Canvas", "ArcGISDarkGray"),
                new("World Light Gray Canvas", "ArcGISLightGray"),
                new("World Terrain", "ArcGISTerrain"),
                new("OpenStreetMap", "OSMStandard"),
                new("World Oceans", "ArcGISOceans")
            };

            if (FindName("cmbBasemap") is ComboBox cmb)
            {
                cmb.ItemsSource = options;
                // Select based on current config
                if (!string.IsNullOrWhiteSpace(Configuration.defaultBasemap))
                {
                    cmb.SelectedItem = options.FirstOrDefault(o => o.Name.Equals(Configuration.defaultBasemap, StringComparison.OrdinalIgnoreCase));
                }
                cmb.SelectionChanged += cmbBasemap_SelectionChanged;
            }
        }

        private void cmbBasemap_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (sender is ComboBox cmb && cmb.SelectedItem is BasemapOption opt)
            {
                Configuration.defaultBasemap = opt.Name;
            }
        }

        private void InitializeUI()
        {
            // Initialize UI state based on offline mode
            txtOfflineBasemap.IsEnabled = Configuration.offlineMode;
            btnLoadLayers.IsEnabled = _currentMap != null;
        }

        private void LoadCurrentConfig()
        {
            // Load basic configuration values
            txtPosmPath.Text = Configuration.posmExecutablePath ?? string.Empty;
            txtMapSource.Text = Configuration.mapId ?? string.Empty;
            txtApiKey.Text = Configuration.apiKey ?? string.Empty;
            
            // Note: Primary ID Field will be set after the selected layer is loaded
            // This happens in UpdateIdFieldDropdown() method
            
            // Load offline mode settings
            chkOfflineMode.IsChecked = Configuration.offlineMode;
            txtOfflineBasemap.Text = Configuration.offlineBasemapPath ?? string.Empty;

            // Set basemap dropdown selection if present
            if (FindName("cmbBasemap") is ComboBox cmb && cmb.ItemsSource is IEnumerable<object> items)
            {
                var match = items.OfType<object>().FirstOrDefault(o => (o as dynamic)?.Name == Configuration.defaultBasemap);
                if (match != null) cmb.SelectedItem = match;
            }

            // Handle the Inspection Type
            string configInspectionType = Configuration.inspectionType ?? string.Empty;
            var inspectionItems = cmbInspectionType.ItemsSource as List<string>;
            if (!string.IsNullOrWhiteSpace(configInspectionType))
            {
                if (inspectionItems != null && !inspectionItems.Contains(configInspectionType))
                {
                    inspectionItems.Add(configInspectionType);
                    cmbInspectionType.ItemsSource = null;
                    cmbInspectionType.ItemsSource = inspectionItems;
                }
                cmbInspectionType.SelectedItem = configInspectionType;
            }
            else
            {
                cmbInspectionType.SelectedIndex = -1;
            }
        }

        private async void LoadAvailableLayers()
        {
            AvailableLayers.Clear();

            if (_currentMap?.OperationalLayers != null)
            {
                await LoadLayersFromMap(_currentMap);
            }

            // Bind the ComboBox to the available layers
            cmbSelectedLayer.ItemsSource = AvailableLayers;

            // If a layer is already set in the configuration, select it
            if (!string.IsNullOrEmpty(Configuration.selectedLayer))
            {
                var selected = AvailableLayers.FirstOrDefault(l =>
                    l.Name.Equals(Configuration.selectedLayer, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                {
                    cmbSelectedLayer.SelectedItem = selected;
                }
            }

            // Build the layer configuration UI
            await BuildLayerConfigurationUI();
        }

        private async Task LoadLayersFromMap(Map map)
        {
            void TraverseLayers(IEnumerable<Layer> layers)
            {
                foreach (var layer in layers)
                {
                    switch (layer)
                    {
                        case FeatureLayer fl:
                            if (string.IsNullOrEmpty(fl.Name))
                                fl.Name = fl.FeatureTable?.DisplayName ?? "Untitled Layer";
                            AvailableLayers.Add(fl);
                            break;

                        case GroupLayer gl:
                            TraverseLayers(gl.Layers); // Recurse into group
                            break;
                    }
                }
            }

            TraverseLayers(map.OperationalLayers);
            
            // Load each layer to access field information
            foreach (var layer in AvailableLayers)
            {
                if (layer.LoadStatus != LoadStatus.Loaded)
                {
                    try
                    {
                        await layer.LoadAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load layer {layer.Name}: {ex.Message}");
                    }
                }
            }
        }

        private async Task BuildLayerConfigurationUI()
        {
            pnlLayerConfigs.Children.Clear();
            _layerConfigControls.Clear();

            // First, add existing configured layers
            foreach (var queryLayer in Configuration.queryLayers)
            {
                var layer = AvailableLayers.FirstOrDefault(l => l.Name.Equals(queryLayer.layerName, StringComparison.OrdinalIgnoreCase));
                var control = new LayerConfigControl(queryLayer, layer);
                _layerConfigControls.Add(control);
                pnlLayerConfigs.Children.Add(control);
            }

            // Then add unconfigured layers from the map
            foreach (var layer in AvailableLayers)
            {
                if (!Configuration.queryLayers.Any(ql => ql.layerName.Equals(layer.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var queryConfig = new QueryLayerConfig
                    {
                        layerName = layer.Name,
                        enabled = false,
                        searchFields = new List<string>(),
                        displayField = ""
                    };

                    var control = new LayerConfigControl(queryConfig, layer);
                    _layerConfigControls.Add(control);
                    pnlLayerConfigs.Children.Add(control);
                }
            }

            // Update the ID field dropdown based on available fields
            await UpdateIdFieldDropdown();
        }

        private async Task UpdateIdFieldDropdown()
        {
            // First, set the selected layer from configuration
            if (!string.IsNullOrEmpty(Configuration.selectedLayer))
            {
                var selectedLayer = AvailableLayers.FirstOrDefault(l => l.Name == Configuration.selectedLayer);
                if (selectedLayer != null)
                {
                    // Temporarily disable the initialization flag to allow the selection changed event
                    var wasInitializing = _isInitializing;
                    _isInitializing = false;
                    
                    cmbSelectedLayer.SelectedItem = selectedLayer;
                    
                    // Manually populate the ID field dropdown since we're bypassing the event handler
                    try
                    {
                        if (selectedLayer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                        {
                            await selectedLayer.LoadAsync();
                        }

                        var fields = selectedLayer.FeatureTable?.Fields
                            .Where(f => !string.IsNullOrEmpty(f.Name))
                            .Select(f => f.Name)
                            .ToList();

                        cmbIdField.ItemsSource = fields;

                        // Now set the configured ID field
                        if (!string.IsNullOrEmpty(Configuration.idField) && fields?.Contains(Configuration.idField) == true)
                        {
                            cmbIdField.SelectedItem = Configuration.idField;
                        }
                        else if (fields?.Any() == true)
                        {
                            // If the configured field doesn't exist, select the first field
                            cmbIdField.SelectedIndex = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading fields: {ex.Message}");
                    }
                    
                    // Restore the initialization flag
                    _isInitializing = wasInitializing;
                }
            }
            else
            {
                // No layer configured, clear dropdowns
                cmbIdField.ItemsSource = null;
                cmbIdField.Items.Clear();
            }
        }

        #region Event Handlers

        private void BrowsePosmPath_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
                Title = "Select POSM Executable"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                txtPosmPath.Text = openFileDialog.FileName;
            }
        }

        private void BrowseMapSource_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Mobile Map Package (*.mmpk)|*.mmpk|All Files (*.*)|*.*",
                Title = "Select MMPK File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                txtMapSource.Text = openFileDialog.FileName;
            }
        }

        private void BrowseOfflineBasemap_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Tile Packages (*.tpk;*.tpkx)|*.tpk;*.tpkx|Vector Tile Packages (*.vtpk)|*.vtpk|All Basemap Files (*.tpk;*.tpkx;*.vtpk)|*.tpk;*.tpkx;*.vtpk",
                Title = "Select Offline Basemap"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                txtOfflineBasemap.Text = openFileDialog.FileName;
            }
        }

        private void chkOfflineMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            txtOfflineBasemap.IsEnabled = chkOfflineMode.IsChecked ?? false;
        }

        private async void txtMapSource_TextChanged(object sender, TextChangedEventArgs e)
        {
            // If the map source changes, we might need to reload layers
            // This is a placeholder for potential future functionality
        }

        private async void LoadLayers_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMap != null)
            {
                try
                {
                    btnLoadLayers.Content = "🔄 Loading...";
                    btnLoadLayers.IsEnabled = false;

                    await LoadLayersFromMap(_currentMap);
                    await BuildLayerConfigurationUI();

                    btnLoadLayers.Content = "✅ Layers Loaded";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading layers: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    btnLoadLayers.Content = "❌ Load Failed";
                }
                finally
                {
                    await Task.Delay(1500); // Show status for 1.5 seconds
                    btnLoadLayers.Content = "🔄 Load Layers from Map";
                    btnLoadLayers.IsEnabled = true;
                }
            }
        }

        private void AddCustomLayer_Click(object sender, RoutedEventArgs e)
        {
            var customLayer = new QueryLayerConfig
            {
                layerName = "Custom Layer",
                enabled = true,
                searchFields = new List<string>(),
                displayField = ""
            };

            var control = new LayerConfigControl(customLayer, null);
            _layerConfigControls.Add(control);
            pnlLayerConfigs.Children.Add(control);
        }

        private void cmbInspectionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Don't show dialog during initialization
            if (_isInitializing) return;
            
            if (cmbInspectionType.SelectedItem?.ToString() == "Custom")
            {
                // Open custom inspection type input dialog
                var customTypeDialog = new CustomInspectionTypeDialog();
                if (customTypeDialog.ShowDialog() == true)
                {
                    string customType = customTypeDialog.CustomInspectionType;
                    if (!string.IsNullOrWhiteSpace(customType))
                    {
                        var inspectionItems = cmbInspectionType.ItemsSource as List<string>;
                        if (inspectionItems != null)
                        {
                            // Remove the generic "Custom" and add the specific custom type
                            if (!inspectionItems.Contains(customType))
                            {
                                var insertIndex = System.Math.Max(0, inspectionItems.Count - 1);
                                inspectionItems.Insert(insertIndex, customType); // Insert before "Custom"
                            }
                            cmbInspectionType.ItemsSource = null;
                            cmbInspectionType.ItemsSource = inspectionItems;
                            cmbInspectionType.SelectedItem = customType;
                        }
                    }
                }
                else
                {
                    // User cancelled, revert selection
                    if (!string.IsNullOrWhiteSpace(Configuration.inspectionType))
                    {
                        cmbInspectionType.SelectedItem = Configuration.inspectionType;
                    }
                    else
                    {
                        cmbInspectionType.SelectedIndex = -1;
                    }
                }
            }
        }

        private void cmbIdField_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbIdField.SelectedItem is string selectedField)
            {
                Configuration.idField = selectedField;
            }
        }

        private async void cmbSelectedLayer_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Don't show dialog during initialization
            if (_isInitializing) return;
            
            if (cmbSelectedLayer.SelectedItem is FeatureLayer selectedLayer)
            {
                Configuration.selectedLayer = selectedLayer.Name;

                // Update field dropdown for this layer
                try
                {
                    if (selectedLayer.LoadStatus != LoadStatus.Loaded)
                    {
                        await selectedLayer.LoadAsync();
                    }

                    var fields = selectedLayer.FeatureTable?.Fields
                        .Where(f => !string.IsNullOrEmpty(f.Name))
                        .Select(f => f.Name)
                        .ToList();

                    cmbIdField.ItemsSource = fields;

                    if (!string.IsNullOrEmpty(Configuration.idField) && fields?.Contains(Configuration.idField) == true)
                    {
                        cmbIdField.SelectedItem = Configuration.idField;
                    }
                    else if (fields?.Any() == true)
                    {
                        cmbIdField.SelectedIndex = 0;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading fields from selected layer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                // No layer selected, clear the ID field dropdown
                cmbIdField.ItemsSource = null;
                cmbIdField.Items.Clear();
                Configuration.selectedLayer = string.Empty;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Update the Configuration object with new values
                Configuration.posmExecutablePath = txtPosmPath.Text;
                Configuration.mapId = txtMapSource.Text;
                Configuration.inspectionType = cmbInspectionType.Text;
                Configuration.apiKey = txtApiKey.Text;
                Configuration.idField = cmbIdField.SelectedItem?.ToString() ?? "AssetID";
                
                // Update offline mode settings
                Configuration.offlineMode = chkOfflineMode.IsChecked ?? false;
                Configuration.offlineBasemapPath = txtOfflineBasemap.Text;

                // Update selected layer
                if (cmbSelectedLayer.SelectedItem is FeatureLayer selectedLayer)
                {
                    Configuration.selectedLayer = selectedLayer.Name;
                }

                // Collect query layer configurations from UI controls
                Configuration.queryLayers.Clear();
                var processedLayerNames = new HashSet<string>();
                
                foreach (var control in _layerConfigControls)
                {
                    var config = control.GetConfiguration();
                    if (config != null && !string.IsNullOrWhiteSpace(config.layerName))
                    {
                        // Avoid duplicates
                        if (!processedLayerNames.Contains(config.layerName))
                        {
                            Configuration.queryLayers.Add(config);
                            processedLayerNames.Add(config.layerName);
                        }
                    }
                }

                // Save configuration to file
                string configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
                var json = JsonConvert.SerializeObject(Configuration, Formatting.Indented);
                File.WriteAllText(configPath, json);

                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving configuration: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        #endregion
    }

    // Individual layer configuration control
    public class LayerConfigControl : Border
    {
        private readonly QueryLayerConfig _config;
        private readonly FeatureLayer? _layer;
        
        private CheckBox _enabledCheckBox = null!;
        private TextBox _layerNameTextBox = null!;
        private ComboBox _displayFieldCombo = null!;
        private WrapPanel _fieldsPanel = null!;
        private readonly List<CheckBox> _fieldCheckBoxes = new List<CheckBox>();

        public LayerConfigControl(QueryLayerConfig config, FeatureLayer? layer)
        {
            _config = config;
            _layer = layer;
            
            BuildUI();
            LoadConfiguration();
        }

        private void BuildUI()
        {
            // Main container styling
            Background = System.Windows.Media.Brushes.White;
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 195, 199));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(15);
            Margin = new Thickness(0, 0, 0, 10);

            var mainStack = new StackPanel();

            // Header with layer name and enabled checkbox
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _layerNameTextBox = new TextBox
            {
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                IsReadOnly = _layer != null // Read-only if bound to actual layer
            };
            Grid.SetColumn(_layerNameTextBox, 0);

            _enabledCheckBox = new CheckBox
            {
                Content = "Enable Search",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            _enabledCheckBox.Checked += OnEnabledChanged;
            _enabledCheckBox.Unchecked += OnEnabledChanged;
            Grid.SetColumn(_enabledCheckBox, 1);

            headerGrid.Children.Add(_layerNameTextBox);
            headerGrid.Children.Add(_enabledCheckBox);
            mainStack.Children.Add(headerGrid);

            // Display field selection
            var displayFieldStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            displayFieldStack.Children.Add(new TextBlock 
            { 
                Text = "Display Field:", 
                FontSize = 12, 
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 73, 94)),
                Margin = new Thickness(0, 0, 0, 5)
            });

            _displayFieldCombo = new ComboBox
            {
                MinWidth = 200,
                Margin = new Thickness(0, 0, 0, 10)
            };
            displayFieldStack.Children.Add(_displayFieldCombo);
            mainStack.Children.Add(displayFieldStack);

            // Searchable fields
            var fieldsStack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            fieldsStack.Children.Add(new TextBlock 
            { 
                Text = "Searchable Fields:", 
                FontSize = 12, 
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 73, 94)),
                Margin = new Thickness(0, 0, 0, 5)
            });

            _fieldsPanel = new WrapPanel();
            fieldsStack.Children.Add(_fieldsPanel);
            mainStack.Children.Add(fieldsStack);

            // Delete button for custom layers
            if (_layer == null)
            {
                var deleteButton = new Button
                {
                    Content = "🗑️ Remove",
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                deleteButton.Click += DeleteLayer_Click;
                mainStack.Children.Add(deleteButton);
            }

            Child = mainStack;
        }

        private async void LoadConfiguration()
        {
            _layerNameTextBox.Text = _config.layerName;
            _enabledCheckBox.IsChecked = _config.enabled;

            // Load fields if we have a layer
            if (_layer?.FeatureTable?.Fields != null)
            {
                var fields = _layer.FeatureTable.Fields
                    .Where(f => !string.IsNullOrWhiteSpace(f.Name))
                    .Select(f => f.Name)
                    .OrderBy(f => f)
                    .ToList();

                // Populate display field combo
                _displayFieldCombo.ItemsSource = fields;
                if (!string.IsNullOrWhiteSpace(_config.displayField) && fields.Contains(_config.displayField))
                {
                    _displayFieldCombo.SelectedItem = _config.displayField;
                }
                else if (fields.Any())
                {
                    _displayFieldCombo.SelectedIndex = 0;
                }

                // Create checkboxes for searchable fields
                foreach (var field in fields)
                {
                    var checkbox = new CheckBox
                    {
                        Content = field,
                        Margin = new Thickness(0, 2, 15, 2),
                        IsChecked = _config.searchFields?.Contains(field) == true
                    };
                    _fieldCheckBoxes.Add(checkbox);
                    _fieldsPanel.Children.Add(checkbox);
                }
            }
            else
            {
                // For custom layers without a bound layer, allow manual field entry
                var manualFieldsText = new TextBox
                {
                    Text = _config.searchFields != null ? string.Join(", ", _config.searchFields) : "",
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 300,
                    Margin = new Thickness(0, 5, 0, 0)
                };
                _fieldsPanel.Children.Add(new TextBlock { Text = "Enter field names (comma-separated):", Margin = new Thickness(0, 0, 0, 5) });
                _fieldsPanel.Children.Add(manualFieldsText);
            }

            UpdateUI();
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e)
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            bool isEnabled = _enabledCheckBox.IsChecked ?? false;
            _displayFieldCombo.IsEnabled = isEnabled;
            
            foreach (var checkbox in _fieldCheckBoxes)
            {
                checkbox.IsEnabled = isEnabled;
            }

            // Update the visual state
            this.Opacity = isEnabled ? 1.0 : 0.6;
        }

        private void DeleteLayer_Click(object sender, RoutedEventArgs e)
        {
            if (Parent is Panel parent)
            {
                parent.Children.Remove(this);
            }
        }

        public QueryLayerConfig? GetConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_layerNameTextBox.Text))
                return null;

            var config = new QueryLayerConfig
            {
                layerName = _layerNameTextBox.Text,
                enabled = _enabledCheckBox.IsChecked ?? false,
                displayField = _displayFieldCombo.SelectedItem?.ToString() ?? "",
                searchFields = new List<string>()
            };

            // Collect selected fields
            if (_layer?.FeatureTable?.Fields != null)
            {
                // From checkboxes
                foreach (var checkbox in _fieldCheckBoxes)
                {
                    if (checkbox.IsChecked == true && checkbox.Content is string fieldName)
                    {
                        config.searchFields.Add(fieldName);
                    }
                }
            }
            else
            {
                // From manual text input (for custom layers)
                var manualTextBox = _fieldsPanel.Children.OfType<TextBox>().FirstOrDefault();
                if (manualTextBox != null && !string.IsNullOrWhiteSpace(manualTextBox.Text))
                {
                    config.searchFields = manualTextBox.Text
                        .Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }
            }

            return config;
        }
    }

    // Configuration classes (moved here for completeness)
    public class Config
    {
        public string runtimeLicenseString { get; set; } = string.Empty;
        public string apiKey { get; set; } = string.Empty;
        public string posmExecutablePath { get; set; } = string.Empty;
        // Optional explicit web map ID for returning online
        public string webMapId { get; set; } = string.Empty;
        public string mapId { get; set; } = string.Empty;
        public string defaultBasemap { get; set; } = string.Empty;
        public string idField { get; set; } = "AssetID";
        public string inspectionType { get; set; } = string.Empty;
        public string selectedLayer { get; set; } = "ssGravityMain";
        
        // Enhanced offline and search configuration
        public bool offlineMode { get; set; } = false;
        public string offlineBasemapPath { get; set; } = string.Empty;
        public Dictionary<string, bool> layerVisibilities { get; set; } = new Dictionary<string, bool>();
        public List<QueryLayerConfig> queryLayers { get; set; } = new List<QueryLayerConfig>();
    }

    public class QueryLayerConfig
    {
        public string layerName { get; set; } = string.Empty;
        public List<string> searchFields { get; set; } = new List<string>();
        public string displayField { get; set; } = string.Empty;
        public bool enabled { get; set; } = true;
    }

    // Custom Inspection Type Input Dialog
    public partial class CustomInspectionTypeDialog : Window
    {
        public string CustomInspectionType { get; private set; } = string.Empty;

        public CustomInspectionTypeDialog()
        {
            Title = "Custom Inspection Type";
            Width = 400;
            Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            // Create the UI
            var mainStack = new StackPanel { Margin = new Thickness(20) };

            var titleBlock = new TextBlock 
            { 
                Text = "Enter Custom Inspection Type:", 
                FontSize = 14, 
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 15)
            };

            var textBox = new TextBox 
            { 
                Name = "txtCustomType",
                FontSize = 13,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 20)
            };

            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Right 
            };

            var okButton = new Button 
            { 
                Content = "OK", 
                Width = 75, 
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };

            var cancelButton = new Button 
            { 
                Content = "Cancel", 
                Width = 75, 
                Height = 30,
                IsCancel = true
            };

            okButton.Click += (s, e) => 
            {
                CustomInspectionType = textBox.Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(CustomInspectionType))
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("Please enter a valid inspection type.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            cancelButton.Click += (s, e) => 
            {
                DialogResult = false;
                Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            mainStack.Children.Add(titleBlock);
            mainStack.Children.Add(textBox);
            mainStack.Children.Add(buttonPanel);

            Content = mainStack;

            // Focus the textbox when loaded
            Loaded += (s, e) => textBox.Focus();
        }
    }
}
