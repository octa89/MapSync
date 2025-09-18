using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;

namespace POSM_MR3_2
{
    /// <summary>
    /// Manages inspection image graphics overlay for displaying images along asset lines
    /// </summary>
    public class InspectionImageOverlay
    {
        // Win32 API for accurate cursor position
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);
        
        private readonly MapView _mapView;
        private readonly PosmDatabaseService _dbService;
        private GraphicsOverlay? _imageOverlay;
        private const string ImageOverlayId = "InspectionImages";
        
        // Image popup window for hover display
        private Window? _imagePopupWindow;  // For clicked images
        private Image? _imagePopupContent;
        private Window? _hoverPopupWindow;  // Separate window for hover images
        private Image? _hoverPopupContent;
        private System.Threading.Timer? _hoverCloseTimer;  // Timer for stable hover

        public InspectionImageOverlay(MapView mapView, PosmDatabaseService dbService)
        {
            _mapView = mapView ?? throw new ArgumentNullException(nameof(mapView));
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            
            InitializeOverlay();
            SetupMapInteraction();
        }

        private void InitializeOverlay()
        {
            // Remove existing overlay if present
            var existingOverlay = _mapView.GraphicsOverlays.FirstOrDefault(o => o.Id == ImageOverlayId);
            if (existingOverlay != null)
                _mapView.GraphicsOverlays.Remove(existingOverlay);

            // Create new overlay configured to render on top of everything
            _imageOverlay = new GraphicsOverlay
            {
                Id = ImageOverlayId,
                IsVisible = true,
                RenderingMode = GraphicsRenderingMode.Dynamic, // Ensures it renders on top
                Opacity = 1.0, // Full opacity to make pins clearly visible
                ScaleSymbols = false // Prevent symbols from scaling with zoom
            };

            // Add overlay to the END of the collection so it renders on top of all other overlays
            _mapView.GraphicsOverlays.Add(_imageOverlay);

            // Force z-index to highest value
            MoveOverlayToTop();

            Console.WriteLine($"[POSM IMAGE DEBUG] Inspection image overlay initialized on top with z-index management. Total overlays: {_mapView.GraphicsOverlays.Count}");
        }

        private void SetupMapInteraction()
        {
            _mapView.GeoViewTapped += OnMapTapped;
            _mapView.MouseMove += OnMapMouseMove;
            _mapView.MouseLeave += OnMapMouseLeave;
            Console.WriteLine("[POSM IMAGE DEBUG] Map interaction setup complete");
        }

        public async Task DisplayImagesForAssetAsync(string assetId, Feature feature)
        {
            await AddImagesForAssetAsync(assetId, feature, clearExisting: true);
            EnsureOverlayOnTop();
        }
        
        /// <summary>
        /// Ensures the image overlay is always rendered on top of all other overlays
        /// </summary>
        private void EnsureOverlayOnTop()
        {
            MoveOverlayToTop();
        }

        /// <summary>
        /// Forcefully moves the overlay to the top of the rendering stack
        /// </summary>
        private void MoveOverlayToTop()
        {
            if (_imageOverlay != null && _mapView.GraphicsOverlays.Contains(_imageOverlay))
            {
                // Store current index
                var currentIndex = _mapView.GraphicsOverlays.IndexOf(_imageOverlay);
                var totalCount = _mapView.GraphicsOverlays.Count;

                // Only move if not already at the end (top)
                if (currentIndex != totalCount - 1)
                {
                    // Remove and re-add to move to end of collection (renders on top)
                    _mapView.GraphicsOverlays.Remove(_imageOverlay);
                    _mapView.GraphicsOverlays.Add(_imageOverlay);
                    Console.WriteLine($"[POSM IMAGE DEBUG] Moved image overlay from index {currentIndex} to top position {totalCount - 1}");
                }
            }
        }
        
        public async Task AddImagesForAssetAsync(string assetId, Feature feature, bool clearExisting = false)
        {
            Console.WriteLine($"[POSM IMAGE DEBUG] ============================================");
            Console.WriteLine($"[POSM IMAGE DEBUG] === AddImagesForAssetAsync START ===");
            Console.WriteLine($"[POSM IMAGE DEBUG] Asset ID: '{assetId}'");
            Console.WriteLine($"[POSM IMAGE DEBUG] Feature is null: {feature == null}");
            Console.WriteLine($"[POSM IMAGE DEBUG] ClearExisting: {clearExisting}");
            Console.WriteLine($"[POSM IMAGE DEBUG] ============================================");
            
            if (_imageOverlay == null)
            {
                Console.WriteLine("[POSM IMAGE DEBUG] CRITICAL: Image overlay is null - cannot display images");
                return;
            }

            Console.WriteLine($"[POSM IMAGE DEBUG] Image overlay is initialized - current graphics count: {_imageOverlay.Graphics.Count}");

            // Clear existing images only if requested (for backwards compatibility)
            if (clearExisting)
            {
                var beforeCount = _imageOverlay.Graphics.Count;
                _imageOverlay.Graphics.Clear();
                Console.WriteLine($"[POSM IMAGE DEBUG] Cleared {beforeCount} existing graphics from overlay");
            }

            if (feature?.Geometry is not Polyline assetLine)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Feature geometry check failed:");
                Console.WriteLine($"[POSM IMAGE DEBUG]   - feature is null: {feature == null}");
                Console.WriteLine($"[POSM IMAGE DEBUG]   - feature.Geometry is null: {feature?.Geometry == null}");
                Console.WriteLine($"[POSM IMAGE DEBUG]   - geometry type: {feature?.Geometry?.GetType().Name ?? "null"}");
                Console.WriteLine("[POSM IMAGE DEBUG] Cannot position images without polyline geometry");
                return;
            }

            Console.WriteLine($"[POSM IMAGE DEBUG] Feature geometry is valid Polyline with {assetLine.Parts.Count} parts");

            try
            {
                // Get image data from database
                Console.WriteLine($"[POSM IMAGE DEBUG] Calling database service GetImagePathsForAssetAsync for '{assetId}'");
                var images = await _dbService.GetImagePathsForAssetAsync(assetId);
                Console.WriteLine($"[POSM IMAGE DEBUG] Database returned {images.Count} image records");

                // Log first few image records for debugging
                foreach (var img in images.Take(3))
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Image record: FilePath='{img.FilePath}', Distance={img.Distance}, PictureLocation='{img.PictureLocation}'");
                }

                var existingImages = images.Where(img => File.Exists(img.FilePath)).ToList();
                Console.WriteLine($"[POSM IMAGE DEBUG] {existingImages.Count} images exist on disk out of {images.Count} total records");

                // Log missing files
                var missingImages = images.Where(img => !File.Exists(img.FilePath)).ToList();
                foreach (var missing in missingImages.Take(3))
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Missing file: '{missing.FilePath}'");
                }

                if (!existingImages.Any())
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] No existing images found for asset {assetId} - no pins will be created");
                    return;
                }

                Console.WriteLine($"[POSM IMAGE DEBUG] Starting to create {existingImages.Count} image graphics");

                // Calculate positions along the line for each image
                int successCount = 0;
                int failCount = 0;
                
                foreach (var imageInfo in existingImages)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Processing image: '{imageInfo.PictureLocation}' at distance {imageInfo.Distance:F2}");
                    
                    var position = CalculatePositionAlongLine(assetLine, imageInfo.Distance);
                    if (position != null)
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Calculated position: X={position.X:F6}, Y={position.Y:F6}");
                        CreateImageGraphic(position, imageInfo);
                        successCount++;
                        Console.WriteLine($"[POSM IMAGE DEBUG] SUCCESS: Created image graphic #{successCount} at distance {imageInfo.Distance:F2}: {imageInfo.PictureLocation}");
                    }
                    else
                    {
                        failCount++;
                        Console.WriteLine($"[POSM IMAGE DEBUG] FAILED: Could not calculate position for distance {imageInfo.Distance:F2}");
                    }
                }

                Console.WriteLine($"[POSM IMAGE DEBUG] Image processing complete: {successCount} success, {failCount} failed");
                Console.WriteLine($"[POSM IMAGE DEBUG] Final overlay graphics count: {_imageOverlay.Graphics.Count}");
                Console.WriteLine($"[POSM IMAGE DEBUG] === AddImagesForAssetAsync COMPLETE ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] CRITICAL ERROR in AddImagesForAssetAsync: {ex.Message}");
                Console.WriteLine($"[POSM IMAGE DEBUG] Stack trace: {ex.StackTrace}");
            }
        }

        private MapPoint? CalculatePositionAlongLine(Polyline line, double distance)
        {
            try
            {
                // Get the total length of the line
                var totalLength = GeometryEngine.Length(line);
                Console.WriteLine($"[POSM IMAGE DEBUG] Line total length: {totalLength:F2}, requested distance: {distance:F2}");

                if (distance < 0 || distance > totalLength)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Distance {distance:F2} is outside line bounds (0 to {totalLength:F2})");
                    // Clamp distance to line bounds
                    distance = Math.Max(0, Math.Min(distance, totalLength));
                }

                // Calculate the position along the line
                var position = GeometryEngine.CreatePointAlong(line, distance);
                
                if (position != null)
                {
                    Console.WriteLine($"[POSM IMAGE DEBUG] Calculated position: X={position.X:F2}, Y={position.Y:F2}");
                }
                
                return position;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error calculating position along line: {ex.Message}");
                return null;
            }
        }

        private void CreateImageGraphic(MapPoint position, ImageInfo imageInfo)
        {
            try
            {
                // Create a highly visible camera/image symbol with enhanced contrast
                var symbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Diamond, System.Drawing.Color.OrangeRed, 20)
                {
                    Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.Yellow, 4)
                };
                Console.WriteLine("[POSM IMAGE DEBUG] Creating high-contrast diamond pin symbol (size 20) for maximum visibility");

                // Create graphic with the image info as attributes
                var graphic = new Graphic(position, symbol);
                graphic.Attributes["ImagePath"] = imageInfo.FilePath;
                graphic.Attributes["Distance"] = imageInfo.Distance;
                graphic.Attributes["PictureLocation"] = imageInfo.PictureLocation;
                graphic.Attributes["MediaFolder"] = imageInfo.MediaFolder;

                _imageOverlay?.Graphics.Add(graphic);
                Console.WriteLine($"[POSM IMAGE DEBUG] Created graphic for image: {imageInfo.PictureLocation} at distance {imageInfo.Distance:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error creating image graphic: {ex.Message}");
            }
        }

        private async void OnMapTapped(object? sender, GeoViewInputEventArgs e)
        {
            try
            {
                if (_imageOverlay == null) return;

                // Ensure overlay is on top before identifying
                MoveOverlayToTop();

                // First close any hover popup immediately to avoid interference
                CloseHoverPopup();

                // Check if tap is near any image graphics (increased tolerance for easier clicking)
                var identifyResult = await _mapView.IdentifyGraphicsOverlayAsync(_imageOverlay, e.Position, 20, false);
                
                if (identifyResult.Graphics.Any())
                {
                    var tappedGraphic = identifyResult.Graphics.First();
                    var imagePath = tappedGraphic.Attributes["ImagePath"]?.ToString();
                    var distance = Convert.ToDouble(tappedGraphic.Attributes["Distance"] ?? 0.0);
                    var pictureLocation = tappedGraphic.Attributes["PictureLocation"]?.ToString() ?? "";

                    Console.WriteLine($"[POSM IMAGE DEBUG] Image graphic tapped: {pictureLocation} at distance {distance:F2}");
                    
                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        ShowImagePopup(imagePath, distance, pictureLocation, e.Position);
                        
                        // Mark event as handled to prevent map tap from showing other popups
                        e.Handled = true;
                        Console.WriteLine($"[POSM IMAGE DEBUG] Event marked as handled to prevent line popup interference");
                        return; // Exit early to prevent further processing
                    }
                    else
                    {
                        Console.WriteLine($"[POSM IMAGE DEBUG] Image file not found: {imagePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error handling map tap: {ex.Message}");
            }
        }

        private async void OnMapMouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (_imageOverlay == null) return;

                // Don't show hover popup if a click popup is already visible
                if (_imagePopupWindow != null && _imagePopupWindow.IsVisible)
                {
                    // If click popup is open, just close hover popup and return
                    CloseHoverPopup();
                    return;
                }

                // Cancel any pending hover close timer
                _hoverCloseTimer?.Dispose();
                _hoverCloseTimer = null;

                // Get mouse position relative to the map view
                var mousePosition = e.GetPosition(_mapView);
                
                // Ensure overlay is on top for hover detection
                MoveOverlayToTop();

                // Check if mouse is over any image graphics (optimized tolerance)
                var identifyResult = await _mapView.IdentifyGraphicsOverlayAsync(_imageOverlay, mousePosition, 25, false);
                
                if (identifyResult.Graphics.Any())
                {
                    var hoveredGraphic = identifyResult.Graphics.First();
                    var imagePath = hoveredGraphic.Attributes["ImagePath"]?.ToString();
                    var distance = Convert.ToDouble(hoveredGraphic.Attributes["Distance"] ?? 0.0);
                    var pictureLocation = hoveredGraphic.Attributes["PictureLocation"]?.ToString() ?? "";

                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        // Check if we need to show a new hover popup
                        var currentImagePath = _hoverPopupWindow?.Tag as string;
                        if (currentImagePath != imagePath)
                        {
                            Console.WriteLine($"[POSM HOVER DEBUG] Showing hover popup for: {pictureLocation}");
                            ShowHoverImagePopup(imagePath, distance, pictureLocation, mousePosition);
                        }
                    }
                }
                else
                {
                    // Mouse not over any image pin, start close timer
                    StartHoverCloseTimer();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM HOVER DEBUG] ERROR in OnMapMouseMove: {ex.Message}");
            }
        }

        private void OnMapMouseLeave(object? sender, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                // Start close timer when mouse leaves the map
                StartHoverCloseTimer();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error in OnMapMouseLeave: {ex.Message}");
            }
        }

        private void ShowHoverImagePopup(string imagePath, double distance, string pictureLocation, System.Windows.Point mousePosition)
        {
            try
            {
                // Close existing hover popup if different image
                CloseHoverPopup();
                
                // Create a hover popup window
                _hoverPopupWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ResizeMode = ResizeMode.NoResize,
                    Title = $"Hover Image - {pictureLocation}",
                    Tag = imagePath  // Store image path for comparison
                };

                _hoverPopupContent = new Image
                {
                    MaxWidth = 250,
                    MaxHeight = 250,
                    Margin = new Thickness(0)
                };

                // Add a border around the image
                var border = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = Brushes.DarkBlue,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(5),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        ShadowDepth = 3,
                        BlurRadius = 5,
                        Opacity = 0.3
                    },
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"{pictureLocation} (Distance: {distance:F1})",
                                Background = Brushes.LightBlue,
                                Padding = new Thickness(5),
                                FontSize = 9,
                                FontWeight = FontWeights.Bold,
                                Foreground = Brushes.DarkBlue
                            },
                            _hoverPopupContent
                        }
                    }
                };

                // Add mouse events to the popup to keep it stable
                border.MouseEnter += (s, e) => {
                    // Cancel close timer when mouse enters popup
                    _hoverCloseTimer?.Dispose();
                    _hoverCloseTimer = null;
                };
                border.MouseLeave += (s, e) => {
                    // Start close timer when mouse leaves popup
                    StartHoverCloseTimer();
                };

                // Load image
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 250; // Optimize for display size
                bitmap.EndInit();
                
                _hoverPopupContent.Source = bitmap;
                _hoverPopupWindow.Content = border;

                // Position popup directly at mouse cursor using Win32 API for accurate positioning
                GetCursorPos(out POINT cursorPos);
                Console.WriteLine($"[POSM HOVER DEBUG] Mouse cursor at screen: {cursorPos.X}, {cursorPos.Y}");
                
                // Position popup near cursor (offset to avoid covering the pin)
                _hoverPopupWindow.Left = cursorPos.X + 15; // Small offset to the right
                _hoverPopupWindow.Top = cursorPos.Y - 180; // Above cursor
                Console.WriteLine($"[POSM HOVER DEBUG] Popup positioned at: {_hoverPopupWindow.Left}, {_hoverPopupWindow.Top}");

                // Ensure popup stays within screen bounds
                var workingArea = SystemParameters.WorkArea;
                if (_hoverPopupWindow.Left + 300 > workingArea.Right)
                    _hoverPopupWindow.Left = cursorPos.X - 300 - 15; // Move to left side
                if (_hoverPopupWindow.Top < workingArea.Top)
                    _hoverPopupWindow.Top = cursorPos.Y + 20; // Move below cursor
                
                Console.WriteLine($"[POSM HOVER DEBUG] Final popup position: {_hoverPopupWindow.Left}, {_hoverPopupWindow.Top}");

                _hoverPopupWindow.Show();
                Console.WriteLine($"[POSM IMAGE DEBUG] Hover image popup displayed for: {pictureLocation}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error showing hover image popup: {ex.Message}");
            }
        }

        private void StartHoverCloseTimer()
        {
            // Close hover popup with a delay to avoid flickering
            _hoverCloseTimer?.Dispose();
            _hoverCloseTimer = new System.Threading.Timer(_ => 
            {
                try
                {
                    Application.Current?.Dispatcher?.Invoke(() => CloseHoverPopup());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[POSM HOVER DEBUG] Error in hover timer callback: {ex.Message}");
                }
            }, null, 800, System.Threading.Timeout.Infinite); // 800ms delay
        }
        
        private void CloseHoverPopup()
        {
            try
            {
                _hoverCloseTimer?.Dispose();
                _hoverCloseTimer = null;
                
                if (_hoverPopupWindow != null)
                {
                    _hoverPopupWindow.Close();
                    _hoverPopupWindow = null;
                    _hoverPopupContent = null;
                    Console.WriteLine($"[POSM IMAGE DEBUG] Hover popup closed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error closing hover popup: {ex.Message}");
            }
        }

        private void ShowImagePopup(string imagePath, double distance, string pictureLocation, System.Windows.Point screenPosition)
        {
            try
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Showing enhanced image viewer for: {pictureLocation}");

                // Close existing popup
                CloseImagePopup();

                // Create the new enhanced image viewer window
                var imageViewer = new ImageViewerWindow();
                imageViewer.LoadImage(imagePath, distance, pictureLocation);

                // Store reference for cleanup
                _imagePopupWindow = imageViewer;

                // Position viewer near click location but ensure it's visible
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var windowPosition = mainWindow.PointToScreen(new Point(0, 0));
                    imageViewer.Left = Math.Max(0, windowPosition.X + screenPosition.X - 400);
                    imageViewer.Top = Math.Max(0, windowPosition.Y + screenPosition.Y - 300);

                    // Ensure window is within screen bounds
                    var workingArea = SystemParameters.WorkArea;
                    if (imageViewer.Left + imageViewer.Width > workingArea.Right)
                        imageViewer.Left = workingArea.Right - imageViewer.Width;
                    if (imageViewer.Top + imageViewer.Height > workingArea.Bottom)
                        imageViewer.Top = workingArea.Bottom - imageViewer.Height;
                }

                imageViewer.Show();
                Console.WriteLine($"[POSM IMAGE DEBUG] Enhanced image viewer displayed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error showing image viewer: {ex.Message}");
            }
        }

        private void CloseImagePopup()
        {
            try
            {
                if (_imagePopupWindow != null)
                {
                    _imagePopupWindow.Close();
                    _imagePopupWindow = null;
                    _imagePopupContent = null;
                    Console.WriteLine($"[POSM IMAGE DEBUG] Click popup closed (pins remain visible)");
                }
                
                // Also close hover popup when click popup closes to avoid confusion
                CloseHoverPopup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error closing click popup: {ex.Message}");
            }
        }

        public void ClearImages()
        {
            try
            {
                var count = _imageOverlay?.Graphics.Count ?? 0;
                Console.WriteLine($"[POSM IMAGE DEBUG] Clearing {count} graphics from overlay");
                _imageOverlay?.Graphics.Clear();
                CloseImagePopup();
                Console.WriteLine("[POSM IMAGE DEBUG] All images cleared from overlay");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error clearing images: {ex.Message}");
            }
        }

        public int GetGraphicsCount()
        {
            return _imageOverlay?.Graphics.Count ?? 0;
        }

        public void Dispose()
        {
            try
            {
                ClearImages();
                
                if (_imageOverlay != null)
                {
                    _mapView.GraphicsOverlays.Remove(_imageOverlay);
                    _imageOverlay = null;
                }

                _mapView.GeoViewTapped -= OnMapTapped;
                _mapView.MouseMove -= OnMapMouseMove;
                _mapView.MouseLeave -= OnMapMouseLeave;
                
                // Clean up hover popup and timer
                _hoverCloseTimer?.Dispose();
                _hoverCloseTimer = null;
                CloseHoverPopup();
                Console.WriteLine("[POSM IMAGE DEBUG] InspectionImageOverlay disposed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POSM IMAGE DEBUG] Error disposing overlay: {ex.Message}");
            }
        }
    }
}