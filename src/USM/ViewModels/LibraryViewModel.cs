using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using USM.Core.Models;
using USM.Core.Services;

namespace USM.ViewModels
{
    public class LibraryViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;

        private string _filterQuery = string.Empty;
        private string _songCountText = "0 songs";
        private bool _allSelected;

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

        public string SongCountText
        {
            get => _songCountText;
            set => SetProperty(ref _songCountText, value);
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

        public ICommand RefreshCommand { get; }
        public ICommand OpenCustomSongsCommand { get; }
        public ICommand ToggleSelectAllCommand { get; }
        public ICommand ToggleSongSelectionCommand { get; }
        public ICommand DeleteSelectedCommand { get; }
        public ICommand DeleteSongCommand { get; }
        public ICommand OpenSongFolderCommand { get; }
        public ICommand PreviewSongCommand { get; }

        public LibraryViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            RefreshCommand = new RelayCommand(RefreshLibrary);
            OpenCustomSongsCommand = new RelayCommand(OpenCustomSongsFolder);
            ToggleSelectAllCommand = new RelayCommand(ToggleSelectAll);
            ToggleSongSelectionCommand = new RelayCommand(ToggleSongSelection);
            DeleteSelectedCommand = new RelayCommand(DeleteSelectedPrompt);
            DeleteSongCommand = new RelayCommand(DeleteSingleSong);
            OpenSongFolderCommand = new RelayCommand(OpenSongFolder);
            PreviewSongCommand = new RelayCommand(PreviewSong);

            RefreshLibrary();
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
                foreach (var dir in Directory.GetDirectories(customSongsDir))
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
        }

        private static InstalledSong? LoadInstalledSong(string dir)
        {
            string folderName = Path.GetFileName(dir);
            string dataXml = Path.Combine(dir, "data.xml");
            string songIni = Path.Combine(dir, "song.ini");

            string title = folderName;
            string artist = "Unknown Artist";
            string charter = "Unknown";
            bool isMulti = false;

            if (File.Exists(dataXml))
            {
                try
                {
                    var doc = XDocument.Load(dataXml);
                    var root = doc.Root;
                    if (root != null)
                    {
                        title = root.Element("SongName")?.Value ?? title;
                        artist = root.Element("BandName")?.Value ?? artist;
                        charter = root.Element("LevelAuthorName")?.Value ?? charter;
                        string multiStr = root.Element("IsMultiTrack")?.Value ?? "false";
                        isMulti = multiStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }
            }
            else if (File.Exists(songIni))
            {
                var ini = SongConverter.ParseSongIni(songIni);
                title = ini.Name;
                artist = ini.Artist;
                charter = ini.Charter;
                isMulti = File.Exists(Path.Combine(dir, "song-guitar.mp3")) && File.Exists(Path.Combine(dir, "song-drums.mp3"));
            }

            string coverPath = Path.Combine(dir, "album.jpg");
            if (!File.Exists(coverPath)) coverPath = Path.Combine(dir, "album.png");
            if (!File.Exists(coverPath)) coverPath = Path.Combine(dir, "cover.jpg");

            string samplePath = Path.Combine(dir, "sample.mp3");
            if (!File.Exists(samplePath)) samplePath = Path.Combine(dir, "song.mp3");

            return new InstalledSong
            {
                FolderName = folderName,
                FolderPath = dir,
                Title = title,
                Artist = artist,
                Charter = charter,
                CoverPath = File.Exists(coverPath) ? coverPath : null,
                AudioPath = File.Exists(samplePath) ? samplePath : null,
                IsMultiTrack = isMulti
            };
        }

        private void ApplyFilter()
        {
            DisplayedSongs.Clear();
            string q = FilterQuery.Trim().ToLowerInvariant();

            var matches = string.IsNullOrEmpty(q)
                ? _allSongs
                : _allSongs.Where(s =>
                    s.Title.ToLowerInvariant().Contains(q) ||
                    s.Artist.ToLowerInvariant().Contains(q) ||
                    s.Charter.ToLowerInvariant().Contains(q) ||
                    s.FolderName.ToLowerInvariant().Contains(q));

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
                foreach (var song in selected)
                {
                    try
                    {
                        if (Directory.Exists(song.FolderPath))
                            Directory.Delete(song.FolderPath, recursive: true);
                    }
                    catch { }
                }
                RefreshLibrary();
                _notifyCallback?.Invoke($"Deleted {selected.Count} songs from CustomSongs.");
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
                try
                {
                    if (Directory.Exists(song.FolderPath))
                        Directory.Delete(song.FolderPath, recursive: true);
                }
                catch { }
                RefreshLibrary();
                _notifyCallback?.Invoke($"Deleted: {song.Title}");
            }
        }

        private void OpenSongFolder(object? param)
        {
            if (param is not InstalledSong song) return;
            if (Directory.Exists(song.FolderPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = song.FolderPath,
                    UseShellExecute = true
                });
            }
        }

        private void PreviewSong(object? param)
        {
            if (param is not InstalledSong song || string.IsNullOrEmpty(song.AudioPath)) return;

            var player = AudioPlayerService.Instance;
            if (player.IsPlaying && player.CurrentSource == song.AudioPath)
            {
                player.Stop();
                song.IsPlaying = false;
            }
            else
            {
                foreach (var s in DisplayedSongs) s.IsPlaying = false;
                player.Play(song.AudioPath);
                song.IsPlaying = true;
            }
            OnPropertyChanged(nameof(DisplayedSongs));
        }
    }
}
