#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Portal;

namespace WpfMapApp1
{
    public partial class AppendToPosmDbWindow : Window
    {
        private readonly Map _map;
        private readonly string _dbFullPath;
        private readonly MappingProfileManager _profiles;

        private FeatureLayer? _currentLayer;

        public ObservableCollection<string> DbFields { get; } = new();
        public List<FieldMapping> FieldMappings { get; private set; } = new();

        public AppendToPosmDbWindow(Map map)
        {
            InitializeComponent();

            _map = map;
            _dbFullPath = GetDatabasePath();
            _profiles = new MappingProfileManager(App.Configuration!.posmExecutablePath);

            LoadFeatureLayersIntoCombo();
            DataContext = this;
        }

        /// <summary>
        /// Retrieves features with every attribute field loaded,
        /// whether coming from a local geodatabase or an ArcGIS Online service.
        /// </summary>
        private async Task<IReadOnlyList<Feature>> QueryAllAttributesAsync(
            FeatureLayer fl,
            QueryParameters qp)
        {
            if (fl.FeatureTable is ServiceFeatureTable sft)
            {
                await sft.LoadAsync();
                var result = await sft.QueryFeaturesAsync(
                    qp,
                    QueryFeatureFields.LoadAll
                );
                return result.ToList();
            }
            // MMPK/local returns all by default
            return (await fl.FeatureTable.QueryFeaturesAsync(qp)).ToList();
        }

        private string GetDatabasePath()
        {
            var posmExe = App.Configuration!.posmExecutablePath;
            var dir = Path.GetDirectoryName(posmExe)!;
            return Path.Combine(dir, "POSMGISData.mdb");
        }

        private string BuildProfileKey(FeatureLayer layer)
        {
            var prefix = _map.Item is PortalItem item
                ? $"WebMap:{item.ItemId}"
                : $"MMPK:{_map.OperationalLayers.FirstOrDefault()?.Name ?? "Unknown"}";
            return $"{prefix}|Layer:{layer.Name}";
        }

        private void LoadFeatureLayersIntoCombo()
        {
            var flat = new List<FeatureLayer>();
            void Recurse(IEnumerable<Layer> layers)
            {
                foreach (var lyr in layers)
                {
                    if (lyr is FeatureLayer fl)
                    {
                        if (string.IsNullOrWhiteSpace(fl.Name))
                            fl.Name = fl.FeatureTable?.DisplayName ?? "Untitled Layer";
                        flat.Add(fl);
                    }
                    else if (lyr is GroupLayer gl)
                        Recurse(gl.Layers);
                }
            }

            Recurse(_map.OperationalLayers);
            LayerComboBox.ItemsSource = flat;
            if (flat.Any()) LayerComboBox.SelectedIndex = 0;
        }

        private async void LayerComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (LayerComboBox.SelectedItem is not FeatureLayer fl)
                return;

            _currentLayer = fl;
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                var qp = new QueryParameters { WhereClause = "1=1", MaxFeatures = 1 };
                var feats = await QueryAllAttributesAsync(fl, qp);
                var feat = feats.FirstOrDefault() as ArcGISFeature;
                if (feat == null)
                {
                    SampleDataGrid.ItemsSource = null;
                    return;
                }

                // build DataTable of attributes
                var dt = new DataTable("SampleData");
                foreach (var key in feat.Attributes.Keys)
                    dt.Columns.Add(key);
                var row = dt.NewRow();
                foreach (var kvp in feat.Attributes)
                    row[kvp.Key] = kvp.Value ?? DBNull.Value;
                dt.Rows.Add(row);

                SampleDataGrid.AutoGenerateColumns = true;
                SampleDataGrid.ItemsSource = dt.DefaultView;

                FieldMappings = feat.Attributes.Keys
                                 .Select(k => new FieldMapping { LayerField = k })
                                 .ToList();
                FieldMappingGrid.ItemsSource = FieldMappings;

                LoadDatabaseFields();

                var saved = _profiles.TryGet(BuildProfileKey(fl));
                if (saved != null)
                {
                    foreach (var fm in FieldMappings)
                    {
                        var hit = saved.FirstOrDefault(kv =>
                                   kv.Value.Equals(fm.LayerField, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(hit.Key))
                            fm.MappedField = hit.Key;
                    }
                    FieldMappingGrid.Items.Refresh();
                }
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void LoadDatabaseFields()
        {
            DbFields.Clear();
            DbFields.Add(string.Empty);
            if (!File.Exists(_dbFullPath)) return;

            var conn = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={_dbFullPath};";
            using var c = new OleDbConnection(conn);
            c.Open();
            foreach (DataRow r in c.GetSchema("Columns", new[] { null!, null!, "POSMGIS" }).Rows)
                if (r["COLUMN_NAME"] is string name)
                    DbFields.Add(name);
        }

        private async void AppendButton_Click(object sender, RoutedEventArgs e)
        {
            AppendProgressBar.Visibility = Visibility.Visible;
            AppendButton.IsEnabled = false;
            AppendProgressBar.Value = 0;

            try
            {
                if (LayerComboBox.SelectedItem is not FeatureLayer fl)
                    return;

                var feats = (await QueryAllAttributesAsync(
                    fl, new QueryParameters { WhereClause = "1=1" }))
                               .ToList();
                if (!feats.Any())
                {
                    MessageBox.Show("No features.");
                    return;
                }

                AppendProgressBar.Maximum = feats.Count;
                var connString = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={_dbFullPath};";
                using var db = new OleDbConnection(connString);
                db.Open();

                // Clear existing rows
                using (var delCmd = new OleDbCommand("DELETE FROM POSMGIS", db))
                    delCmd.ExecuteNonQuery();

                // Build list of mapped fields once
                var mapped = FieldMappings
                                .Where(m => !string.IsNullOrWhiteSpace(m.MappedField))
                                .ToList();

                foreach (var feat in feats)
                {
                    // skip if no mappings
                    if (!mapped.Any()) break;

                    // build INSERT command
                    string cols = string.Join(", ", mapped.Select(m => $"[{m.MappedField}]"));
                    string parms = string.Join(", ", mapped.Select(_ => "?"));
                    using var cmd = new OleDbCommand(
                        $"INSERT INTO POSMGIS ({cols}) VALUES ({parms})",
                        db);

                    // add each parameter, truncating floats/doubles/decimals
                    foreach (var m in mapped)
                    {
                        object paramValue;

                        if (feat.Attributes.TryGetValue(m.LayerField, out var rawValue)
                            && rawValue is not null
                            && rawValue != DBNull.Value)
                        {
                            switch (rawValue)
                            {
                                case double dv:
                                    dv = Math.Truncate(dv * 100) / 100;
                                    paramValue = dv;
                                    break;
                                case float fv:
                                    fv = (float)(Math.Truncate(fv * 100) / 100);
                                    paramValue = fv;
                                    break;
                                case decimal decv:
                                    decv = Math.Truncate(decv * 100) / 100;
                                    paramValue = decv;
                                    break;
                                default:
                                    paramValue = rawValue;
                                    break;
                            }
                        }
                        else
                        {
                            paramValue = DBNull.Value;
                        }

                        cmd.Parameters.AddWithValue($"@{m.MappedField}", paramValue);
                    }

                    // execute and advance progress
                    cmd.ExecuteNonQuery();
                    AppendProgressBar.Value++;
                    await Task.Delay(1);
                }

                MessageBox.Show("Data appended OK!");
                SaveMapping();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
            finally
            {
                AppendProgressBar.Visibility = Visibility.Collapsed;
                AppendButton.IsEnabled = true;
            }
        }


        private void SaveMapping()
        {
            if (_currentLayer == null) return;

            var dict = FieldMappings
                       .Where(m => !string.IsNullOrWhiteSpace(m.MappedField))
                       .ToDictionary(m => m.MappedField!, m => m.LayerField);

            if (dict.Any())
                _profiles.Save(BuildProfileKey(_currentLayer), dict);
        }

        private void POSMGISFieldComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is FieldMapping fm)
                fm.MappedField = cb.SelectedItem as string;
        }

        public sealed class FieldMapping : INotifyPropertyChanged
        {
            private string _layerField = string.Empty;
            private string? _mappedField;

            public string LayerField
            {
                get => _layerField;
                set
                {
                    if (_layerField != value)
                    {
                        _layerField = value;
                        Notify(nameof(LayerField));
                    }
                }
            }

            public string? MappedField
            {
                get => _mappedField;
                set
                {
                    if (_mappedField != value)
                    {
                        _mappedField = value;
                        Notify(nameof(MappedField));
                    }
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Notify(string prop) => PropertyChanged?.Invoke(this, new(prop));
        }
    }

    internal sealed class MappingProfileManager
    {
        private readonly string _iniPath;
        private readonly Dictionary<string, Dictionary<string, string>> _cache;

        public MappingProfileManager(string posmExePath)
        {
            _iniPath = Path.Combine(Path.GetDirectoryName(posmExePath)!, "MappingProfiles.ini");
            _cache = File.Exists(_iniPath)
                ? ParseIni(File.ReadAllLines(_iniPath))
                : new(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, string>? TryGet(string key) =>
            _cache.TryGetValue(key, out var m) ? m : null;

        public void Save(string key, IReadOnlyDictionary<string, string> map)
        {
            _cache[key] = new(map, StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(_iniPath, SerializeIni(_cache));
        }

        private static Dictionary<string, Dictionary<string, string>> ParseIni(IEnumerable<string> lines)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? section = null;

            foreach (var raw in lines.Select(l => l.Trim()))
            {
                if (raw.Length == 0 || raw.StartsWith(';')) continue;
                if (raw.StartsWith('[') && raw.EndsWith(']'))
                {
                    section = new(StringComparer.OrdinalIgnoreCase);
                    result[raw[1..^1]] = section;
                }
                else if (section != null && raw.Contains('='))
                {
                    var idx = raw.IndexOf('=');
                    section[raw[..idx].Trim()] = raw[(idx + 1)..].Trim();
                }
            }

            return result;
        }

        private static IEnumerable<string> SerializeIni(Dictionary<string, Dictionary<string, string>> data)
        {
            foreach (var (section, kv) in data)
            {
                yield return $"[{section}]";
                foreach (var (k, v) in kv) yield return $"{k} = {v}";
                yield return "";
            }
        }
    }
}
