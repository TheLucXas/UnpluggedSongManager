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
        private readonly List<ChorusChart> _rawCharts = new();

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

        private int _selectedSortIndex = 0;
        public int SelectedSortIndex
        {
            get => _selectedSortIndex;
            set
            {
                if (SetProperty(ref _selectedSortIndex, value))
                {
                    if (_rawCharts.Count > 0 || !string.IsNullOrWhiteSpace(SearchQuery))
                    {
                        _ = PerformSearchAsync();
                    }
                }
            }
        }

        private int _selectedTrackFilterIndex = 0;
        public int SelectedTrackFilterIndex
        {
            get => _selectedTrackFilterIndex;
            set
            {
                if (SetProperty(ref _selectedTrackFilterIndex, value))
                {
                    ApplyFilterAndSort();
                }
            }
        }

        private int _selectedDifficultyFilterIndex = 0;
        public int SelectedDifficultyFilterIndex
        {
            get => _selectedDifficultyFilterIndex;
            set
            {
                if (SetProperty(ref _selectedDifficultyFilterIndex, value))
                {
                    ApplyFilterAndSort();
                }
            }
        }

        private int _selectedStatusFilterIndex = 0;
        public int SelectedStatusFilterIndex
        {
            get => _selectedStatusFilterIndex;
            set
            {
                if (SetProperty(ref _selectedStatusFilterIndex, value))
                {
                    ApplyFilterAndSort();
                }
            }
        }

        public ObservableCollection<ChorusChart> Results { get; } = new();

        public ICommand SearchCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand ResetFiltersCommand { get; }

        public SearchViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            SearchCommand = new AsyncRelayCommand(PerformSearchAsync);
            DownloadCommand = new RelayCommand(EnqueueDownload);
            ResetFiltersCommand = new RelayCommand(_ =>
            {
                _selectedSortIndex = 0;
                OnPropertyChanged(nameof(SelectedSortIndex));
                _selectedTrackFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedTrackFilterIndex));
                _selectedDifficultyFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedDifficultyFilterIndex));
                _selectedStatusFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedStatusFilterIndex));
                ApplyFilterAndSort();
            });

            _gameDetector.MaxConcurrentDownloadsChanged += _ => ProcessDownloadQueue();
        }

        private async Task PerformSearchAsync()
        {
            if (IsSearching) return;

            string query = string.IsNullOrWhiteSpace(SearchQuery) ? "*" : SearchQuery.Trim();

            IsSearching = true;
            StatusText = query == "*"
                ? "Browsing latest charts on Chorus Encore..."
                : $"Searching Chorus Encore for '{query}'...";

            string? sortType = null;
            string? sortDirection = null;

            switch (SelectedSortIndex)
            {
                case 1: // Newest / Recientes
                    sortType = "modifiedTime";
                    sortDirection = "desc";
                    break;
                case 2: // Title (A-Z)
                    sortType = "name";
                    sortDirection = "asc";
                    break;
                case 3: // Artist (A-Z)
                    sortType = "artist";
                    sortDirection = "asc";
                    break;
                case 4: // Year
                    sortType = "year";
                    sortDirection = "desc";
                    break;
                case 5: // Length
                    sortType = "length";
                    sortDirection = "desc";
                    break;
            }

            try
            {
                var (total, charts) = await ChorusClient.SearchAsync(query, 1, 40, "guitar", sortType, sortDirection);

                _rawCharts.Clear();
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
                    _rawCharts.Add(chart);
                }

                ApplyFilterAndSort();

                if (_rawCharts.Count == 0)
                {
                    StatusText = "No charts found. Try another search term or change filters.";
                }
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

        private void ApplyFilterAndSort()
        {
            Results.Clear();
            var filtered = System.Linq.Enumerable.AsEnumerable(_rawCharts);

            // MultiTrack filter
            if (SelectedTrackFilterIndex == 1) // MultiTrack Only
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.IsMultiTrack);
            }
            else if (SelectedTrackFilterIndex == 2) // Single Track Only
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => !c.IsMultiTrack);
            }

            // Difficulty filter
            if (SelectedDifficultyFilterIndex == 1) // Beginner Tier 0-1
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.DiffGuitar >= 0 && c.DiffGuitar <= 1);
            }
            else if (SelectedDifficultyFilterIndex == 2) // Intermediate Tier 2-3
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.DiffGuitar >= 2 && c.DiffGuitar <= 3);
            }
            else if (SelectedDifficultyFilterIndex == 3) // Expert Tier 4+
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.DiffGuitar >= 4);
            }

            // Installation status filter
            if (SelectedStatusFilterIndex == 1) // Not Installed Only
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => !c.IsInstalled);
            }
            else if (SelectedStatusFilterIndex == 2) // Installed Only
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.IsInstalled);
            }

            foreach (var chart in filtered)
            {
                Results.Add(chart);
            }

            if (_rawCharts.Count > 0)
            {
                StatusText = $"Showing {Results.Count} of {_rawCharts.Count} charts found:";
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
