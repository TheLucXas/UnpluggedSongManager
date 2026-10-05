using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using USM.Core.Models;
using USM.Core.Services;

namespace USM.ViewModels
{
    public class SearchViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;

        private string _searchQuery = string.Empty;
        private bool _isSearching;
        private string _statusText = "Type a song or artist name to search Chorus Encore / Clone Hero database.";

        private readonly ConcurrentQueue<ChorusChart> _downloadQueue = new();
        private int _activeDownloads = 0;
        private readonly object _queueLock = new();

        public string SearchQuery
        {
            get => _searchQuery;
            set => SetProperty(ref _searchQuery, value);
        }

        public bool IsSearching
        {
            get => _isSearching;
            set => SetProperty(ref _isSearching, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public ObservableCollection<ChorusChart> Results { get; } = new();

        public ICommand SearchCommand { get; }
        public ICommand DownloadCommand { get; }

        public SearchViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            SearchCommand = new AsyncRelayCommand(PerformSearchAsync);
            DownloadCommand = new RelayCommand(EnqueueDownload);

            _gameDetector.MaxConcurrentDownloadsChanged += _ => ProcessDownloadQueue();
        }

        private async Task PerformSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchQuery) || IsSearching) return;

            IsSearching = true;
            StatusText = $"Searching Chorus Encore for '{SearchQuery}'...";
            Results.Clear();

            try
            {
                var (total, charts) = await ChorusClient.SearchAsync(SearchQuery);

                string? customSongsDir = _gameDetector.GetCustomSongsDir();
                foreach (var chart in charts)
                {
                    if (!string.IsNullOrEmpty(customSongsDir))
                    {
                        string folderGuess = SongConverter.SanitizeFolderName($"{chart.Artist} - {chart.Title}");
                        if (Directory.Exists(Path.Combine(customSongsDir, folderGuess)))
                        {
                            chart.IsInstalled = true;
                            chart.StatusText = "✓ Installed";
                        }
                        else
                        {
                            chart.StatusText = "📥 Download";
                        }
                    }
                    else
                    {
                        chart.StatusText = "📥 Download";
                    }
                    Results.Add(chart);
                }

                StatusText = charts.Count > 0
                    ? $"Found {charts.Count} charts ready for Unplugged VR:"
                    : "No charts found. Try another search term.";
            }
            catch (Exception ex)
            {
                StatusText = $"Search error: {ex.Message}";
            }
            finally
            {
                IsSearching = false;
            }
        }

        private void EnqueueDownload(object? param)
        {
            if (param is not ChorusChart chart || !chart.CanDownload) return;

            string? customSongsDir = _gameDetector.GetCustomSongsDir();
            if (string.IsNullOrEmpty(customSongsDir))
            {
                MessageBox.Show("Please configure the Unplugged game folder in Settings first!", "Game Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(chart.DownloadUrl))
            {
                MessageBox.Show("This chart has no direct download URL available.", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            chart.IsQueued = true;
            chart.StatusText = "⏳ Queued...";
            _downloadQueue.Enqueue(chart);

            ProcessDownloadQueue();
        }

        private void ProcessDownloadQueue()
        {
            lock (_queueLock)
            {
                int maxConcurrent = _gameDetector.MaxConcurrentDownloads;
                while (_activeDownloads < maxConcurrent && _downloadQueue.TryDequeue(out var nextChart))
                {
                    _activeDownloads++;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunDownloadAndConversionAsync(nextChart);
                        }
                        finally
                        {
                            lock (_queueLock)
                            {
                                _activeDownloads--;
                            }
                            ProcessDownloadQueue();
                        }
                    });
                }
            }
        }

        private async Task RunDownloadAndConversionAsync(ChorusChart chart)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                chart.IsQueued = false;
                chart.IsDownloading = true;
                chart.DownloadProgress = 0.05;
                chart.StatusText = "Downloading...";
            });

            string? customSongsDir = _gameDetector.GetCustomSongsDir();
            if (string.IsNullOrEmpty(customSongsDir) || string.IsNullOrEmpty(chart.DownloadUrl))
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    chart.IsDownloading = false;
                    chart.StatusText = "Failed: Missing dir/URL";
                });
                return;
            }

            string tempSng = Path.Combine(Path.GetTempPath(), $"{chart.Md5}_{Guid.NewGuid():N}.sng");

            try
            {
                var progress = new Progress<double>(p =>
                {
                    chart.DownloadProgress = Math.Min(0.5, p * 0.5);
                    chart.StatusText = $"Downloading: {p:P0}";
                });

                await ChorusClient.DownloadFileAsync(chart.DownloadUrl, tempSng, progress);

                chart.StatusText = "Converting to VR...";
                var convProgress = new Progress<ConversionProgress>(cp =>
                {
                    chart.DownloadProgress = 0.5 + (cp.Fraction * 0.5);
                    chart.StatusText = cp.Message;
                });

                bool success = await SongConverter.ConvertSongAsync(tempSng, customSongsDir, convProgress);
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (success)
                    {
                        chart.IsInstalled = true;
                        chart.StatusText = "✓ Installed in CustomSongs";
                        _notifyCallback?.Invoke($"Song '{chart.Title}' downloaded and installed successfully!");
                    }
                    else
                    {
                        chart.StatusText = "Conversion failed";
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    chart.StatusText = $"Error: {ex.Message}";
                });
            }
            finally
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    chart.IsDownloading = false;
                });

                if (File.Exists(tempSng))
                {
                    try { File.Delete(tempSng); } catch { }
                }
            }
        }
    }
}
