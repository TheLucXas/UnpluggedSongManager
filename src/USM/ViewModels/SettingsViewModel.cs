using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using USM.Core.Services;
using USM.Core.Services.Localization;

namespace USM.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;

        public System.Collections.Generic.IReadOnlyList<LanguageItem> Languages => LocalizationService.SupportedLanguages;

        public string SelectedLanguage
        {
            get => LocalizationService.Instance.CurrentLanguage;
            set
            {
                if (!string.IsNullOrEmpty(value) && LocalizationService.Instance.CurrentLanguage != value)
                {
                    LocalizationService.Instance.CurrentLanguage = value;
                    _gameDetector.Language = value;
                    OnPropertyChanged();
                    RefreshStatus();
                }
            }
        }

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

        private string _maxDownloadsText = "5";
        public string MaxDownloadsText
        {
            get => _maxDownloadsText;
            set
            {
                if (SetProperty(ref _maxDownloadsText, value))
                {
                    if (int.TryParse(value, out int parsed))
                    {
                        int clamped = Math.Clamp(parsed, 1, 99);
                        if (_gameDetector.MaxConcurrentDownloads != clamped)
                        {
                            _gameDetector.MaxConcurrentDownloads = clamped;
                            OnPropertyChanged(nameof(MaxConcurrentDownloads));
                            _notifyCallback?.Invoke($"Maximum concurrent downloads set to {clamped}.");
                        }
                    }
                }
            }
        }

        public int MaxConcurrentDownloads
        {
            get => _gameDetector.MaxConcurrentDownloads;
            set
            {
                int clamped = Math.Clamp(value, 1, 99);
                if (_gameDetector.MaxConcurrentDownloads != clamped)
                {
                    _gameDetector.MaxConcurrentDownloads = clamped;
                    _maxDownloadsText = clamped.ToString();
                    OnPropertyChanged(nameof(MaxDownloadsText));
                    OnPropertyChanged(nameof(MaxConcurrentDownloads));
                    _notifyCallback?.Invoke($"Maximum concurrent downloads set to {clamped}.");
                }
            }
        }

        public ICommand BrowseGamePathCommand { get; }
        public ICommand InstallModCommand { get; }
        public ICommand IncrementDownloadsCommand { get; }
        public ICommand DecrementDownloadsCommand { get; }

        public SettingsViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            BrowseGamePathCommand = new RelayCommand(BrowseGamePath);
            InstallModCommand = new RelayCommand(InstallMod);
            IncrementDownloadsCommand = new RelayCommand(IncrementDownloads);
            DecrementDownloadsCommand = new RelayCommand(DecrementDownloads);

            RefreshStatus();
        }

        private void IncrementDownloads()
        {
            int current = int.TryParse(MaxDownloadsText, out int v) ? v : 5;
            if (current < 99)
            {
                current++;
                MaxDownloadsText = current.ToString();
            }
        }

        private void DecrementDownloads()
        {
            int current = int.TryParse(MaxDownloadsText, out int v) ? v : 5;
            if (current > 1)
            {
                current--;
                MaxDownloadsText = current.ToString();
            }
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
            _maxDownloadsText = _gameDetector.MaxConcurrentDownloads.ToString();
            OnPropertyChanged(nameof(EnableDiscordRpc));
            OnPropertyChanged(nameof(EnableVerboseDebug));
            OnPropertyChanged(nameof(MaxConcurrentDownloads));
            OnPropertyChanged(nameof(MaxDownloadsText));
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
