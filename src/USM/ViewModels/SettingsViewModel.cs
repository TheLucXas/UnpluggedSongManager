using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using USM.Core.Services;

namespace USM.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;

        private string _gamePath = string.Empty;
        private bool _isGameFound;
        private bool _isMelonInstalled;
        private bool _isModInstalled;
        private bool _isCustomSongsReady;
        private bool _enableDiscordRpc;
        private bool _enableVerboseDebug;
        private string _modStatusMessage = string.Empty;

        public string GamePath
        {
            get => _gamePath;
            set => SetProperty(ref _gamePath, value);
        }

        public bool IsGameFound
        {
            get => _isGameFound;
            set => SetProperty(ref _isGameFound, value);
        }

        public bool IsMelonInstalled
        {
            get => _isMelonInstalled;
            set => SetProperty(ref _isMelonInstalled, value);
        }

        public bool IsModInstalled
        {
            get => _isModInstalled;
            set => SetProperty(ref _isModInstalled, value);
        }

        public bool IsCustomSongsReady
        {
            get => _isCustomSongsReady;
            set => SetProperty(ref _isCustomSongsReady, value);
        }

        public bool EnableDiscordRpc
        {
            get => _enableDiscordRpc;
            set
            {
                if (SetProperty(ref _enableDiscordRpc, value))
                {
                    SavePreferences();
                }
            }
        }

        public bool EnableVerboseDebug
        {
            get => _enableVerboseDebug;
            set
            {
                if (SetProperty(ref _enableVerboseDebug, value))
                {
                    SavePreferences();
                }
            }
        }

        public string ModStatusMessage
        {
            get => _modStatusMessage;
            set => SetProperty(ref _modStatusMessage, value);
        }

        public int[] AvailableDownloadLimits { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        public int MaxConcurrentDownloads
        {
            get => _gameDetector.MaxConcurrentDownloads;
            set
            {
                if (_gameDetector.MaxConcurrentDownloads != value)
                {
                    _gameDetector.MaxConcurrentDownloads = value;
                    OnPropertyChanged(nameof(MaxConcurrentDownloads));
                    _notifyCallback?.Invoke($"Maximum concurrent downloads set to {value}.");
                }
            }
        }

        public ICommand BrowseGamePathCommand { get; }
        public ICommand InstallModCommand { get; }

        public SettingsViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            BrowseGamePathCommand = new RelayCommand(BrowseGamePath);
            InstallModCommand = new RelayCommand(InstallMod);

            RefreshStatus();
        }

        public void RefreshStatus()
        {
            GamePath = _gameDetector.GamePath ?? string.Empty;
            IsGameFound = !string.IsNullOrEmpty(_gameDetector.GamePath) && GameDetector.IsValidGamePath(_gameDetector.GamePath);
            IsMelonInstalled = _gameDetector.IsMelonInstalled();
            IsModInstalled = _gameDetector.IsModInstalled();
            IsCustomSongsReady = Directory.Exists(_gameDetector.GetCustomSongsDir() ?? "");

            var (disc, verb) = _gameDetector.ReadPreferences();
            _enableDiscordRpc = disc;
            _enableVerboseDebug = verb;
            OnPropertyChanged(nameof(EnableDiscordRpc));
            OnPropertyChanged(nameof(EnableVerboseDebug));
            OnPropertyChanged(nameof(MaxConcurrentDownloads));
        }

        private void BrowseGamePath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Unplugged VR Installation Folder (contains Unplugged.exe)",
                Multiselect = false
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            {
                if (_gameDetector.SetGamePath(dlg.FolderName))
                {
                    RefreshStatus();
                    _notifyCallback?.Invoke("Unplugged game folder updated successfully!");
                }
                else
                {
                    MessageBox.Show("Selected directory does not appear to contain Unplugged.exe or Unplugged_Data.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void InstallMod()
        {
            if (_gameDetector.InstallOrUpdateModPlugin(out string msg))
            {
                ModStatusMessage = msg;
                RefreshStatus();
                _notifyCallback?.Invoke("UnpluggedCustomSongs.dll mod plugin installed!");
                MessageBox.Show(msg, "Mod Installation Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ModStatusMessage = msg;
                MessageBox.Show(msg, "Mod Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SavePreferences()
        {
            _gameDetector.WritePreferences(EnableDiscordRpc, EnableVerboseDebug);
        }
    }
}
