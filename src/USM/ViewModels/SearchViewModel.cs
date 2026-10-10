using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using USM.Core.Models;
using USM.Core.Parsers;
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
        private bool _searchPending;
        private static readonly SemaphoreSlim AudioLayoutProbeLimit = new(3);
        private static readonly ConcurrentDictionary<string, AudioTrackLayout> AudioLayoutCache = new(StringComparer.OrdinalIgnoreCase);

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
                    OnPropertyChanged(nameof(CanChooseSortDirection));
                    RequestSearch();
                }
            }
        }

        public bool CanChooseSortDirection => SelectedSortIndex != 0;

        private int _selectedSortDirectionIndex;
        public int SelectedSortDirectionIndex
        {
            get => _selectedSortDirectionIndex;
            set
            {
                if (SetProperty(ref _selectedSortDirectionIndex, value) && SelectedSortIndex != 0)
                {
                    RequestSearch();
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

        private int _selectedAudioLayoutFilterIndex;
        public int SelectedAudioLayoutFilterIndex
        {
            get => _selectedAudioLayoutFilterIndex;
            set
            {
                if (SetProperty(ref _selectedAudioLayoutFilterIndex, value))
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
                OnPropertyChanged(nameof(CanChooseSortDirection));
                _selectedSortDirectionIndex = 0;
                OnPropertyChanged(nameof(SelectedSortDirectionIndex));
                _selectedDifficultyFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedDifficultyFilterIndex));
                _selectedStatusFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedStatusFilterIndex));
                _selectedAudioLayoutFilterIndex = 0;
                OnPropertyChanged(nameof(SelectedAudioLayoutFilterIndex));
                RequestSearch();
            });

            _gameDetector.MaxConcurrentDownloadsChanged += _ => ProcessDownloadQueue();
        }

        private async Task PerformSearchAsync()
        {
            if (IsSearching) return;

            string query = string.IsNullOrWhiteSpace(SearchQuery) ? "*" : SearchQuery.Trim();
            string apiQuery = query;

            IsSearching = true;
            StatusText = query == "*"
                ? "Browsing charts on Chorus Encore..."
                : $"Searching Chorus Encore for '{query}'...";

            var (sortType, sortDirection) = GetSortRequest();

            try
            {
                var (total, charts) = await ChorusClient.SearchAsync(apiQuery, 1, 60, "guitar", sortType, sortDirection);

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

                StartAudioLayoutProbes(charts);
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
                if (_searchPending)
                {
                    _searchPending = false;
                    RequestSearch();
                }
            }
        }

        private void StartAudioLayoutProbes(IEnumerable<ChorusChart> charts)
        {
            foreach (ChorusChart chart in charts)
            {
                if (string.IsNullOrWhiteSpace(chart.Md5) || string.IsNullOrWhiteSpace(chart.DownloadUrl))
                {
                    chart.TrackLayout = AudioTrackLayout.Unknown;
                    continue;
                }

                if (AudioLayoutCache.TryGetValue(chart.Md5, out AudioTrackLayout cachedLayout))
                {
                    chart.TrackLayout = cachedLayout;
                    continue;
                }

                chart.TrackLayout = AudioTrackLayout.Checking;
                _ = ProbeAudioLayoutAsync(chart);
            }
        }

        private async Task ProbeAudioLayoutAsync(ChorusChart chart)
        {
            try
            {
                await AudioLayoutProbeLimit.WaitAsync();
                try
                {
                    if (AudioLayoutCache.TryGetValue(chart.Md5, out AudioTrackLayout cachedLayout))
                    {
                        chart.TrackLayout = cachedLayout;
                        RefreshAudioLayoutFilter();
                        return;
                    }

                    AudioTrackLayout layout = await SngDecoder.ProbeRemoteAudioLayoutAsync(chart.DownloadUrl!);
                    if (layout != AudioTrackLayout.Unknown)
                        AudioLayoutCache[chart.Md5] = layout;
                    chart.TrackLayout = layout;
                    RefreshAudioLayoutFilter();
                }
                finally
                {
                    AudioLayoutProbeLimit.Release();
                }
            }
            catch
            {
                chart.TrackLayout = AudioTrackLayout.Unknown;
                RefreshAudioLayoutFilter();
            }
        }

        private void RefreshAudioLayoutFilter()
        {
            if (SelectedAudioLayoutFilterIndex != 0)
            {
                ApplyFilterAndSort();
            }
        }

        private void RequestSearch()
        {
            if (IsSearching)
            {
                _searchPending = true;
                return;
            }

            _ = PerformSearchAsync();
        }

        private void ApplyFilterAndSort()
        {
            Results.Clear();
            var filtered = System.Linq.Enumerable.AsEnumerable(_rawCharts);

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

            AudioTrackLayout? selectedLayout = SelectedAudioLayoutFilterIndex switch
            {
                1 => AudioTrackLayout.SingleTrack,
                2 => AudioTrackLayout.HalfTrack,
                3 => AudioTrackLayout.MultiTrack,
                _ => null
            };
            if (selectedLayout.HasValue)
            {
                filtered = System.Linq.Enumerable.Where(filtered, c => c.TrackLayout == selectedLayout.Value);
            }

            // Chorus returns the selected server-side ordering; filtering preserves that order.
            foreach (var chart in filtered)
            {
                Results.Add(chart);
            }

            if (_rawCharts.Count > 0)
            {
                StatusText = $"Showing {Results.Count} of {_rawCharts.Count} charts found:";
            }
        }

        private (string? SortType, string? SortDirection) GetSortRequest()
        {
            (string? type, string? defaultDirection) = SelectedSortIndex switch
            {
                1 => ("modifiedTime", "desc"),
                2 => ("name", "asc"),
                3 => ("artist", "asc"),
                4 => ("length", "desc"),
                5 => ("album", "asc"),
                6 => ("genre", "asc"),
                7 => ("year", "desc"),
                8 => ("charter", "asc"),
                _ => ((string?)null, (string?)null)
            };

            if (type == null) return (null, null);

            string? direction = SelectedSortDirectionIndex switch
            {
                1 => "asc",
                2 => "desc",
                _ => defaultDirection
            };
            return (type, direction);
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
                while (_activeDownloads < maxConcurrent && _downloadQueue.TryDequeue(out var nextDownload))
                {
                    _activeDownloads++;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunDownloadAndConversionAsync(nextDownload);
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

            string tempDir = Path.Combine(Path.GetTempPath(), $"usm_download_{Guid.NewGuid():N}");

            try
            {
                var progress = new Progress<double>(p =>
                {
                    chart.DownloadProgress = Math.Min(0.5, p * 0.5);
                    chart.StatusText = $"Downloading: {p:P0}";
                });

                await SngDecoder.DecodeRemoteAsync(chart.DownloadUrl, tempDir, progress);

                chart.StatusText = "Converting to VR...";
                var convProgress = new Progress<ConversionProgress>(cp =>
                {
                    chart.DownloadProgress = 0.5 + (cp.Fraction * 0.5);
                    chart.StatusText = cp.Message;
                });

                bool success = await SongConverter.ConvertSongAsync(
                    tempDir,
                    customSongsDir,
                    convProgress,
                    sourceChartId: chart.Id,
                    sourceChartMd5: chart.Md5);
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

                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
