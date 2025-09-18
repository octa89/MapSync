using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Mapping;
using WpfMapApp1;

namespace POSM_MR3_2
{
    public class PosmVideoPopupButton
    {
        private readonly Config _config;
        private readonly PosmDatabaseService _dbService;
        private readonly DispatcherTimer _debounceTimer;
        private CancellationTokenSource? _currentCheckCts;
        private Button? _button;
        private Feature? _currentFeature;
        private string? _currentAssetId;

        public PosmVideoPopupButton(Config config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _dbService = new PosmDatabaseService(config);
            
            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250) // 250ms debounce
            };
            _debounceTimer.Tick += OnDebounceTimer_Tick;
        }

        public Button CreateButton()
        {
            _button = new Button
            {
                Content = "View POSM Videos",
                IsEnabled = false,
                MinWidth = 120,
                Height = 32,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)), // Steel blue
                Foreground = Brushes.White,
                FontWeight = FontWeights.Medium,
                ToolTip = "Checking..."
            };

            _button.Click += OnButtonClick;
            return _button;
        }

        public void OnPopupFeatureChanged(Feature? feature, FeatureLayer? layer)
        {
            Console.WriteLine($"[POSM VIDEO DEBUG] === OnPopupFeatureChanged called ===");
            Console.WriteLine($"[POSM VIDEO DEBUG] Feature: {(feature != null ? "Present" : "Null")}");
            Console.WriteLine($"[POSM VIDEO DEBUG] Layer: {layer?.Name ?? "Null"}");
            Console.WriteLine($"[POSM VIDEO DEBUG] Config Selected Layer: {_config.selectedLayer}");
            
            // Cancel any pending check
            _currentCheckCts?.Cancel();
            _currentCheckCts = new CancellationTokenSource();

            _currentFeature = feature;
            _currentAssetId = null;

            // Stop previous debounce timer
            _debounceTimer.Stop();

            // Quick initial state update
            UpdateButtonState(ButtonState.Checking, "Checking...");

            // Check if this is the target layer
            if (!(layer?.Name?.Equals(_config.selectedLayer, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] Layer mismatch - hiding button. Expected: '{_config.selectedLayer}', Got: '{layer?.Name}'");
                UpdateButtonState(ButtonState.Hidden, "Not applicable to this layer");
                return;
            }

            if (feature == null)
            {
                Console.WriteLine($"[POSM VIDEO DEBUG] No feature selected - disabling button");
                UpdateButtonState(ButtonState.Disabled, "Select one pipe to view videos");
                return;
            }

            Console.WriteLine($"[POSM VIDEO DEBUG] Starting debounce timer for database check");
            // Start debounce timer for actual database check
            _debounceTimer.Start();
        }

        private async void OnDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _debounceTimer.Stop();
            
            if (_currentFeature == null || _currentCheckCts == null)
                return;

            await CheckEnablementAsync(_currentFeature, _currentCheckCts.Token);
        }

        private async Task CheckEnablementAsync(Feature feature, CancellationToken cancellationToken)
        {
            try
            {
                // Log diagnostics
                _dbService.LogDiagnostics();

                // Check if database is available
                if (!_dbService.IsAvailable)
                {
                    var reason = _dbService.GetUnavailabilityReason();
                    UpdateButtonState(ButtonState.Disabled, $"POSM database unavailable: {reason}");
                    return;
                }

                // Get the asset ID from the configured field
                if (!feature.Attributes.TryGetValue(_config.idField, out var idValue) || idValue == null)
                {
                    UpdateButtonState(ButtonState.Disabled, $"This pipe has no value for the configured IdField ({_config.idField})");
                    return;
                }

                var assetId = idValue.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(assetId))
                {
                    UpdateButtonState(ButtonState.Disabled, $"This pipe has no value for the configured IdField ({_config.idField})");
                    return;
                }

                _currentAssetId = assetId;

                // Check for available videos on disk for this asset
                UpdateButtonState(ButtonState.Checking, "Checking for POSM videos...");

                var videos = await _dbService.GetVideoPathsForAssetAsync(assetId, cancellationToken);
                System.Diagnostics.Debug.WriteLine($"[PosmPopup] DB returned {videos.Count} candidate video path(s) for {assetId}");
                System.Console.WriteLine($"[POSM Videos] DB returned {videos.Count} candidate path(s) for {assetId}");
                if (cancellationToken.IsCancellationRequested) return;

                // Quick check with expected path convention: <POSM exe dir>\\Video\\<MediaFolder>\\<VideoLocation>
                QuickCheckExpectedVideoPaths(assetId, videos);

                var existingVideos = videos.Where(v => System.IO.File.Exists(v.FilePath)).ToList();
                var sample = string.Join("\n  - ", videos.Take(5).Select(v => $"{v.FilePath} (exists={System.IO.File.Exists(v.FilePath)})"));
                if (!string.IsNullOrWhiteSpace(sample))
                {
                    System.Diagnostics.Debug.WriteLine($"[PosmPopup] Sample paths for {assetId}:\n  - {sample}");
                    System.Console.WriteLine($"[POSM Videos] Sample paths for {assetId}:\n  - {sample}");
                }

                Console.WriteLine($"[POSM VIDEO DEBUG] Found {videos.Count} total video paths, {existingVideos.Count} exist on disk");
                
                if (existingVideos.Any())
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] ✅ VIDEOS FOUND - Setting button to ENABLED/GREEN state");
                    UpdateButtonState(ButtonState.Enabled, $"View POSM videos for asset: {assetId}");

                    System.Diagnostics.Debug.WriteLine($"[PosmPopup] INFO - Popup resolved:");
                    System.Diagnostics.Debug.WriteLine($"  - SelectedLayer: {_config.selectedLayer}");
                    System.Diagnostics.Debug.WriteLine($"  - IdField: {_config.idField}");
                    System.Diagnostics.Debug.WriteLine($"  - Asset ID: {assetId}");
                    System.Diagnostics.Debug.WriteLine($"  - POSM.mdb: {_dbService.PosmDbPath}");
                    System.Diagnostics.Debug.WriteLine($"  - Videos available: {existingVideos.Count}");
                    System.Console.WriteLine($"[POSM Videos] {existingVideos.Count} video file(s) exist for {assetId}");
                }
                else
                {
                    Console.WriteLine($"[POSM VIDEO DEBUG] ❌ NO VIDEOS FOUND - Setting button to DISABLED/RED state");
                    UpdateButtonState(ButtonState.Disabled, $"No videos found for ID: {assetId}");

                    System.Diagnostics.Debug.WriteLine($"[PosmPopup] INFO - Popup resolved:");
                    System.Diagnostics.Debug.WriteLine($"  - SelectedLayer: {_config.selectedLayer}");
                    System.Diagnostics.Debug.WriteLine($"  - IdField: {_config.idField}");
                    System.Diagnostics.Debug.WriteLine($"  - Asset ID: {assetId}");
                    System.Diagnostics.Debug.WriteLine($"  - POSM.mdb: {_dbService.PosmDbPath}");
                    System.Diagnostics.Debug.WriteLine($"  - Videos available: 0");
                    System.Console.WriteLine($"[POSM Videos] No existing video files found for {assetId}");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PosmPopup] Error checking enablement: {ex.Message}");
                UpdateButtonState(ButtonState.Disabled, $"Error checking POSM database: {ex.Message}");
            }
        }

        private void QuickCheckExpectedVideoPaths(string assetId, System.Collections.Generic.List<VideoInfo> videos)
        {
            try
            {
                var exePath = App.Configuration?.posmExecutablePath ?? string.Empty;
                var exeDir = string.IsNullOrWhiteSpace(exePath) ? string.Empty : Path.GetDirectoryName(exePath) ?? string.Empty;
                var baseVideoDir = string.IsNullOrWhiteSpace(exeDir) ? string.Empty : Path.Combine(exeDir, "Video");

                System.Console.WriteLine($"[POSM Videos] Quick check for asset {assetId}");
                System.Console.WriteLine($"[POSM Videos] Expected template: C:\\POSM\\Video\\{{media folder}}\\{{Video Location}}");
                System.Console.WriteLine($"[POSM Videos] POSM exe: {exePath}");
                System.Console.WriteLine($"[POSM Videos] POSM base: {exeDir}");
                System.Console.WriteLine($"[POSM Videos] POSM video base: {baseVideoDir}");

                foreach (var v in videos)
                {
                    var mf = v.MediaFolder ?? string.Empty;
                    var vl = v.VideoLocation ?? string.Empty;
                    var expectedPath = (string.IsNullOrWhiteSpace(baseVideoDir) || string.IsNullOrWhiteSpace(mf) || string.IsNullOrWhiteSpace(vl))
                        ? string.Empty
                        : Path.GetFullPath(Path.Combine(baseVideoDir, mf, vl));
                    if (!string.IsNullOrWhiteSpace(expectedPath))
                    {
                        var exists = File.Exists(expectedPath);
                        System.Console.WriteLine($"[POSM Videos] Expected: {expectedPath} (exists={exists})");
                    }
                    else
                    {
                        System.Console.WriteLine($"[POSM Videos] Skipped expected path build (missing base or fields). MediaFolder='{mf}', VideoLocation='{vl}'");
                    }
                }
            }
            catch { /* best-effort diagnostics only */ }
        }

        private void UpdateButtonState(ButtonState state, string tooltip)
        {
            if (_button == null)
                return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                switch (state)
                {
                    case ButtonState.Hidden:
                        _button.Visibility = Visibility.Collapsed;
                        break;

                    case ButtonState.Checking:
                        _button.Visibility = Visibility.Visible;
                        _button.IsEnabled = false;
                        _button.Background = new SolidColorBrush(Color.FromArgb(255, 255, 165, 0)); // Bright Orange - Checking
                        _button.Foreground = new SolidColorBrush(Colors.White);
                        _button.Content = "Checking...";
                        _button.FontWeight = FontWeights.Bold;
                        Console.WriteLine("[POSM VIDEO DEBUG] Button state: CHECKING (Orange)");
                        break;

                    case ButtonState.Enabled:
                        _button.Visibility = Visibility.Visible;
                        _button.IsEnabled = true;
                        _button.Background = new SolidColorBrush(Color.FromArgb(255, 0, 200, 0)); // Bright Green - Videos Available
                        _button.Foreground = new SolidColorBrush(Colors.White);
                        _button.Content = "View POSM Videos";
                        _button.FontWeight = FontWeights.Bold;
                        Console.WriteLine("[POSM VIDEO DEBUG] Button state: ENABLED (Bright Green - Videos Available)");
                        break;

                    case ButtonState.Disabled:
                        _button.Visibility = Visibility.Visible;
                        _button.IsEnabled = false;
                        _button.Background = new SolidColorBrush(Color.FromArgb(255, 200, 50, 50)); // Red - No Videos
                        _button.Foreground = new SolidColorBrush(Colors.White);
                        _button.Content = "No POSM Videos";
                        _button.FontWeight = FontWeights.Normal;
                        Console.WriteLine("[POSM VIDEO DEBUG] Button state: DISABLED (Red - No Videos)");
                        break;
                }

                _button.ToolTip = tooltip;
            });
        }

        private async void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentAssetId))
            {
                MessageBox.Show("No asset ID available.", "POSM Videos", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LaunchPosmVideosAsync(_currentAssetId);
        }

        private async Task LaunchPosmVideosAsync(string assetId)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[PosmPopup] Opening video player for asset: {assetId}");

                // Get video file paths from database
                var videoPaths = await _dbService.GetVideoPathsForAssetAsync(assetId);
                
                if (!videoPaths.Any())
                {
                    MessageBox.Show($"No video files found for asset: {assetId}", "POSM Videos", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Filter to only existing video files
                var existingVideos = videoPaths.Where(v => System.IO.File.Exists(v.FilePath)).ToList();
                
                if (!existingVideos.Any())
                {
                    var missingFiles = string.Join("\n", videoPaths.Select(v => v.FilePath));
                    MessageBox.Show($"Video files not found on disk for asset {assetId}:\n\n{missingFiles}", 
                        "POSM Videos", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (existingVideos.Count < videoPaths.Count)
                {
                    var missingCount = videoPaths.Count - existingVideos.Count;
                    System.Diagnostics.Debug.WriteLine($"[PosmPopup] Warning: {missingCount} video files not found on disk");
                }

                System.Diagnostics.Debug.WriteLine($"[PosmPopup] Opening video player with {existingVideos.Count} videos");

                // Open video player window
                var videoPlayerWindow = new PosmVideoPlayerWindow(assetId, existingVideos)
                {
                    Owner = Application.Current.MainWindow
                };
                
                videoPlayerWindow.Show();
                
                System.Diagnostics.Debug.WriteLine($"[PosmPopup] Video player opened successfully for asset: {assetId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PosmPopup] Error opening video player: {ex.Message}");
                MessageBox.Show($"Could not open video player: {ex.Message}", "POSM Videos", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _currentCheckCts?.Cancel();
            _currentCheckCts?.Dispose();
            _debounceTimer?.Stop();
        }

        private enum ButtonState
        {
            Hidden,
            Checking,
            Enabled,
            Disabled
        }
    }
}
