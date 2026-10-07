using System;
using System.IO;
using System.Threading.Tasks;
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
        private readonly UpdateService _updateService;
        private GitHubAssetInfo? _modAsset;

        public string CurrentAppVersion => _updateService.CurrentAppVersion;

        private string? _installedModVersion;
        public string InstalledModVersion
        {
            get => _installedModVersion ?? "N/A";
            private set => SetProperty(ref _installedModVersion, value);
        }

        private string _latestReleaseVersion = "—";
        public string LatestReleaseVersion
        {
            get => _latestReleaseVersion;
            private set => SetProperty(ref _latestReleaseVersion, value);
        }

        private bool _isUpdateAvailable;
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetProperty(ref _isUpdateAvailable, value);
        }

        private bool _isCheckingUpdates;
        public bool IsCheckingUpdates
        {
            get => _isCheckingUpdates;
            private set => SetProperty(ref _isCheckingUpdates, value);
        }

        private string _updateStatusMessage = string.Empty;
        public string UpdateStatusMessage
        {
            get => _updateStatusMessage;
            private set => SetProperty(ref _updateStatusMessage, value);
        }

        private bool _canDownloadModAsset;
        public bool CanDownloadModAsset
        {
            get => _canDownloadModAsset;
            private set => SetProperty(ref _canDownloadModAsset, value);
        }

        private GitHubAssetInfo? _usmAsset;
        public bool CanUpdateApp => _usmAsset != null && IsUpdateAvailable;

        private bool _isUpdatingApp;
        public bool IsUpdatingApp
        {
            get => _isUpdatingApp;
            set => SetProperty(ref _isUpdatingApp, value);
        }

        private double _updateProgress;
        public double UpdateProgress
        {
            get => _updateProgress;
            set => SetProperty(ref _updateProgress, value);
        }

        private string _releaseUrl = "https://github.com/TheLucXas/USM/releases";
        public string ReleaseUrl
        {
            get => _releaseUrl;
            private set => SetProperty(ref _releaseUrl, value);
        }

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
            set
            {
                if (SetProperty(ref _isMelonInstalled, value))
                {
                    OnPropertyChanged(nameof(MelonActionButtonText));
                }
            }
        }

        public string MelonActionButtonText => _isMelonInstalled
            ? LocalizationService.T("Settings_MelonReinstallBtn")
            : LocalizationService.T("Settings_MelonInstallBtn");

        private bool _isInstallingMelon;
        public bool IsInstallingMelon
        {
            get => _isInstallingMelon;
            set
            {
                if (SetProperty(ref _isInstallingMelon, value))
                {
                    OnPropertyChanged(nameof(CanInstallMelon));
                }
            }
        }

        public bool CanInstallMelon => !_isInstallingMelon;

        private string _melonInstallStatusMessage = string.Empty;
        public string MelonInstallStatusMessage
        {
            get => _melonInstallStatusMessage;
            set => SetProperty(ref _melonInstallStatusMessage, value);
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
        public ICommand InstallMelonCommand { get; }
        public ICommand UninstallMelonCommand { get; }
        public ICommand IncrementDownloadsCommand { get; }
        public ICommand DecrementDownloadsCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand OpenReleaseCommand { get; }
        public ICommand DownloadModAssetCommand { get; }
        public ICommand UpdateAppCommand { get; }

        public SettingsViewModel(GameDetector gameDetector, Action<string> notifyCallback, UpdateService? updateService = null)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;
            _updateService = updateService ?? new UpdateService();

            BrowseGamePathCommand = new RelayCommand(BrowseGamePath);
            InstallModCommand = new RelayCommand(InstallMod);
            InstallMelonCommand = new AsyncRelayCommand(InstallMelonAsync);
            UninstallMelonCommand = new RelayCommand(UninstallMelon);
            IncrementDownloadsCommand = new RelayCommand(IncrementDownloads);
            DecrementDownloadsCommand = new RelayCommand(DecrementDownloads);

            CheckUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
            OpenReleaseCommand = new RelayCommand(() => UpdateService.OpenUrlInBrowser(ReleaseUrl));
            DownloadModAssetCommand = new AsyncRelayCommand(DownloadModAssetAsync);
            UpdateAppCommand = new AsyncRelayCommand(UpdateAppAsync);

            RefreshStatus();
            _ = CheckForUpdatesAsync();
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
            InstalledModVersion = _gameDetector.GetInstalledModVersion() ?? "N/A";
            IsCustomSongsReady = Directory.Exists(_gameDetector.GetCustomSongsDir() ?? "");

            var (disc, verb) = _gameDetector.ReadPreferences();
            _enableDiscordRpc = disc;
            _enableVerboseDebug = verb;
            _maxDownloadsText = _gameDetector.MaxConcurrentDownloads.ToString();
            CanDownloadModAsset = _modAsset != null && IsGameFound;
            OnPropertyChanged(nameof(CurrentAppVersion));
            OnPropertyChanged(nameof(EnableDiscordRpc));
            OnPropertyChanged(nameof(EnableVerboseDebug));
            OnPropertyChanged(nameof(MaxConcurrentDownloads));
            OnPropertyChanged(nameof(MaxDownloadsText));
        }

        public async Task CheckForUpdatesAsync()
        {
            if (IsCheckingUpdates) return;
            IsCheckingUpdates = true;
            UpdateStatusMessage = LocalizationService.T("Settings_CheckingUpdates");

            try
            {
                var result = await _updateService.CheckForUpdatesAsync();
                IsUpdateAvailable = result.IsUpdateAvailable;
                UpdateStatusMessage = result.Message;

                if (result.Release != null)
                {
                    LatestReleaseVersion = result.Release.TagName;
                    if (!string.IsNullOrEmpty(result.Release.HtmlUrl))
                    {
                        ReleaseUrl = result.Release.HtmlUrl;
                    }
                }
                else
                {
                    LatestReleaseVersion = LocalizationService.T("Settings_NoReleasesFound");
                }

                _modAsset = result.ModAsset;
                _usmAsset = result.UsmAsset;
                CanDownloadModAsset = _modAsset != null && IsGameFound;
                OnPropertyChanged(nameof(CanUpdateApp));
            }
            catch (Exception ex)
            {
                UpdateStatusMessage = LocalizationService.T("Settings_UpdateCheckFailed", ex.Message);
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        public async Task UpdateAppAsync()
        {
            if (_usmAsset == null || string.IsNullOrEmpty(_usmAsset.BrowserDownloadUrl))
            {
                MessageBox.Show(
                    LocalizationService.T("Settings_NoAppAssetFound"),
                    LocalizationService.T("Settings_UpdateAppConfirmTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                LocalizationService.T("Settings_UpdateAppConfirmMsg", LatestReleaseVersion),
                LocalizationService.T("Settings_UpdateAppConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsUpdatingApp = true;
            UpdateProgress = 0;
            UpdateStatusMessage = LocalizationService.T("Settings_UpdatingApp", 0.0);

            try
            {
                var progress = new Progress<double>(p =>
                {
                    UpdateProgress = p;
                    UpdateStatusMessage = LocalizationService.T("Settings_UpdatingApp", p);
                });

                var (success, msg, batchFile) = await _updateService.DownloadAndPrepareAppUpdateAsync(
                    _usmAsset.BrowserDownloadUrl,
                    _usmAsset.Name,
                    progress);

                if (!success)
                {
                    MessageBox.Show(msg, LocalizationService.T("Settings_UpdateAppConfirmTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatusMessage = msg;
                    IsUpdatingApp = false;
                    return;
                }

                if (!string.IsNullOrEmpty(batchFile))
                {
                    UpdateStatusMessage = LocalizationService.T("Settings_ExtractingApp");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = batchFile,
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
                    });

                    Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
            }
            catch (Exception ex)
            {
                UpdateStatusMessage = LocalizationService.T("Settings_UpdateAppFailed", ex.Message);
                MessageBox.Show(UpdateStatusMessage, LocalizationService.T("Settings_UpdateAppConfirmTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                IsUpdatingApp = false;
            }
        }

        public async Task DownloadModAssetAsync()
        {
            if (_modAsset == null || string.IsNullOrEmpty(_modAsset.BrowserDownloadUrl)) return;
            if (string.IsNullOrEmpty(_gameDetector.GamePath)) return;

            string targetDll = Path.Combine(_gameDetector.GamePath, "Mods", "UnpluggedCustomSongs.dll");
            UpdateStatusMessage = LocalizationService.T("Settings_CheckingUpdates");

            var (success, msg) = await _updateService.DownloadAndInstallModAssetAsync(_modAsset.BrowserDownloadUrl, targetDll);
            UpdateStatusMessage = msg;
            _notifyCallback?.Invoke(msg);

            if (success)
            {
                RefreshStatus();
                MessageBox.Show(msg, "USM Mod Update", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(msg, "USM Mod Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

        public async Task InstallMelonAsync()
        {
            if (IsInstallingMelon) return;
            if (string.IsNullOrEmpty(_gameDetector.GamePath) || !Directory.Exists(_gameDetector.GamePath))
            {
                MessageBox.Show(
                    "Please select a valid Unplugged game folder first.",
                    "Game Folder Missing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            IsInstallingMelon = true;
            MelonInstallStatusMessage = LocalizationService.T("Settings_MelonDownloading", 0);

            try
            {
                var progress = new Progress<(double Progress, string Status)>(p =>
                {
                    MelonInstallStatusMessage = p.Status;
                });

                var (success, msg) = await _updateService.DownloadAndInstallMelonLoaderAsync(
                    _gameDetector.GamePath,
                    progress);

                MelonInstallStatusMessage = msg;
                _notifyCallback?.Invoke(msg);

                RefreshStatus();

                if (success)
                {
                    MessageBox.Show(
                        msg,
                        "MelonLoader",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        msg,
                        "MelonLoader Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MelonInstallStatusMessage = LocalizationService.T("Settings_MelonFailed", ex.Message);
                MessageBox.Show(
                    MelonInstallStatusMessage,
                    "MelonLoader Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsInstallingMelon = false;
            }
        }

        public void UninstallMelon()
        {
            if (string.IsNullOrEmpty(_gameDetector.GamePath)) return;

            var (success, msg) = _updateService.UninstallMelonLoader(_gameDetector.GamePath);
            _notifyCallback?.Invoke(msg);
            RefreshStatus();
            MessageBox.Show(msg, "MelonLoader", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SavePreferences()
        {
            _gameDetector.WritePreferences(EnableDiscordRpc, EnableVerboseDebug);
        }
    }
}
