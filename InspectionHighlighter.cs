using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;               
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using WpfMapApp1;

namespace POSM_MR3_2
{
    /// <summary>
    /// Highlights every feature in the user-selected GIS layer whose
    /// <see cref="App.Configuration.idField"/> matches an AssetID in
    /// POSM.mdb ▸ SpecialFields.
    /// </summary>
    public static class InspectionHighlighter
    {
        // ───────────────────────── Public entry point ─────────────────────────
        public static async Task ApplyInspectionGlowAsync(MapView mapView)
        {
            if (mapView?.Map == null) return;

            //------------------------------------------------------------------
            // 1.  Resolve layer + GIS field names from config.json
            //------------------------------------------------------------------
            string? layerName = App.Configuration?.selectedLayer;
            if (string.IsNullOrWhiteSpace(layerName)) return;

            FeatureLayer? layer = FindLayerRecursive(mapView.Map.OperationalLayers, layerName);
            if (layer == null)
            {
                ShowMsg($"Layer '{layerName}' not found in map.", "Layer not found");
                return;
            }

            // GIS side field (can be anything user mapped)
            string gisField = string.IsNullOrWhiteSpace(App.Configuration?.idField)
                                ? "AssetID"
                                : App.Configuration!.idField;

            System.Diagnostics.Debug.WriteLine($"[InspectionHighlighter] Layer   : {layerName}");
            System.Diagnostics.Debug.WriteLine($"[InspectionHighlighter] GIS fld : {gisField}");

            //------------------------------------------------------------------
            // 2.  Locate POSM.mdb next to POSM.exe (with fallback)
            //------------------------------------------------------------------
            string exePath = App.Configuration?.posmExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
                exePath = @"C:\POSM\POSM.exe";

            string mdbPath = exePath.Replace("POSM.exe", "POSM.mdb",
                                             StringComparison.OrdinalIgnoreCase);

            System.Diagnostics.Debug.WriteLine($"[InspectionHighlighter] MDB path: {mdbPath}");

            if (!System.IO.File.Exists(mdbPath))
            {
                ShowMsg($"POSM.mdb missing at\n{mdbPath}", "POSM.mdb missing");
                return;
            }

            //------------------------------------------------------------------
            // 3.  Read inspected IDs from MDB (field is ALWAYS “AssetID”)
            //------------------------------------------------------------------
            const string mdbField = "AssetID";

            List<string> ids;
            try { ids = ReadIdsFromMdb(mdbPath); }
            catch (Exception ex)
            {
                ShowMsg($"Error reading MDB:\n{ex.Message}", "MDB error");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[InspectionHighlighter] IDs read : {ids.Count}");

            if (ids.Count == 0)
            {
                ShowMsg($"No {mdbField} values found in MDB.", "Nothing to highlight");
                return;
            }

            //------------------------------------------------------------------
            // 4.  Select matched features in manageable chunks (glowing green)
            //------------------------------------------------------------------
            try
            {
                // Configure selection appearance (bright green)
                mapView.SelectionProperties.Color = System.Drawing.Color.Lime;

                // Clear previous selection for this layer
                layer.ClearSelection();

                int chunkSize = 500;
                for (int i = 0; i < ids.Count; i += chunkSize)
                {
                    var chunk = ids.Skip(i).Take(chunkSize).ToList();

                    var numericVals = new List<string>();
                    var stringVals = new List<string>();

                    foreach (var v in chunk)
                    {
                        var t = v?.Trim();
                        if (string.IsNullOrEmpty(t)) continue;

                        if (long.TryParse(t, out var li))
                        {
                            numericVals.Add(li.ToString());
                        }
                        else if (double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            numericVals.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            stringVals.Add($"'{t.Replace("'", "''")}'");
                        }
                    }

                    if (numericVals.Count > 0)
                    {
                        string inNums = string.Join(",", numericVals);
                        try
                        {
                            var qpNum = new QueryParameters { WhereClause = $"{gisField} IN ({inNums})" };
                            await layer.SelectFeaturesAsync(qpNum, SelectionMode.Add);
                        }
                        catch
                        {
                            // Fallback: treat numerics as strings
                            var quotedNums = string.Join(",", numericVals.Select(n => $"'{n.Replace("'", "''")}'"));
                            if (!string.IsNullOrWhiteSpace(quotedNums))
                            {
                                var qpNumAsStr = new QueryParameters { WhereClause = $"{gisField} IN ({quotedNums})" };
                                await layer.SelectFeaturesAsync(qpNumAsStr, SelectionMode.Add);
                            }
                        }
                    }

                    if (stringVals.Count > 0)
                    {
                        string inStrs = string.Join(",", stringVals);
                        var qpStr = new QueryParameters { WhereClause = $"{gisField} IN ({inStrs})" };
                        await layer.SelectFeaturesAsync(qpStr, SelectionMode.Add);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InspectionHighlighter] Error selecting features: {ex.Message}");
            }

        }

        // ───────────────────────── Helpers ─────────────────────────
        private static Symbol SymbolForGeometry(GeometryType gType) =>
            gType switch
            {
                GeometryType.Polyline => new SimpleLineSymbol(
                         SimpleLineSymbolStyle.Solid, Color.Yellow, 6),
                _ /* point, polygon, etc. */ => new SimpleMarkerSymbol(
                         SimpleMarkerSymbolStyle.Circle, Color.Yellow, 8)
            };

        // Legacy overlay approach retained for reference (not used now)
        private static GraphicsOverlay PrepareOverlay(MapView mv)
        {
            const string id = "InspectedGlow";
            var existing = mv.GraphicsOverlays.FirstOrDefault(o => o.Id == id);
            if (existing != null) mv.GraphicsOverlays.Remove(existing);

            var ov = new GraphicsOverlay { Id = id };
            mv.GraphicsOverlays.Add(ov);
            return ov;
        }

        private static List<string> ReadIdsFromMdb(string mdb)
        {
            const string sql = "SELECT [AssetID] FROM SpecialFields";

            foreach (var provider in new[] { "Microsoft.ACE.OLEDB.12.0", "Microsoft.ACE.OLEDB.16.0" })
            {
                try
                {
                    var list = new List<string>();
                    string conn = $"Provider={provider};Data Source={mdb};";

                    using OleDbConnection c = new OleDbConnection(conn);
                    c.Open();
                    using OleDbCommand cmd = new OleDbCommand(sql, c);
                    using OleDbDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var v = r[0]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
                    }
                    return list;
                }
                catch (OleDbException)
                {
                    // try next provider
                }
            }

            throw new InvalidOperationException("ACE OLEDB provider not available (tried 12.0, 16.0).");
        }

        private static FeatureLayer? FindLayerRecursive(IEnumerable<Layer> layers, string name)
        {
            foreach (Layer l in layers)
            {
                if (l is FeatureLayer fl &&
                    fl.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return fl;

                if (l is GroupLayer gl)
                {
                    FeatureLayer? nested = FindLayerRecursive(gl.Layers, name);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static void ShowMsg(string msg, string caption) =>
            System.Windows.MessageBox.Show(msg, caption,
                                           System.Windows.MessageBoxButton.OK,
                                           System.Windows.MessageBoxImage.Information);
    }
}
