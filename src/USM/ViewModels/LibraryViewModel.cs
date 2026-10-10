using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using USM.Core.Models;
using USM.Core.Parsers;
using USM.Core.Services;
using USM.Core.Services.Quest;

namespace USM.ViewModels
{
    public sealed class QuestSongListItem : ViewModelBase
    {
        public string FileName { get; }
        public string Title { get; }
        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public QuestSongListItem(string fileName)
        {
            FileName = fileName;
            Title = Path.GetFileNameWithoutExtension(fileName);
        }
    }

    public class LibraryViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;
        private readonly QuestViewModel _questViewModel;
        private readonly SemaphoreSlim _questSongsRefreshGate = new(1, 1);

        private string _filterQuery = string.Empty;
        private int _selectedTrackTypeFilterIndex;
        private string _songCountText = "0 songs";
        private bool _allSelected;
        private InstalledSong? _currentlyPreviewingSong;
        private bool _isQuestSongsAvailable;
        private bool _isRefreshingQuestSongs;
        private bool _isResyncingCharts;
        private string _questSongsStatusText = "Connect a Quest to manage its songs.";

        private readonly List<InstalledSong> _allSongs = new();

        public string FilterQuery
        {
            get => _filterQuery;
            set
            {
                if (SetProperty(ref _filterQuery, value))
                {
                    ApplyFilter();
                }
            }
        }

        public int SelectedTrackTypeFilterIndex
        {
            get => _selectedTrackTypeFilterIndex;
            set
            {
                if (SetProperty(ref _selectedTrackTypeFilterIndex, value))
                {
                    ApplyFilter();
                }
            }
        }

        public string SongCountText
        {
            get => _songCountText;
            set => SetProperty(ref _songCountText, value);
        }

        public int OutdatedChartCount => _allSongs.Count(song => song.NeedsChartResync);
        public bool HasOutdatedCharts => OutdatedChartCount > 0;
        public string ResyncChartsText => IsResyncingCharts
            ? "⏳ Resyncing charts..."
            : $"↻ Resync Charts ({OutdatedChartCount})";

        public bool IsResyncingCharts
        {
            get => _isResyncingCharts;
            private set
            {
                if (SetProperty(ref _isResyncingCharts, value))
                {
                    OnPropertyChanged(nameof(ResyncChartsText));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool AllSelected
        {
            get => _allSelected;
            set
            {
                if (SetProperty(ref _allSelected, value))
                {
                    foreach (var s in DisplayedSongs) s.IsSelected = value;
                    OnPropertyChanged(nameof(DisplayedSongs));
                }
            }
        }

        public string SelectAllText => DisplayedSongs.Count > 0 && DisplayedSongs.All(s => s.IsSelected)
            ? "Deselect All"
            : "Select All";

        public string DeleteSelectedText
        {
            get
            {
                int count = DisplayedSongs.Count(s => s.IsSelected);
                return count > 0 ? $"🗑 Delete Selected ({count})" : "🗑 Delete Selected";
            }
        }

        public ObservableCollection<InstalledSong> DisplayedSongs { get; } = new();
        public ObservableCollection<QuestSongListItem> QuestSongs { get; } = new();

        public bool IsQuestSongsAvailable
        {
            get => _isQuestSongsAvailable;
            private set => SetProperty(ref _isQuestSongsAvailable, value);
        }

        public bool IsRefreshingQuestSongs
        {
            get => _isRefreshingQuestSongs;
            private set
            {
                if (SetProperty(ref _isRefreshingQuestSongs, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasNoQuestSongs => IsQuestSongsAvailable && QuestSongs.Count == 0 &&
            !IsRefreshingQuestSongs && QuestSongsStatusText.StartsWith("No .usm songs found", StringComparison.OrdinalIgnoreCase);
        public string QuestSongsCountText => $"{QuestSongs.Count} songs";
        public string SelectAllQuestSongsText => QuestSongs.Count > 0 && QuestSongs.All(song => song.IsSelected)
            ? "Deselect All"
            : "Select All";
        public string DeleteSelectedQuestSongsText
        {
            get
            {
                int count = QuestSongs.Count(song => song.IsSelected);
                return count > 0 ? $"🗑 Delete Selected ({count})" : "🗑 Delete Selected";
            }
        }

        public string QuestSongsStatusText
        {
            get => _questSongsStatusText;
            private set => SetProperty(ref _questSongsStatusText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand OpenCustomSongsCommand { get; }
        public ICommand ToggleSelectAllCommand { get; }
        public ICommand ToggleSongSelectionCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand DeleteSongCommand { get; }
        public ICommand OpenSongFolderCommand { get; }
        public ICommand PreviewSongCommand { get; }
        public ICommand ResyncChartsCommand { get; }
        public ICommand RefreshQuestSongsCommand { get; }
        public ICommand DeleteQuestSongCommand { get; }
        public ICommand ToggleSelectAllQuestSongsCommand { get; }
        public ICommand DeleteSelectedQuestSongsCommand { get; }

        public LibraryViewModel(GameDetector gameDetector, Action<string> notifyCallback, QuestViewModel questViewModel)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;
            _questViewModel = questViewModel;

            RefreshCommand = new RelayCommand(RefreshLibrary);
            OpenCustomSongsCommand = new RelayCommand(OpenCustomSongsFolder);
            ToggleSelectAllCommand = new RelayCommand(ToggleSelectAll);
            ToggleSongSelectionCommand = new RelayCommand(ToggleSongSelection);
            DeleteSelectedCommand = new RelayCommand(DeleteSelectedPrompt);
            DeleteSongCommand = new RelayCommand(DeleteSingleSong);
            OpenSongFolderCommand = new RelayCommand(OpenSongFolder);
            PreviewSongCommand = new RelayCommand(PreviewSong);
            ResyncChartsCommand = new AsyncRelayCommand(_ => ResyncOutdatedChartsAsync(),
                _ => !IsResyncingCharts && HasOutdatedCharts);
            RefreshQuestSongsCommand = new AsyncRelayCommand(_ => RefreshQuestSongsAsync(), _ => !IsRefreshingQuestSongs);
            DeleteQuestSongCommand = new AsyncRelayCommand(DeleteQuestSongAsync, _ => IsQuestSongsAvailable && !IsRefreshingQuestSongs);
            ToggleSelectAllQuestSongsCommand = new RelayCommand(_ => ToggleSelectAllQuestSongs(), _ => QuestSongs.Count > 0 && !IsRefreshingQuestSongs);
            DeleteSelectedQuestSongsCommand = new AsyncRelayCommand(_ => DeleteSelectedQuestSongsAsync(),
                _ => IsQuestSongsAvailable && !IsRefreshingQuestSongs && QuestSongs.Any(song => song.IsSelected));
            AudioPlayerService.Instance.PlaybackStopped += OnPreviewStopped;
            _questViewModel.PropertyChanged += OnQuestViewModelPropertyChanged;
            _questViewModel.QuestSongsChanged += OnQuestSongsChanged;

            RefreshLibrary();
            _ = RefreshQuestSongsAsync();
        }

        private void OnQuestViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(QuestViewModel.SelectedDevice))
                _ = RefreshQuestSongsAsync();
        }

        private void OnQuestSongsChanged(object? sender, EventArgs e) => _ = RefreshQuestSongsAsync();

        private async Task RefreshQuestSongsAsync()
        {
            await _questSongsRefreshGate.WaitAsync();
            try
            {
                QuestDevice? device = _questViewModel.SelectedDevice;
                if (device == null || !device.Status.Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    ClearQuestSongs();
                    IsQuestSongsAvailable = false;
                    QuestSongsStatusText = "Connect a Quest to manage its songs.";
                    OnPropertyChanged(nameof(QuestSongsCountText));
                    OnPropertyChanged(nameof(HasNoQuestSongs));
                    UpdateQuestSongSelectionState();
                    return;
                }

                IsRefreshingQuestSongs = true;
                QuestSongsStatusText = "Checking the Quest custom-songs folder...";
                var result = await _questViewModel.QuestService.ListQuestSongsAsync(device.Serial);

                if (_questViewModel.SelectedDevice?.Serial != device.Serial) return;

                ClearQuestSongs();
                foreach (string song in result.SongFiles)
                {
                    var item = new QuestSongListItem(song);
                    item.PropertyChanged += OnQuestSongPropertyChanged;
                    QuestSongs.Add(item);
                }
                IsQuestSongsAvailable = result.DirectoryExists;
                QuestSongsStatusText = result.DirectoryExists
                    ? result.Message
                    : "The Quest custom-songs folder does not exist.";
                OnPropertyChanged(nameof(QuestSongsCountText));
                OnPropertyChanged(nameof(HasNoQuestSongs));
                UpdateQuestSongSelectionState();
            }
            catch (Exception ex)
            {
                ClearQuestSongs();
                IsQuestSongsAvailable = false;
                QuestSongsStatusText = $"Could not read Quest songs: {ex.Message}";
                OnPropertyChanged(nameof(QuestSongsCountText));
                OnPropertyChanged(nameof(HasNoQuestSongs));
                UpdateQuestSongSelectionState();
            }
            finally
            {
                IsRefreshingQuestSongs = false;
                OnPropertyChanged(nameof(HasNoQuestSongs));
                _questSongsRefreshGate.Release();
            }
        }

        private void ClearQuestSongs()
        {
            foreach (QuestSongListItem song in QuestSongs)
                song.PropertyChanged -= OnQuestSongPropertyChanged;
            QuestSongs.Clear();
        }

        private void OnQuestSongPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(QuestSongListItem.IsSelected))
                UpdateQuestSongSelectionState();
        }

        private void UpdateQuestSongSelectionState()
        {
            OnPropertyChanged(nameof(SelectAllQuestSongsText));
            OnPropertyChanged(nameof(DeleteSelectedQuestSongsText));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ToggleSelectAllQuestSongs()
        {
            if (QuestSongs.Count == 0) return;
            bool select = !QuestSongs.All(song => song.IsSelected);
            foreach (QuestSongListItem song in QuestSongs)
                song.IsSelected = select;
            UpdateQuestSongSelectionState();
        }

        private async Task DeleteSelectedQuestSongsAsync()
        {
            List<QuestSongListItem> selected = QuestSongs.Where(song => song.IsSelected).ToList();
            if (selected.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Delete {selected.Count} selected song(s) from the Quest? This cannot be undone.",
                "Delete Quest Songs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            QuestDevice? device = _questViewModel.SelectedDevice;
            if (device == null || !device.Status.Equals("device", StringComparison.OrdinalIgnoreCase)) return;

            IsRefreshingQuestSongs = true;
            try
            {
                var failures = new List<string>();
                foreach (QuestSongListItem song in selected)
                {
                    var result = await _questViewModel.QuestService.DeleteQuestSongAsync(device.Serial, song.FileName);
                    if (!result.Success) failures.Add($"{song.Title}: {result.Message}");
                }

                if (failures.Count == 0)
                    _notifyCallback($"Deleted {selected.Count} song(s) from the Quest.");
                else
                    _notifyCallback($"Some Quest songs could not be deleted: {string.Join(" | ", failures)}");

                await RefreshQuestSongsAsync();
            }
            finally
            {
                IsRefreshingQuestSongs = false;
                OnPropertyChanged(nameof(HasNoQuestSongs));
                UpdateQuestSongSelectionState();
            }
        }

        private async Task DeleteQuestSongAsync(object? parameter)
        {
            if (parameter is not string fileName || !IsQuestSongsAvailable) return;
            QuestDevice? device = _questViewModel.SelectedDevice;
            if (device == null || !device.Status.Equals("device", StringComparison.OrdinalIgnoreCase)) return;

            string title = Path.GetFileNameWithoutExtension(fileName);
            var confirm = MessageBox.Show(
                $"Delete '{title}' from the Quest? This cannot be undone.",
                "Delete Quest Song",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            IsRefreshingQuestSongs = true;
            try
            {
                var result = await _questViewModel.QuestService.DeleteQuestSongAsync(device.Serial, fileName);
                if (result.Success)
                {
                    _notifyCallback(result.Message);
                    await RefreshQuestSongsAsync();
                }
                else
                {
                    _notifyCallback($"Could not delete Quest song: {result.Message}");
                }
            }
            finally
            {
                IsRefreshingQuestSongs = false;
                OnPropertyChanged(nameof(HasNoQuestSongs));
            }
        }

        public void OpenCustomSongsFolder()
        {
            string? dir = _gameDetector.GetCustomSongsDir();
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }

        public void RefreshLibrary()
        {
            _allSongs.Clear();
            string? customSongsDir = _gameDetector.GetCustomSongsDir();


            if (!string.IsNullOrEmpty(customSongsDir) && Directory.Exists(customSongsDir))
            {


                foreach (var dir in Directory.GetFiles(customSongsDir, "*.usm"))

                {
                    var song = LoadInstalledSong(dir);
                    if (song != null)
                    {
                        _allSongs.Add(song);
                    }
                }
            }

            _allSongs.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            ApplyFilter();
            OnPropertyChanged(nameof(OutdatedChartCount));
            OnPropertyChanged(nameof(HasOutdatedCharts));
            OnPropertyChanged(nameof(ResyncChartsText));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task ResyncOutdatedChartsAsync()
        {
            List<InstalledSong> selectedSongs = _allSongs.Where(song => song.IsSelected).ToList();
            IEnumerable<InstalledSong> scope = selectedSongs.Count > 0 ? selectedSongs : _allSongs;
            List<InstalledSong> targets = scope.Where(song => song.NeedsChartResync).ToList();
            if (targets.Count == 0)
            {
                _notifyCallback("The selected songs already use the current chart parser.");
                return;
            }

            IsResyncingCharts = true;
            int updated = 0;
            var issues = new List<string>();
            try
            {
                foreach (InstalledSong song in targets)
                {
                    string tempRoot = Path.Combine(Path.GetTempPath(), $"usm_resync_{Guid.NewGuid():N}");
                    try
                    {
                        _notifyCallback($"Resyncing chart: {song.Artist} - {song.Title}");
                        var (sourceChart, resolutionMessage) = await ResolveChorusChartAsync(song);
                        if (sourceChart == null)
                        {
                            issues.Add($"{song.Artist} - {song.Title}: {resolutionMessage}");
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(sourceChart.Md5))
                        {
                            issues.Add($"{song.Artist} - {song.Title}: Chorus did not provide a chart file hash; no files were changed.");
                            continue;
                        }

                        string downloadUrl = sourceChart.DownloadUrl ??
                            $"https://files.enchor.us/{Uri.EscapeDataString(sourceChart.Md5)}.sng";
                        string chartSourceDir = Path.Combine(tempRoot, "chart");
                        await SngDecoder.DecodeRemoteAsync(
                            downloadUrl,
                            chartSourceDir,
                            includeFile: fileName => fileName.Equals("notes.mid", StringComparison.OrdinalIgnoreCase)
                                || fileName.Equals("notes.chart", StringComparison.OrdinalIgnoreCase)
                                || fileName.Equals("song.chart", StringComparison.OrdinalIgnoreCase));
                        SongIni resyncIni = SongConverter.ParseSongIni(Path.Combine(chartSourceDir, "song.ini"));
                        var jsonCharts = SongConverter.GenerateJsonCharts(
                            chartSourceDir,
                            null,
                            resyncIni.DurationSeconds,
                            out int sourceDifficultyCount,
                            out int sourceNoteCount,
                            out double chartCoverageRatio);
                        UsmPacker.ReplaceChartData(
                            song.FolderPath,
                            jsonCharts,
                            sourceChart.Id,
                            sourceChart.Md5,
                            JsonGenerator.ParserVersion,
                            sourceDifficultyCount,
                            sourceNoteCount,
                            chartCoverageRatio,
                            resyncIni.DurationSeconds);
                        updated++;
                    }
                    catch (Exception ex)
                    {
                        issues.Add($"{song.Artist} - {song.Title}: {ex.Message}");
                    }
                    finally
                    {
                        try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
                    }
                }
            }
            finally
            {
                IsResyncingCharts = false;
                RefreshLibrary();
            }

            string summary = $"Chart resync complete: {updated} updated, {issues.Count} skipped or failed.";
            if (issues.Count == 0)
            {
                _notifyCallback(summary);
            }
            else
            {
                string details = string.Join(Environment.NewLine, issues.Take(8));
                if (issues.Count > 8) details += Environment.NewLine + $"...and {issues.Count - 8} more.";
                MessageBox.Show($"{summary}{Environment.NewLine}{Environment.NewLine}{details}",
                    "USM Chart Resync", MessageBoxButton.OK,
                    updated > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }

        private static async Task<(ChorusChart? Chart, string Message)> ResolveChorusChartAsync(InstalledSong song)
        {
            if (!string.IsNullOrWhiteSpace(song.ChorusChartMd5))
            {
                string md5 = song.ChorusChartMd5.Trim();
                return (new ChorusChart
                {
                    Id = song.ChorusChartId ?? md5,
                    Md5 = md5,
                    Title = song.Title,
                    Artist = song.Artist,
                    DownloadUrl = $"https://files.enchor.us/{Uri.EscapeDataString(md5)}.sng"
                }, "Using saved Chorus chart ID.");
            }

            string query = string.Join(" ", new[] { song.Title, song.Artist }
                .Where(value => !string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(query))
                return (null, "No title or artist metadata is available to identify its original chart.");

            var search = await ChorusClient.SearchAsync(query, 1, 60, "guitar");
            List<ChorusChart> exactMatches = search.Charts.Where(chart => !string.IsNullOrWhiteSpace(chart.Md5) &&
                MatchKey(chart.Title) == MatchKey(song.Title) &&
                MatchKey(chart.Artist) == MatchKey(song.Artist)).ToList();

            if (exactMatches.Count == 1)
                return (exactMatches[0], "Matched by exact title and artist.");

            if (exactMatches.Count > 1)
            {
                var selected = ChooseChorusMatch(song, exactMatches);
                return selected != null
                    ? (selected, "Selected Chorus chart match.")
                    : (null, "Multiple exact Chorus matches were found; no chart was changed.");
            }

            List<ChorusChart> titleMatches = search.Charts
                .Where(chart => !string.IsNullOrWhiteSpace(chart.Md5) && MatchKey(chart.Title) == MatchKey(song.Title))
                .Take(12)
                .ToList();
            if (titleMatches.Count == 0)
                return (null, "No exact title match was found on Chorus; this song was left unchanged.");

            var manuallySelected = ChooseChorusMatch(song, titleMatches);
            return manuallySelected != null
                ? (manuallySelected, "Selected Chorus chart match.")
                : (null, "A title match exists but the artist differs; no chart was changed.");
        }

        private static ChorusChart? ChooseChorusMatch(InstalledSong song, IReadOnlyList<ChorusChart> candidates)
        {
            var dialog = new Window
            {
                Title = $"Choose Chorus chart for {song.Title}",
                Width = 680,
                Height = 390,
                MinWidth = 520,
                MinHeight = 300,
                WindowStartupLocation = Application.Current?.MainWindow != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(25, 27, 39)),
                Foreground = Brushes.White,
                ResizeMode = ResizeMode.CanResize
            };
            if (Application.Current?.MainWindow != null && Application.Current.MainWindow != dialog)
                dialog.Owner = Application.Current.MainWindow;

            var layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = $"Several possible official charts were found for {song.Artist} — {song.Title}. Choose the matching one:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Brushes.White
            };
            Grid.SetRow(heading, 0);
            layout.Children.Add(heading);

            var choices = new ListBox
            {
                ItemsSource = candidates,
                DisplayMemberPath = nameof(ChorusChart.ResyncChoiceLabel),
                Background = new SolidColorBrush(Color.FromRgb(31, 34, 48)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 66, 86)),
                Margin = new Thickness(0, 0, 0, 14)
            };
            if (candidates.Count == 1) choices.SelectedIndex = 0;
            Grid.SetRow(choices, 1);
            layout.Children.Add(choices);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 7, 12, 7) };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            var choose = new Button { Content = "Use Selected Chart", MinWidth = 140, Padding = new Thickness(12, 7, 12, 7), IsDefault = true };
            choose.Click += (_, _) =>
            {
                if (choices.SelectedItem != null) dialog.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(choose);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);

            dialog.Content = layout;
            return dialog.ShowDialog() == true ? choices.SelectedItem as ChorusChart : null;
        }

        private static string MatchKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (char character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            }
            return builder.ToString();
        }

        private static InstalledSong? LoadInstalledSong(string dir)
        {
            string folderName = Path.GetFileNameWithoutExtension(dir);
            string title = folderName;
            string artist = "Unknown Artist";
            string charter = "Unknown";
            bool isMulti = false;
            bool isHalfTrack = false;
            bool isIdealForUnplugged = false;
            int audioStemCount = 0;
            int sourceDifficultyCount = 0;
            double chartCoverageRatio = 0;
            bool hasKnownAudioLayout = false;
            string? chorusChartId = null;
            string? chorusChartMd5 = null;
            int chartParserVersion = 0;
            BitmapImage? coverImage = null;

            try
            {
                using var archive = ZipFile.OpenRead(dir);
                var metaEntry = FindEntry(archive, "meta.json");
                if (metaEntry != null)
                {
                    using var metaStream = metaEntry.Open();
                    using var document = JsonDocument.Parse(metaStream);
                    var meta = document.RootElement;
                    title = CleanUsmDisplayName(ReadMetaValue(meta, "SongName", "Name") ?? title);
                    artist = ReadMetaValue(meta, "BandName", "Artist") ?? artist;
                    charter = ReadMetaValue(meta, "LevelAuthorName", "Charter") ?? charter;
                    chorusChartId = ReadMetaValue(meta, "ChorusChartId", "SourceChartId");
                    chorusChartMd5 = ReadMetaValue(meta, "ChorusChartMd5", "SourceChartMd5");
                    chartParserVersion = ReadMetaInt(meta, "ChartParserVersion", "ParserVersion");
                    audioStemCount = ReadMetaInt(meta, "AudioStemCount");
                    sourceDifficultyCount = ReadMetaInt(meta, "SourceDifficultyCount");
                    if (meta.TryGetProperty("ChartCoverageRatio", out var coverageValue))
                    {
                        if (coverageValue.ValueKind == JsonValueKind.Number && coverageValue.TryGetDouble(out double coverage))
                            chartCoverageRatio = Math.Clamp(coverage, 0, 1);
                        else if (coverageValue.ValueKind == JsonValueKind.String && double.TryParse(coverageValue.GetString(), out double parsedCoverage))
                            chartCoverageRatio = Math.Clamp(parsedCoverage, 0, 1);
                    }
                    if (meta.TryGetProperty("IsIdealForUnplugged", out var idealValue))
                    {
                        isIdealForUnplugged = idealValue.ValueKind == JsonValueKind.True ||
                            (idealValue.ValueKind == JsonValueKind.String && bool.TryParse(idealValue.GetString(), out var parsedIdeal) && parsedIdeal);
                    }
                    if (meta.TryGetProperty("AudioLayout", out var audioLayout) && audioLayout.ValueKind == JsonValueKind.String)
                    {
                        string layout = audioLayout.GetString() ?? string.Empty;
                        if (string.Equals(layout, "two-track-instrumental-vocals", StringComparison.OrdinalIgnoreCase))
                        {
                            isHalfTrack = true;
                            isMulti = true;
                            hasKnownAudioLayout = true;
                        }
                        else if (string.Equals(layout, "full-multitrack", StringComparison.OrdinalIgnoreCase))
                        {
                            isMulti = true;
                            hasKnownAudioLayout = true;
                        }
                        else if (string.Equals(layout, "single-track", StringComparison.OrdinalIgnoreCase))
                        {
                            hasKnownAudioLayout = true;
                        }
                    }
                    if (!hasKnownAudioLayout && meta.TryGetProperty("IsMultiTrack", out var multiTrack))
                    {
                        isMulti = multiTrack.ValueKind == JsonValueKind.True ||
                            (multiTrack.ValueKind == JsonValueKind.String && bool.TryParse(multiTrack.GetString(), out var parsed) && parsed);
                    }
                }

                var coverEntry = FindEntry(archive, "cover.jpg", "album.jpg", "cover.jpeg", "album.jpeg", "cover.png", "album.png")
                    ?? archive.Entries.FirstOrDefault(entry => entry.Length > 0 &&
                        (entry.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));
                if (coverEntry != null)
                {
                    using var coverStream = coverEntry.Open();
                    using var memory = new MemoryStream();
                    coverStream.CopyTo(memory);
                    memory.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 180;
                    bitmap.StreamSource = memory;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    coverImage = bitmap;
                }

                if (!hasKnownAudioLayout)
                {
                    isMulti |= archive.Entries.Any(entry => entry.Name.StartsWith("song-", StringComparison.OrdinalIgnoreCase)
                        && entry.Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // Keep a readable fallback card when an archive is damaged or has unexpected metadata.
            }

            return new InstalledSong
            {
                FolderName = folderName,
                FolderPath = dir,
                Title = title,
                Artist = artist,
                Charter = charter,
                CoverPath = null,
                CoverImage = coverImage,
                AudioPath = null,
                IsMultiTrack = isMulti,
                IsHalfTrack = isHalfTrack,
                IsIdealForUnplugged = isIdealForUnplugged,
                AudioStemCount = audioStemCount,
                SourceDifficultyCount = sourceDifficultyCount,
                ChartCoverageRatio = chartCoverageRatio,
                ChorusChartId = chorusChartId,
                ChorusChartMd5 = chorusChartMd5,
                ChartParserVersion = chartParserVersion
            };
        }

        private static ZipArchiveEntry? FindEntry(ZipArchive archive, params string[] names)
        {
            return archive.Entries.FirstOrDefault(entry => names.Any(name =>
                string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        private static string? ReadMetaValue(JsonElement meta, params string[] names)
        {
            foreach (var property in meta.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var text = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            return null;
        }

        private static int ReadMetaInt(JsonElement meta, params string[] names)
        {
            foreach (var property in meta.EnumerateObject())
            {
                if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number))
                    return number;
                if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out number))
                    return number;
            }
            return 0;
        }

        private static string CleanUsmDisplayName(string value)
        {
            string cleaned = value.Trim();
            return cleaned.EndsWith(".usm", StringComparison.OrdinalIgnoreCase)
                ? cleaned[..^4].TrimEnd()
                : cleaned;
        }

        private static bool DeleteDirectorySafe(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return !File.Exists(path);
                }
                catch { return false; }
            }

            if (!Directory.Exists(path)) return true;
            try
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes = FileAttributes.Normal;
                }
                foreach (var sub in di.GetDirectories("*", SearchOption.AllDirectories))
                {
                    sub.Attributes = FileAttributes.Normal;
                }
                di.Attributes = FileAttributes.Normal;

                GC.Collect();
                GC.WaitForPendingFinalizers();

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                System.Threading.Thread.Sleep(60);
                try
                {
                    Directory.Delete(path, recursive: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private void ApplyFilter()
        {
            DisplayedSongs.Clear();
            string q = FilterQuery.Trim().ToLowerInvariant();

            IEnumerable<InstalledSong> matches = string.IsNullOrEmpty(q)
                ? _allSongs
                : _allSongs.Where(s =>
                    s.Title.ToLowerInvariant().Contains(q) ||
                    s.Artist.ToLowerInvariant().Contains(q) ||
                    s.Charter.ToLowerInvariant().Contains(q) ||
                    s.FolderName.ToLowerInvariant().Contains(q));

            matches = SelectedTrackTypeFilterIndex switch
            {
                1 => matches.Where(song => song.IsMultiTrack && !song.IsHalfTrack),
                2 => matches.Where(song => song.IsHalfTrack),
                3 => matches.Where(song => !song.IsMultiTrack),
                _ => matches
            };

            foreach (var song in matches)
            {
                DisplayedSongs.Add(song);
            }

            SongCountText = $"{DisplayedSongs.Count} of {_allSongs.Count} songs";
            UpdateSelectionState();
        }

        public void UpdateSelectionState()
        {
            OnPropertyChanged(nameof(SelectAllText));
            OnPropertyChanged(nameof(DeleteSelectedText));
        }

        public void ToggleSongSelection(object? param)
        {
            if (param is InstalledSong song)
            {
                song.IsSelected = !song.IsSelected;
                UpdateSelectionState();
            }
        }

        private void ToggleSelectAll()
        {
            if (DisplayedSongs.Count == 0) return;
            bool target = !DisplayedSongs.All(s => s.IsSelected);
            foreach (var s in DisplayedSongs)
            {
                s.IsSelected = target;
            }
            UpdateSelectionState();
        }

        private void DeleteSelectedPrompt()
        {
            var selected = DisplayedSongs.Where(s => s.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("No songs selected! Click on any song card to select it first.", "USM", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var res = MessageBox.Show(
                $"Are you sure you want to delete {selected.Count} selected song(s)? This will remove them from your VR turntable.",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (res == MessageBoxResult.Yes)
            {
                AudioPlayerService.Instance.Stop();
                int deletedCount = 0;
                var failedSongs = new List<string>();

                foreach (var song in selected)
                {
                    if (DeleteDirectorySafe(song.FolderPath))
                    {
                        deletedCount++;
                    }
                    else
                    {
                        failedSongs.Add(song.Title);
                    }
                }

                RefreshLibrary();

                if (failedSongs.Count > 0)
                {
                    MessageBox.Show(
                        $"Could not delete the following song(s) because they may be in use by another program:\n{string.Join("\n", failedSongs)}",
                        "Deletion Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }

                if (deletedCount > 0)
                {
                    _notifyCallback?.Invoke($"Deleted {deletedCount} song(s) from CustomSongs.");
                }
            }
        }

        private void DeleteSingleSong(object? param)
        {
            if (param is not InstalledSong song) return;

            var res = MessageBox.Show(
                $"Delete '{song.Artist} - {song.Title}'?",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (res == MessageBoxResult.Yes)
            {
                AudioPlayerService.Instance.Stop();
                if (DeleteDirectorySafe(song.FolderPath))
                {
                    RefreshLibrary();
                    _notifyCallback?.Invoke($"Deleted: {song.Title}");
                }
                else
                {
                    MessageBox.Show(
                        $"Could not delete '{song.Title}'. The folder or files may be in use by another program.",
                        "Deletion Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    RefreshLibrary();
                }
            }
        }

        private void OpenSongFolder(object? param)
        {
            if (param is not InstalledSong song) return;
            string? folder = Directory.Exists(song.FolderPath)
                ? song.FolderPath
                : Path.GetDirectoryName(song.FolderPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }

        private async void PreviewSong(object? param)
        {
            if (param is not InstalledSong song || !File.Exists(song.FolderPath)) return;

            string? previewPath = await ExtractPreviewAudioAsync(song.FolderPath);
            if (string.IsNullOrEmpty(previewPath))
            {
                _notifyCallback?.Invoke($"Could not prepare preview for '{song.Title}'. Check that the .usm contains sample.ogg or song.ogg.");
                return;
            }

            var player = AudioPlayerService.Instance;
            if (player.IsPlaying && string.Equals(player.CurrentSource, previewPath, StringComparison.OrdinalIgnoreCase))
            {
                player.Stop();
                song.IsPlaying = false;
                _currentlyPreviewingSong = null;
            }
            else
            {
                foreach (var s in _allSongs) s.IsPlaying = false;
                player.Play(previewPath);
                _currentlyPreviewingSong = player.IsPlaying ? song : null;
                song.IsPlaying = player.IsPlaying;
            }
            OnPropertyChanged(nameof(DisplayedSongs));
        }

        private static async Task<string?> ExtractPreviewAudioAsync(string archivePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var audioEntry = FindEntry(archive,
                    "sample.ogg", "preview.ogg", "sample.wav", "preview.wav", "sample.mp3", "preview.mp3",
                    "song.ogg", "song.mp3", "song.wav", "song.opus", "song.flac", "song.m4a")
                    ?? archive.Entries.FirstOrDefault(entry =>
                        !entry.Name.StartsWith("song-", StringComparison.OrdinalIgnoreCase) &&
                        new[] { ".ogg", ".opus", ".mp3", ".wav", ".flac", ".m4a" }
                            .Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase));
                if (audioEntry == null || audioEntry.Length == 0) return null;

                var info = new FileInfo(archivePath);
                string cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USM", "PreviewCache");
                Directory.CreateDirectory(cacheRoot);
                string keySource = $"{Path.GetFullPath(archivePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySource))).Substring(0, 20);
                string extension = Path.GetExtension(audioEntry.Name).ToLowerInvariant();
                string extractedPath = Path.Combine(cacheRoot, key + extension);
                string playablePath = extension == ".wav" ? extractedPath : Path.Combine(cacheRoot, key + ".wav");

                if (!File.Exists(extractedPath))
                {
                    using var entryStream = audioEntry.Open();
                    using var output = new FileStream(extractedPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await entryStream.CopyToAsync(output);
                }

                // WPF's MediaPlayer cannot decode OGG on all Windows builds. Convert
                // every non-WAV preview to a short PCM WAV for reliable playback.
                if (extension != ".wav" && !File.Exists(playablePath))
                {
                    try
                    {
                        await AudioPipeline.RunFfmpegAsync($"-y -i \"{extractedPath}\" -vn -ac 2 -ar 44100 -t 30 -c:a pcm_s16le \"{playablePath}\"");
                    }
                    catch
                    {
                        try { File.Delete(playablePath); } catch { }
                        return null;
                    }
                }

                return File.Exists(playablePath) && new FileInfo(playablePath).Length > 44 ? playablePath : null;
            }
            catch
            {
                return null;
            }
        }

        private void OnPreviewStopped()
        {
            var songs = _allSongs.Where(song => song.IsPlaying).ToList();
            if (_currentlyPreviewingSong != null && !songs.Contains(_currentlyPreviewingSong))
                songs.Add(_currentlyPreviewingSong);
            _currentlyPreviewingSong = null;
            if (songs.Count == 0) return;

            void UpdateState()
            {
                foreach (var song in songs) song.IsPlaying = false;
                OnPropertyChanged(nameof(DisplayedSongs));
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) UpdateState();
            else dispatcher.BeginInvoke((Action)UpdateState);
        }
    }
}
