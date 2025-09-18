using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Data;

namespace WpfMapApp1
{
    /// <summary>
    /// Provides safe font fallbacks for both WPF UI elements and ArcGIS Runtime symbols
    /// to prevent rendering errors when fonts are not available or have issues.
    /// </summary>
    public static class FontFallbackHelper
    {
        // Problematic → safe web font mappings used in label JSON
        private static readonly Dictionary<string, string> WebFontMapping = new(StringComparer.OrdinalIgnoreCase)
        {
            { "tahoma-bold", "Arial Bold" },
            { "calibri-bold", "Arial Bold" },
            { "times-new-roman-regular", "Times New Roman" },
            { "segoe-ui", "Arial" },
        };

        // Safe font families that are broadly available on Windows
        private static readonly List<string> SafeFontFallbacks = new()
        {
            "Segoe UI",
            "Arial",
            "Tahoma",
            "Verdana",
            "Microsoft Sans Serif",
            "Times New Roman",
            "Courier New"
        };

        private const string DefaultSafeFontFamily = "Arial";
        private const double DefaultSafeFontSize = 12.0;
        private static readonly System.Windows.FontWeight DefaultSafeFontWeight = System.Windows.FontWeights.Normal;

        #region WPF Font Fallback Methods
        public static System.Windows.Media.FontFamily GetSafeFontFamily(string? requestedFont = null)
        {
            if (!string.IsNullOrWhiteSpace(requestedFont))
            {
                try
                {
                    var requestedFamily = new System.Windows.Media.FontFamily(requestedFont);
                    if (requestedFamily.GetTypefaces().GetEnumerator().MoveNext())
                        return requestedFamily;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FontFallback] Requested font '{requestedFont}' failed: {ex.Message}");
                }
            }

            foreach (var safeFontName in SafeFontFallbacks)
            {
                try
                {
                    var fallbackFamily = new System.Windows.Media.FontFamily(safeFontName);
                    if (fallbackFamily.GetTypefaces().GetEnumerator().MoveNext())
                    {
                        Debug.WriteLine($"[FontFallback] Using fallback font: {safeFontName}");
                        return fallbackFamily;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FontFallback] Fallback font '{safeFontName}' failed: {ex.Message}");
                }
            }

            Debug.WriteLine("[FontFallback] Using system default font family");
            return SystemFonts.MessageFontFamily;
        }

        public static void ApplySafeFont(FrameworkElement element,
                                          string? requestedFontFamily = null,
                                          double? fontSize = null,
                                          System.Windows.FontWeight? fontWeight = null)
        {
            try
            {
                if (element is System.Windows.Controls.Control control)
                {
                    control.FontFamily = GetSafeFontFamily(requestedFontFamily);
                    control.FontSize = Math.Max(fontSize ?? DefaultSafeFontSize, 6.0);
                    control.FontWeight = fontWeight ?? DefaultSafeFontWeight;
                }
                else if (element is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.FontFamily = GetSafeFontFamily(requestedFontFamily);
                    textBlock.FontSize = Math.Max(fontSize ?? DefaultSafeFontSize, 6.0);
                    textBlock.FontWeight = fontWeight ?? DefaultSafeFontWeight;
                }
                else
                {
                    Debug.WriteLine($"[FontFallback] Element type {element.GetType().Name} does not support font properties");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FontFallback] Error applying safe font to element: {ex.Message}");
                try
                {
                    if (element is System.Windows.Controls.Control control)
                    {
                        control.FontFamily = SystemFonts.MessageFontFamily;
                        control.FontSize = DefaultSafeFontSize;
                        control.FontWeight = DefaultSafeFontWeight;
                    }
                    else if (element is System.Windows.Controls.TextBlock textBlock)
                    {
                        textBlock.FontFamily = SystemFonts.MessageFontFamily;
                        textBlock.FontSize = DefaultSafeFontSize;
                        textBlock.FontWeight = DefaultSafeFontWeight;
                    }
                }
                catch { /* ignore */ }
            }
        }
        #endregion

        #region ArcGIS Runtime Label Fallback
        public static string SanitizeWebFont(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DefaultSafeFontFamily;
            if (WebFontMapping.TryGetValue(name.Trim(), out var mapped)) return mapped;
            return name;
        }

        public static string TestWebFontSanitization(string name)
        {
            var result = SanitizeWebFont(name);
            Debug.WriteLine($"[FontFallback] Test sanitize '{name}' => '{result}'");
            return result;
        }

        public static void LogWebFontMappings()
        {
            foreach (var kv in WebFontMapping)
                Debug.WriteLine($"[FontFallback] map '{kv.Key}' => '{kv.Value}'");
        }

        /// <summary>
        /// Attempts to force label fonts to safe choices by rewriting label definitions via JSON.
        /// </summary>
        public static async Task ForceAllLabelsToDisplayAsync(Map? map)
        {
            if (map == null) return;
            foreach (var layer in map.OperationalLayers.OfType<FeatureLayer>())
            {
                try
                {
                    if (layer.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                        await layer.LoadAsync();

                    if (layer.LabelDefinitions == null || layer.LabelDefinitions.Count == 0)
                        continue;

                    var updated = new List<LabelDefinition>();
                    foreach (var def in layer.LabelDefinitions)
                    {
                        try
                        {
                            var json = def.ToJson();
                            // Replace problematic web fonts in the JSON payload
                            foreach (var kv in WebFontMapping)
                            {
                                if (json.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    json = json.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
                                    Debug.WriteLine($"[FontFallback] LabelDef font '{kv.Key}' => '{kv.Value}' on layer '{layer.Name}'");
                                }
                            }
                            var newDef = LabelDefinition.FromJson(json);
                            updated.Add(newDef);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[FontFallback] Could not rewrite label definition on '{layer.Name}': {ex.Message}");
                            updated.Add(def);
                        }
                    }
                    layer.LabelDefinitions.Clear();
                    foreach (var d in updated) layer.LabelDefinitions.Add(d);
                    layer.LabelsEnabled = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FontFallback] Error processing layer '{layer.Name}': {ex.Message}");
                }
            }
        }
        #endregion

        #region Layer Labeling Safety Methods
        /// <summary>
        /// Applies safe font fallbacks to all layers in a map to prevent label rendering errors.
        /// More aggressive on WebMaps.
        /// </summary>
        public static async Task MakeMapLabelingSafe(Map? map)
        {
            if (map == null || map.OperationalLayers == null) return;
            bool isWebMap = IsWebMap(map);
            string mapType = isWebMap ? "Web Map" : "Offline Map";
            Debug.WriteLine($"[FontFallback] ===== Making {mapType} Labeling Safe =====");
            Debug.WriteLine($"[FontFallback] Processing {map.OperationalLayers.Count} operational layers");

            if (isWebMap)
            {
                Debug.WriteLine("[FontFallback]   WEB MAP DETECTED - Applying aggressive font safety measures");
                await ApplyWebMapFontSanitization(map);
            }

            await MakeLayerCollectionSafe(map.OperationalLayers, isWebMap);
            Debug.WriteLine($"[FontFallback] ===== {mapType} Labeling Safety Complete =====");
        }

        /// <summary>
        /// Detects if a map is a web map (more prone to font issues than MMPK files).
        /// </summary>
        private static bool IsWebMap(Map map)
        {
            try
            {
                if (map.Item != null) return true;

                var baseLayers = map.Basemap?.BaseLayers;
                if (baseLayers != null)
                {
                    foreach (var baseLayer in baseLayers)
                    {
                        if (baseLayer is ArcGISMapImageLayer mil)
                        {
                            var url = mil.Source?.ToString();
                            if (!string.IsNullOrEmpty(url) && (url.Contains("arcgisonline.com") || url.Contains("arcgis.com") || url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                                return true;
                        }
                        else if (baseLayer is ArcGISTiledLayer tl)
                        {
                            var url = tl.Source?.ToString();
                            if (!string.IsNullOrEmpty(url) && (url.Contains("arcgisonline.com") || url.Contains("arcgis.com") || url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                                return true;
                        }
                    }
                }

                foreach (var layer in map.OperationalLayers)
                {
                    if (layer is FeatureLayer fl && fl.FeatureTable is Esri.ArcGISRuntime.Data.ServiceFeatureTable sft)
                    {
                        var url = sft.Source?.ToString();
                        if (!string.IsNullOrEmpty(url) && (url.Contains("arcgisonline.com") || url.Contains("arcgis.com") || url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FontFallback] Error detecting web map: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Recursively processes a layer collection to make labels safer.
        /// </summary>
        private static async Task MakeLayerCollectionSafe(IEnumerable<Layer> layers, bool isWebMap)
        {
            foreach (var layer in layers)
            {
                try
                {
                    if (layer is GroupLayer gl)
                    {
                        await MakeLayerCollectionSafe(gl.Layers, isWebMap);
                        continue;
                    }

                    if (layer is FeatureLayer fl)
                    {
                        if (fl.LoadStatus != Esri.ArcGISRuntime.LoadStatus.Loaded)
                            await fl.LoadAsync();

                        if (fl.LabelDefinitions == null || fl.LabelDefinitions.Count == 0)
                            continue;

                        var updated = new List<LabelDefinition>();
                        foreach (var def in fl.LabelDefinitions)
                        {
                            try
                            {
                                var json = def.ToJson();
                                foreach (var kv in WebFontMapping)
                                {
                                    if (json.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        json = json.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
                                        Debug.WriteLine($"[FontFallback] LabelDef font '{kv.Key}' => '{kv.Value}' on layer '{fl.Name}'");
                                    }
                                }
                                var newDef = LabelDefinition.FromJson(json);
                                updated.Add(newDef);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[FontFallback] Could not rewrite label definition on '{fl.Name}': {ex.Message}");
                                updated.Add(def);
                            }
                        }
                        fl.LabelDefinitions.Clear();
                        foreach (var d in updated) fl.LabelDefinitions.Add(d);
                        fl.LabelsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FontFallback] Error processing layer '{layer.Name}': {ex.Message}");
                }
            }
        }

        private static Task ApplyWebMapFontSanitization(Map map)
        {
            // Reserved for advanced pre‑sanitization
            return Task.CompletedTask;
        }
        #endregion

        #region ArcGIS Runtime Font Fallback Methods (TextSymbol)
        /// <summary>
        /// Creates a safe TextSymbol with font fallbacks for ArcGIS Runtime.
        /// </summary>
        public static TextSymbol CreateSafeTextSymbol(
            string text,
            System.Drawing.Color? color = null,
            double fontSize = 12.0,
            string? requestedFontFamily = null,
            Esri.ArcGISRuntime.Symbology.FontStyle fontStyle = Esri.ArcGISRuntime.Symbology.FontStyle.Normal,
            Esri.ArcGISRuntime.Symbology.FontWeight fontWeight = Esri.ArcGISRuntime.Symbology.FontWeight.Normal)
        {
            try
            {
                string safeFontName = GetSafeFontName(requestedFontFamily);
                return new TextSymbol
                {
                    Text = text ?? string.Empty,
                    Color = color ?? System.Drawing.Color.Black,
                    Size = Math.Max(fontSize, 6.0),
                    FontFamily = safeFontName,
                    FontStyle = fontStyle,
                    FontWeight = fontWeight,
                    HaloColor = System.Drawing.Color.White,
                    HaloWidth = 1.0
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FontFallback] Error creating TextSymbol, using minimal fallback: {ex.Message}");
                return new TextSymbol
                {
                    Text = text ?? string.Empty,
                    Color = System.Drawing.Color.Black,
                    Size = 12.0,
                    FontFamily = DefaultSafeFontFamily,
                    FontStyle = Esri.ArcGISRuntime.Symbology.FontStyle.Normal,
                    FontWeight = Esri.ArcGISRuntime.Symbology.FontWeight.Normal
                };
            }
        }

        /// <summary>
        /// Gets a safe font name for ArcGIS Runtime text symbols.
        /// </summary>
        public static string GetSafeFontName(string? requestedFont = null)
        {
            if (!string.IsNullOrWhiteSpace(requestedFont))
            {
                try
                {
                    var testFamily = new System.Windows.Media.FontFamily(requestedFont);
                    if (testFamily.GetTypefaces().GetEnumerator().MoveNext())
                        return requestedFont;
                }
                catch
                {
                    Debug.WriteLine($"[FontFallback] Requested font name '{requestedFont}' not available");
                }
            }

            foreach (var safeFontName in SafeFontFallbacks)
            {
                try
                {
                    var testFamily = new System.Windows.Media.FontFamily(safeFontName);
                    if (testFamily.GetTypefaces().GetEnumerator().MoveNext())
                        return safeFontName;
                }
                catch { }
            }

            return DefaultSafeFontFamily;
        }

        /// <summary>
        /// Safely clones a TextSymbol with fallback font family.
        /// </summary>
        public static TextSymbol MakeSafeTextSymbol(TextSymbol originalSymbol)
        {
            if (originalSymbol == null)
                return CreateSafeTextSymbol(string.Empty, System.Drawing.Color.Black, 12.0);

            try
            {
                return new TextSymbol
                {
                    Text = originalSymbol.Text,
                    Color = originalSymbol.Color,
                    Size = Math.Max(originalSymbol.Size, 6.0),
                    FontFamily = GetSafeFontName(originalSymbol.FontFamily),
                    FontStyle = originalSymbol.FontStyle,
                    FontWeight = originalSymbol.FontWeight,
                    HaloColor = originalSymbol.HaloColor,
                    HaloWidth = Math.Max(originalSymbol.HaloWidth, 0.5),
                    HorizontalAlignment = originalSymbol.HorizontalAlignment,
                    VerticalAlignment = originalSymbol.VerticalAlignment,
                    OffsetX = originalSymbol.OffsetX,
                    OffsetY = originalSymbol.OffsetY
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FontFallback] Error making TextSymbol safe, creating new: {ex.Message}");
                return CreateSafeTextSymbol(originalSymbol.Text, originalSymbol.Color, originalSymbol.Size);
            }
        }

        /// <summary>
        /// Creates a TextSymbol optimized for web maps by sanitizing the font name using the web font map.
        /// </summary>
        public static TextSymbol CreateAggressivelySafeTextSymbol(TextSymbol originalSymbol)
        {
            if (originalSymbol == null)
                return CreateSafeTextSymbol(string.Empty, System.Drawing.Color.Black, 12.0);

            try
            {
                string webSafeFont = SanitizeWebFont(originalSymbol.FontFamily);
                Debug.WriteLine($"[FontFallback] Creating aggressively safe TextSymbol: '{originalSymbol.FontFamily}' => '{webSafeFont}'");
                return new TextSymbol
                {
                    Text = originalSymbol.Text,
                    Color = originalSymbol.Color,
                    Size = Math.Max(originalSymbol.Size, 8.0),
                    FontFamily = webSafeFont,
                    FontStyle = GetSafeWebFontStyle(originalSymbol.FontStyle),
                    FontWeight = GetSafeWebFontWeight(originalSymbol.FontWeight),
                    HaloColor = originalSymbol.HaloColor.IsEmpty ? System.Drawing.Color.White : originalSymbol.HaloColor,
                    HaloWidth = Math.Max(originalSymbol.HaloWidth, 1.0),
                    HorizontalAlignment = originalSymbol.HorizontalAlignment,
                    VerticalAlignment = originalSymbol.VerticalAlignment,
                    OffsetX = originalSymbol.OffsetX,
                    OffsetY = originalSymbol.OffsetY
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FontFallback] Error creating aggressively safe TextSymbol: {ex.Message}");
                return new TextSymbol
                {
                    Text = originalSymbol?.Text ?? string.Empty,
                    Color = System.Drawing.Color.Black,
                    Size = 10.0,
                    FontFamily = "Arial",
                    FontStyle = Esri.ArcGISRuntime.Symbology.FontStyle.Normal,
                    FontWeight = Esri.ArcGISRuntime.Symbology.FontWeight.Normal,
                    HaloColor = System.Drawing.Color.White,
                    HaloWidth = 1.0
                };
            }
        }

        private static Esri.ArcGISRuntime.Symbology.FontStyle GetSafeWebFontStyle(Esri.ArcGISRuntime.Symbology.FontStyle style)
            => style;

        private static Esri.ArcGISRuntime.Symbology.FontWeight GetSafeWebFontWeight(Esri.ArcGISRuntime.Symbology.FontWeight weight)
            => weight;
        #endregion
    }
}


