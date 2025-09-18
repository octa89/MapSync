using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace POSM_MR3_2
{
    /// <summary>
    /// Enhanced image viewer window with zoom, pan, and file operations
    /// </summary>
    public partial class ImageViewerWindow : Window
    {
        private string _imagePath = string.Empty;
        private double _currentZoom = 1.0;
        private const double ZoomStep = 0.1;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 5.0;

        private bool _isPanning = false;
        private Point _clickPosition;
        private Point _scrollOffset;

        public ImageViewerWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Load and display an image with metadata
        /// </summary>
        public void LoadImage(string imagePath, double distance, string pictureLocation)
        {
            _imagePath = imagePath;

            try
            {
                LoadingIndicator.Visibility = Visibility.Visible;

                // Update title and info
                TitleText.Text = $"Inspection Image - {pictureLocation}";
                InfoText.Text = $"Distance: {distance:F2} ft | File: {Path.GetFileName(imagePath)}";

                // Load the image
                if (File.Exists(imagePath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imagePath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();

                    MainImage.Source = bitmap;
                    Console.WriteLine($"[IMAGE VIEWER] Successfully loaded image: {imagePath}");
                }
                else
                {
                    MessageBox.Show($"Image file not found:\n{imagePath}", "File Not Found",
                                   MessageBoxButton.OK, MessageBoxImage.Warning);
                    Console.WriteLine($"[IMAGE VIEWER] Image file not found: {imagePath}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading image:\n{ex.Message}", "Load Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                Console.WriteLine($"[IMAGE VIEWER] Error loading image: {ex.Message}");
            }
            finally
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
            }
        }

        #region Zoom Controls

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(_currentZoom + ZoomStep);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(_currentZoom - ZoomStep);
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            SetZoom(1.0);
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
        }

        private void ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            if (MainImage.Source == null) return;

            var imageWidth = MainImage.Source.Width;
            var imageHeight = MainImage.Source.Height;
            var viewWidth = ImageScrollViewer.ActualWidth;
            var viewHeight = ImageScrollViewer.ActualHeight;

            var scaleX = viewWidth / imageWidth;
            var scaleY = viewHeight / imageHeight;
            var scale = Math.Min(scaleX, scaleY);

            SetZoom(scale);
            ImageTranslateTransform.X = 0;
            ImageTranslateTransform.Y = 0;
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            {
                e.Handled = true;
                var delta = e.Delta > 0 ? ZoomStep : -ZoomStep;
                SetZoom(_currentZoom + delta);
            }
        }

        private void SetZoom(double zoom)
        {
            _currentZoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
            ImageScaleTransform.ScaleX = _currentZoom;
            ImageScaleTransform.ScaleY = _currentZoom;
            ZoomLevelText.Text = $"{_currentZoom * 100:F0}%";
        }

        #endregion

        #region Pan Controls

        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // Double-click to reset view
                ZoomReset_Click(sender, null);
            }
            else
            {
                // Start panning
                _isPanning = true;
                _clickPosition = e.GetPosition(ImageScrollViewer);
                _scrollOffset = new Point(ImageScrollViewer.HorizontalOffset, ImageScrollViewer.VerticalOffset);
                MainImage.CaptureMouse();
                MainImage.Cursor = Cursors.SizeAll;
            }
        }

        private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                MainImage.ReleaseMouseCapture();
                MainImage.Cursor = Cursors.Hand;
            }
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var currentPosition = e.GetPosition(ImageScrollViewer);
                var deltaX = _clickPosition.X - currentPosition.X;
                var deltaY = _clickPosition.Y - currentPosition.Y;

                ImageScrollViewer.ScrollToHorizontalOffset(_scrollOffset.X + deltaX);
                ImageScrollViewer.ScrollToVerticalOffset(_scrollOffset.Y + deltaY);
            }
        }

        #endregion

        #region File Operations

        private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_imagePath) && File.Exists(_imagePath))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{_imagePath}\"");
                    Console.WriteLine($"[IMAGE VIEWER] Opened Explorer for: {_imagePath}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening Explorer:\n{ex.Message}", "Error",
                                   MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_imagePath))
            {
                try
                {
                    Clipboard.SetText(_imagePath);
                    MessageBox.Show("Image path copied to clipboard!", "Success",
                                   MessageBoxButton.OK, MessageBoxImage.Information);
                    Console.WriteLine($"[IMAGE VIEWER] Copied path to clipboard: {_imagePath}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error copying to clipboard:\n{ex.Message}", "Error",
                                   MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Add || e.Key == Key.OemPlus)
            {
                ZoomIn_Click(null, null);
            }
            else if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
            {
                ZoomOut_Click(null, null);
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                ZoomReset_Click(null, null);
            }
        }
    }
}