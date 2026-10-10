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
        private const int SearchPageSize = 60;

        private string _searchQuery = string.Empty;
        private bool _isSearching;
        private string _statusText = "Type a song or artist name to search Chorus Encore / Clone Hero database.";

        private readonly ConcurrentQueue<ChorusChart> _downloadQueue = new();
        private int _activeDownloads = 0;
        private readonly object _queueLock = new();
        private readonly List<ChorusChart> _rawCharts = new();
        private bool _searchPending;
        private int _nextApiPage = 1;
        private int _totalCharts;
        private bool _hasMoreApiPages;
        private int _currentPage = 1;
        private string _activeQuery = "*";
        private string? _activeSortType;
        private string? _activeSortDirection;
        private DateTime _lastAudioFilterRefreshUtc = DateTime.MinValue;
        private CancellationTokenSource? _searchCancellation;
        private static readonly SemaphoreSlim AudioLayoutProbeLimit = new(3);
        private static readonly ConcurrentDictionary<string, SngDecoder.RemoteSongProfile> SongProfileCache = new(StringComparer.OrdinalIgnoreCase);

        public string SearchQuery
        {
            get => _searchQuery;
            set => SetProperty(ref _searchQuery, value);
        }

        public bool IsSearching
        {
            get => _isSearching;
            set
            {
                if (SetProperty(ref _isSearching, value))
                {
                    OnPropertyChanged(nameof(CanGoToPreviousPage));
                    OnPropertyChanged(nameof(CanGoToNextPage));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string PageInfoText => $"Page {_currentPage}";
        public bool CanGoToPreviousPage => !IsSearching && _currentPage > 1;
        public bool CanGoToNextPage => !IsSearching &&
            (GetFilteredCharts().Count > _currentPage * SearchPageSize || _hasMoreApiPages);

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
                    RequestSearch();
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
                    RequestSearch();
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
                    RequestSearch();
                }
            }
        }

        public ObservableCollection<ChorusChart> Results { get; } = new();

        public ICommand SearchCommand { get; }
        public ICommand DownloadCommand { get; }
        public ICommand ResetFiltersCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }

        public SearchViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            SearchCommand = new AsyncRelayCommand(SearchNowAsync);
            DownloadCommand = new RelayCommand(EnqueueDownload);
            PreviousPageCommand = new RelayCommand(_ => MoveToPreviousPage());
            NextPageCommand = new AsyncRelayCommand(LoadNextPageAsync);
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

            _activeQuery = string.IsNullOrWhiteSpace(SearchQuery) ? "*" : SearchQuery.Trim();
            (_activeSortType, _activeSortDirection) = GetSortRequest();
            IsSearching = true;
            var cancellation = new CancellationTokenSource();
            _searchCancellation = cancellation;
            _rawCharts.Clear();
            Results.Clear();
            _currentPage = 1;
            _nextApiPage = 1;
            _totalCharts = 0;
            _hasMoreApiPages = true;
            _lastAudioFilterRefreshUtc = DateTime.MinValue;
            OnPropertyChanged(nameof(PageInfoText));
            StatusText = _activeQuery == "*"
                ? "Browsing charts on Chorus Encore..."
                : $"Searching Chorus Encore for '{_activeQuery}'...";
            OnPropertyChanged(nameof(CanGoToNextPage));

            try
            {
                await FillUntilMatchCountAsync(SearchPageSize, cancellation.Token);
                ShowCurrentPage();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                StatusText = $"Search error: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                    _searchCancellation = null;
                cancellation.Dispose();
                IsSearching = false;
                if (_searchPending)
                {
                    _searchPending = false;
                    RequestSearch();
                }
            }
        }

        private Task SearchNowAsync()
        {
            if (IsSearching)
            {
                _searchPending = true;
                _searchCancellation?.Cancel();
                return Task.CompletedTask;
            }

            return PerformSearchAsync();
        }

        private List<Task> StartAudioLayoutProbes(IEnumerable<ChorusChart> charts, CancellationToken cancellationToken)
        {
            var probeTasks = new List<Task>();
            foreach (ChorusChart chart in charts)
            {
                if (string.IsNullOrWhiteSpace(chart.Md5) || string.IsNullOrWhiteSpace(chart.DownloadUrl))
                {
                    chart.TrackLayout = AudioTrackLayout.Unknown;
                    continue;
                }

                if (SongProfileCache.TryGetValue(chart.Md5, out SngDecoder.RemoteSongProfile? cachedProfile))
                {
                    ApplySongProfile(chart, cachedProfile);
                    continue;
                }

                chart.TrackLayout = AudioTrackLayout.Checking;
                chart.IsCheckingQuality = true;
                probeTasks.Add(ProbeSongProfileAsync(chart, cancellationToken));
            }
            return probeTasks;
        }

        private async Task ProbeSongProfileAsync(ChorusChart chart, CancellationToken cancellationToken)
        {
            try
            {
                await AudioLayoutProbeLimit.WaitAsync(cancellationToken);
                try
                {
                    if (SongProfileCache.TryGetValue(chart.Md5, out SngDecoder.RemoteSongProfile? cachedProfile))
                    {
                        ApplySongProfile(chart, cachedProfile);
                        return;
                    }

                    SngDecoder.RemoteSongProfile profile = await SngDecoder.ProbeRemoteSongProfileAsync(chart.DownloadUrl!, cancellationToken);
                    SongProfileCache[chart.Md5] = profile;
                    ApplySongProfile(chart, profile);
                    RefreshVisibleResultsForAudioMatch(chart, profile.TrackLayout, cancellationToken);
                }
                finally
                {
                    AudioLayoutProbeLimit.Release();
                }
            }
            catch
            {
                chart.TrackLayout = AudioTrackLayout.Unknown;
                chart.IsIdealForUnplugged = false;
                chart.IsCheckingQuality = false;
            }
        }

        private static void ApplySongProfile(ChorusChart chart, SngDecoder.RemoteSongProfile profile)
        {
            chart.TrackLayout = profile.TrackLayout;
            chart.AudioStemCount = profile.AudioStemCount;
            chart.SourceDifficultyCount = profile.SourceDifficultyCount;
            chart.IsIdealForUnplugged = profile.IsIdealForUnplugged;
            chart.IsCheckingQuality = false;
        }

        private void RequestSearch()
        {
            if (IsSearching)
            {
                _searchPending = true;
                _searchCancellation?.Cancel();
                return;
            }

            _ = PerformSearchAsync();
        }

        private void RefreshVisibleResultsForAudioMatch(ChorusChart chart, AudioTrackLayout layout, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || !_rawCharts.Contains(chart))
                return;

            AudioTrackLayout? selectedLayout = SelectedAudioLayoutFilterIndex switch
            {
                1 => AudioTrackLayout.SingleTrack,
                2 => AudioTrackLayout.HalfTrack,
                3 => AudioTrackLayout.MultiTrack,
                _ => null
            };
            if (!selectedLayout.HasValue || selectedLayout != layout)
                return;

            DateTime now = DateTime.UtcNow;
            if ((now - _lastAudioFilterRefreshUtc).TotalMilliseconds < 300)
                return;
            _lastAudioFilterRefreshUtc = now;

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(ShowCurrentPage));
            else
                ShowCurrentPage();
        }

        private async Task FillUntilMatchCountAsync(int requiredMatchCount, CancellationToken cancellationToken)
        {
            while (GetFilteredCharts().Count < requiredMatchCount && _hasMoreApiPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusText = $"Searching Chorus page {_nextApiPage} for matching charts... " +
                             $"{GetFilteredCharts().Count} matches found so far.";
                await FetchNextApiPageAsync(cancellationToken);
            }
        }

        private async Task FetchNextApiPageAsync(CancellationToken cancellationToken)
        {
            if (!_hasMoreApiPages) return;

            int page = _nextApiPage;
            var (total, charts) = await ChorusClient.SearchAsync(
                _activeQuery, page, SearchPageSize, "guitar", _activeSortType, _activeSortDirection, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _totalCharts = total;
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

            _nextApiPage = page + 1;
            // Chorus may return a different number of entries than per_page on
            // some pages. Continue until the endpoint returns an empty page.
            _hasMoreApiPages = charts.Count > 0;

            _ = StartAudioLayoutProbes(
                charts,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            ShowCurrentPage();
            OnPropertyChanged(nameof(CanGoToNextPage));
        }

        private List<ChorusChart> GetFilteredCharts()
        {
            IEnumerable<ChorusChart> filtered = _rawCharts;

            // Difficulty filter
            if (SelectedDifficultyFilterIndex == 1) // Beginner Tier 0-1
            {
                filtered = filtered.Where(c => c.DiffGuitar >= 0 && c.DiffGuitar <= 1);
            }
            else if (SelectedDifficultyFilterIndex == 2) // Intermediate Tier 2-3
            {
                filtered = filtered.Where(c => c.DiffGuitar >= 2 && c.DiffGuitar <= 3);
            }
            else if (SelectedDifficultyFilterIndex == 3) // Expert Tier 4+
            {
                filtered = filtered.Where(c => c.DiffGuitar >= 4);
            }

            // Installation status filter
            if (SelectedStatusFilterIndex == 1) // Not Installed Only
            {
                filtered = filtered.Where(c => !c.IsInstalled);
            }
            else if (SelectedStatusFilterIndex == 2) // Installed Only
            {
                filtered = filtered.Where(c => c.IsInstalled);
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
                filtered = filtered.Where(c => c.TrackLayout == selectedLayout.Value);
            }

            return filtered.ToList();
        }

        private void ShowCurrentPage()
        {
            List<ChorusChart> filtered = GetFilteredCharts();
            int startIndex = (_currentPage - 1) * SearchPageSize;
            List<ChorusChart> pageResults = filtered.Skip(startIndex).Take(SearchPageSize).ToList();
            int sharedCount = Math.Min(Results.Count, pageResults.Count);
            for (int i = 0; i < sharedCount; i++)
            {
                if (!ReferenceEquals(Results[i], pageResults[i]))
                    Results[i] = pageResults[i];
            }
            while (Results.Count > pageResults.Count)
                Results.RemoveAt(Results.Count - 1);
            for (int i = Results.Count; i < pageResults.Count; i++)
                Results.Add(pageResults[i]);

            OnPropertyChanged(nameof(PageInfoText));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));

            if (filtered.Count <= startIndex)
            {
                if (IsSearching && _hasMoreApiPages)
                {
                    StatusText = $"Searching Chorus page {_nextApiPage}; {filtered.Count} matching charts found so far.";
                }
                else if (filtered.Count == 0)
                {
                    StatusText = _rawCharts.Count == 0
                        ? "No charts found on Chorus."
                        : $"No matching charts in {_rawCharts.Count} scanned; Chorus has {_totalCharts} charts for this search.";
                }
                else
                {
                    StatusText = $"No results on page {_currentPage}; {filtered.Count} matching charts found.";
                }
                return;
            }

            int first = startIndex + 1;
            int last = startIndex + Results.Count;
            string matchCount = _hasMoreApiPages ? $"at least {filtered.Count}" : filtered.Count.ToString();
            bool pageStillFilling = filtered.Count < _currentPage * SearchPageSize && _hasMoreApiPages;
            string scanStatus = IsSearching && pageStillFilling ? $" · scanning {_rawCharts.Count} of {_totalCharts}" : string.Empty;
            StatusText = $"Showing {first}–{last} of {matchCount} matching charts · scanned {_rawCharts.Count} of {_totalCharts}{scanStatus}.";
        }

        private void MoveToPreviousPage()
        {
            if (!CanGoToPreviousPage) return;
            _currentPage--;
            ShowCurrentPage();
        }

        private async Task LoadNextPageAsync()
        {
            if (!CanGoToNextPage) return;

            IsSearching = true;
            var cancellation = new CancellationTokenSource();
            _searchCancellation = cancellation;
            try
            {
                int previousPage = _currentPage;
                _currentPage++;
                OnPropertyChanged(nameof(PageInfoText));
                ShowCurrentPage();

                int requiredMatchCount = _currentPage * SearchPageSize;
                await FillUntilMatchCountAsync(requiredMatchCount, cancellation.Token);
                if (GetFilteredCharts().Count <= (_currentPage - 1) * SearchPageSize)
                    _currentPage = previousPage;
                ShowCurrentPage();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                StatusText = $"Search error: {ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation))
                    _searchCancellation = null;
                cancellation.Dispose();
                IsSearching = false;
                if (_searchPending)
                {
                    _searchPending = false;
                    RequestSearch();
                }
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
