using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WpfMapApp1;

namespace POSM_MR3_2
{
    public partial class PosmVideoPlayerWindow : Window
    {
        private List<VideoInfo> _videos = new List<VideoInfo>();
        private DispatcherTimer? _progressTimer;
        private bool _isPlaying = false;
        private bool _isDraggingProgress = false;

        public PosmVideoPlayerWindow(string assetId, List<VideoInfo> videos)
        {
            InitializeComponent();
            
            Title = $"POSM Video Player - Asset: {assetId}";
            _videos = videos ?? new List<VideoInfo>();
            
            InitializeVideoSelection();
            InitializeProgressTimer();
        }

        private void InitializeVideoSelection()
        {
            if (!_videos.Any())
            {
                VideoInfoText.Text = "No videos found for this asset";
                PlayPauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                return;
            }

            // Populate video selection combo box
            VideoSelectionCombo.ItemsSource = _videos.Select((v, i) => new 
            { 
                Index = i, 
                Display = $"Video {i + 1}: {Path.GetFileName(v.FilePath)}",
                Video = v 
            }).ToList();
            VideoSelectionCombo.DisplayMemberPath = "Display";
            VideoSelectionCombo.SelectedIndex = 0;
        }

        private void InitializeProgressTimer()
        {
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _progressTimer.Tick += ProgressTimer_Tick;
        }

        private void VideoSelectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoSelectionCombo.SelectedItem != null)
            {
                dynamic selectedItem = VideoSelectionCombo.SelectedItem;
                var video = selectedItem.Video as VideoInfo;
                LoadVideo(video);
            }
        }

        private void LoadVideo(VideoInfo? video)
        {
            if (video == null) return;

            try
            {
                StopVideo();

                if (!File.Exists(video.FilePath))
                {
                    VideoInfoText.Text = $"Video file not found: {Path.GetFileName(video.FilePath)}";
                    VideoPathText.Text = video.FilePath;
                    PlayPauseButton.IsEnabled = false;
                    return;
                }

                VideoPlayer.Source = new Uri(video.FilePath, UriKind.Absolute);
                VideoInfoText.Text = $"Loading: {Path.GetFileName(video.FilePath)}";
                VideoPathText.Text = video.FilePath;

                // Additional diagnostics to verify expected path structure
                try
                {
                    var exePath = WpfMapApp1.App.Configuration?.posmExecutablePath ?? string.Empty;
                    var exeDir = string.IsNullOrWhiteSpace(exePath) ? string.Empty : Path.GetDirectoryName(exePath) ?? string.Empty;
                    var baseVideoDir = string.IsNullOrWhiteSpace(exeDir) ? string.Empty : Path.Combine(exeDir, "Video");
                    var expectedPath = (!string.IsNullOrWhiteSpace(baseVideoDir) && !string.IsNullOrWhiteSpace(video.MediaFolder) && !string.IsNullOrWhiteSpace(video.VideoLocation))
                        ? Path.GetFullPath(Path.Combine(baseVideoDir, video.MediaFolder, video.VideoLocation))
                        : string.Empty;

                    System.Diagnostics.Debug.WriteLine("[VideoPlayer] Selected video breakdown:");
                    System.Diagnostics.Debug.WriteLine($"  - FilePath: {video.FilePath}");
                    System.Diagnostics.Debug.WriteLine($"  - MediaFolder: {video.MediaFolder}");
                    System.Diagnostics.Debug.WriteLine($"  - VideoLocation: {video.VideoLocation}");
                    System.Diagnostics.Debug.WriteLine($"  - POSM exe: {exePath}");
                    System.Diagnostics.Debug.WriteLine($"  - POSM base: {exeDir}");
                    System.Diagnostics.Debug.WriteLine($"  - POSM video base: {baseVideoDir}");
                    if (!string.IsNullOrWhiteSpace(expectedPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"  - Expected (exe\\Video): {expectedPath} (exists={File.Exists(expectedPath)})");
                    }

                    System.Console.WriteLine("[POSM Videos] Selected video:");
                    System.Console.WriteLine($"  - FilePath: {video.FilePath}");
                    System.Console.WriteLine($"  - MediaFolder: {video.MediaFolder}");
                    System.Console.WriteLine($"  - VideoLocation: {video.VideoLocation}");
                    if (!string.IsNullOrWhiteSpace(baseVideoDir))
                    {
                        System.Console.WriteLine($"  - Expected base: {baseVideoDir}");
                    }
                    if (!string.IsNullOrWhiteSpace(expectedPath))
                    {
                        System.Console.WriteLine($"  - Expected path: {expectedPath} (exists={File.Exists(expectedPath)})");
                    }
                }
                catch { /* best-effort diagnostics */ }

                System.Diagnostics.Debug.WriteLine($"[VideoPlayer] Loading video: {video.FilePath}");
            }
            catch (Exception ex)
            {
                VideoInfoText.Text = $"Error loading video: {ex.Message}";
                VideoPathText.Text = "";
                PlayPauseButton.IsEnabled = false;
                System.Diagnostics.Debug.WriteLine($"[VideoPlayer] Error loading video: {ex.Message}");
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            PlayPauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                ProgressBar.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                VideoInfoText.Text = $"Ready to play - Duration: {VideoPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
            }
            else
            {
                VideoInfoText.Text = "Video loaded and ready to play";
            }
            
            VideoPlayer.Volume = VolumeSlider.Value;
            System.Diagnostics.Debug.WriteLine("[VideoPlayer] Video loaded successfully");
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            StopVideo();
            VideoInfoText.Text = "Video playback completed";
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            VideoInfoText.Text = $"Video playback failed: {e.ErrorException?.Message ?? "Unknown error"}";
            PlayPauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            System.Diagnostics.Debug.WriteLine($"[VideoPlayer] Playback failed: {e.ErrorException?.Message}");
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
            {
                PauseVideo();
            }
            else
            {
                PlayVideo();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }

        private void PlayVideo()
        {
            try
            {
                VideoPlayer.Play();
                _isPlaying = true;
                PlayPauseButton.Content = "⏸ Pause";
                _progressTimer?.Start();
                VideoInfoText.Text = "Playing...";
            }
            catch (Exception ex)
            {
                VideoInfoText.Text = $"Playback error: {ex.Message}";
            }
        }

        private void PauseVideo()
        {
            VideoPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            _progressTimer?.Stop();
            VideoInfoText.Text = "Paused";
        }

        private void StopVideo()
        {
            VideoPlayer.Stop();
            _isPlaying = false;
            PlayPauseButton.Content = "▶ Play";
            _progressTimer?.Stop();
            ProgressBar.Value = 0;
            VideoInfoText.Text = "Stopped";
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Volume = e.NewValue;
            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDraggingProgress && VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                ProgressBar.Value = VideoPlayer.Position.TotalSeconds;
                
                var current = VideoPlayer.Position;
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                VideoInfoText.Text = $"Playing - {current:mm\\:ss} / {total:mm\\:ss}";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            StopVideo();
            _progressTimer?.Stop();
            VideoPlayer.Source = null;
            base.OnClosing(e);
        }
    }

    public class VideoInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string MediaFolder { get; set; } = string.Empty;
        public string VideoLocation { get; set; } = string.Empty;
        public DateTime? InspectionDate { get; set; }
        
        public VideoInfo(string filePath, string mediaFolder = "", string videoLocation = "")
        {
            FilePath = filePath;
            MediaFolder = mediaFolder;
            VideoLocation = videoLocation;
        }
    }
}
