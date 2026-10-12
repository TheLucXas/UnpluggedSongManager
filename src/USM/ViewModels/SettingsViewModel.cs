using System;
using System.ComponentModel;
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
        private readonly QuestViewModel? _questViewModel;
        private GitHubAssetInfo? _modAsset;
        private GitHubAssetInfo? _questModAsset;
        private bool _packagedPcUpdateAvailable;
        private bool _isUpdatingMod;
        public bool IsUpdatingMod
        {
            get => _isUpdatingMod;
            private set { if (SetProperty(ref _isUpdatingMod, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        public bool HasPcModUpdate => IsModInstalled && (_packagedPcUpdateAvailable ||
            HasNewReleaseBuild(_modAsset, InstalledModVersion));
        public string PcModActionText => LocalizationService.T(!IsModInstalled
            ? "Settings_ModInstallBtn" : HasPcModUpdate ? "Settings_ModUpdateBtn" : "Settings_ModReinstallBtn");
        public string GameExecutableStatus => LocalizationService.T(IsGameFound ? "Settings_StatusReady" : "Settings_StatusMissing");
        public string MelonStatus => LocalizationService.T(IsMelonInstalled ? "Settings_StatusReady" : "Settings_StatusMissing");
        public string PcModStatus => LocalizationService.T(IsModInstalled ? "Settings_StatusReady" : "Settings_StatusMissing");
        public string SongsFolderStatus => LocalizationService.T(IsCustomSongsReady ? "Settings_StatusReady" : "Settings_StatusMissing");
        public bool HasQuestModUpdate => _questViewModel?.IsModdedInstalled == true &&
            (_questViewModel.IsModUpdateAvailable || HasNewReleaseBuild(_questModAsset, _questViewModel.InstalledModInfo?.Version));

        private static bool HasNewReleaseBuild(GitHubAssetInfo? asset, string? installedVersion)
        {
            return !string.IsNullOrWhiteSpace(installedVersion) && !string.IsNullOrWhiteSpace(asset?.ModVersion)
                && UpdateService.CompareVersions(asset.ModVersion, installedVersion) > 0;
        }

        private void RefreshUpdateNotices()
        {
            OnPropertyChanged(nameof(HasPcModUpdate));
            OnPropertyChanged(nameof(HasQuestModUpdate));
            OnPropertyChanged(nameof(InstalledPcModVersionText));
            OnPropertyChanged(nameof(InstalledQuestModVersion));
            OnPropertyChanged(nameof(PcModActionText));
            OnPropertyChanged(nameof(GameExecutableStatus));
            OnPropertyChanged(nameof(MelonStatus));
            OnPropertyChanged(nameof(PcModStatus));
            OnPropertyChanged(nameof(SongsFolderStatus));
            OnPropertyChanged(nameof(MelonActionButtonText));
        }

        public string CurrentAppVersion => _updateService.CurrentAppVersion;

        private string? _installedModVersion;
        public string InstalledModVersion
        {
            get => _installedModVersion ?? "N/A";
            private set => SetProperty(ref _installedModVersion, value);
        }

        public bool IsQuestModInstalled => _questViewModel?.IsModdedInstalled == true;

        public string InstalledPcModVersionText => !IsModInstalled ? "N/A"
            : $"v{InstalledModVersion} · {LocalizationService.T(HasPcModUpdate ? "Settings_ModOutdated" : "Settings_ModCurrent")}";

        public string InstalledQuestModVersion
        {
            get
            {
                var mod = _questViewModel?.InstalledModInfo;
                if (!IsQuestModInstalled || mod == null) return "N/A";
                string status = LocalizationService.T(HasQuestModUpdate ? "Settings_ModOutdated" : "Settings_ModCurrent");
                return $"v{mod.Version} · {status}";
            }
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
        private string? _lastUpdateStatusKey;
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
        public ICommand UninstallModCommand { get; }
        public ICommand InstallMelonCommand { get; }
        public ICommand UninstallMelonCommand { get; }
        public ICommand IncrementDownloadsCommand { get; }
        public ICommand DecrementDownloadsCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand OpenReleaseCommand { get; }
        public ICommand DownloadModAssetCommand { get; }
        public ICommand UpdateAppCommand { get; }

        public SettingsViewModel(GameDetector gameDetector, Action<string> notifyCallback, UpdateService? updateService = null, QuestViewModel? questViewModel = null)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;
            _updateService = updateService ?? new UpdateService();
            _questViewModel = questViewModel;
            if (_questViewModel != null)
                _questViewModel.PropertyChanged += OnQuestViewModelPropertyChanged;

            BrowseGamePathCommand = new RelayCommand(BrowseGamePath);
            InstallModCommand = new RelayCommand(_ => InstallMod(), _ => !IsUpdatingMod);
            UninstallModCommand = new RelayCommand(_ => UninstallMod(), _ => !IsUpdatingMod && IsModInstalled);
            InstallMelonCommand = new AsyncRelayCommand(InstallMelonAsync);
            UninstallMelonCommand = new RelayCommand(UninstallMelon);
            IncrementDownloadsCommand = new RelayCommand(IncrementDownloads);
            DecrementDownloadsCommand = new RelayCommand(DecrementDownloads);

            CheckUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
            OpenReleaseCommand = new RelayCommand(() => UpdateService.OpenUrlInBrowser(ReleaseUrl));
            DownloadModAssetCommand = new AsyncRelayCommand(_ => DownloadModAssetAsync(), _ => !IsUpdatingMod);
            UpdateAppCommand = new AsyncRelayCommand(UpdateAppAsync);

            RefreshStatus();
            LocalizationService.Instance.LanguageChanged += _ =>
            {
                RefreshUpdateNotices();
                if (_lastUpdateStatusKey != null)
                    UpdateStatusMessage = LocalizationService.T(_lastUpdateStatusKey);
                if (LatestReleaseVersion == "—" || _lastUpdateStatusKey == "Settings_NoReleasesFound")
                    LatestReleaseVersion = LocalizationService.T("Settings_NoReleasesFound");
            };
            _ = CheckForUpdatesAsync();
        }

        private void OnQuestViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(QuestViewModel.IsModdedInstalled) ||
                e.PropertyName == nameof(QuestViewModel.InstalledModInfo))
            {
                OnPropertyChanged(nameof(IsQuestModInstalled));
                OnPropertyChanged(nameof(InstalledQuestModVersion));
                RefreshUpdateNotices();
            }
            if (e.PropertyName == nameof(QuestViewModel.IsModUpdateAvailable)) RefreshUpdateNotices();
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
            string? packagedPc = PackagedModAssemblyProvider.ExtractPcModAssembly();
            string? packagedVersion = ModAssemblyMetadata.GetVersion(packagedPc ?? "");
            _packagedPcUpdateAvailable = IsModInstalled &&
                ModAssemblyMetadata.IsNewPackagedVersion(packagedVersion, InstalledModVersion);
            RefreshUpdateNotices();
            IsCustomSongsReady = Directory.Exists(_gameDetector.GetCustomSongsDir() ?? "");

            var (disc, verb) = _gameDetector.ReadPreferences();
            _enableDiscordRpc = disc;
            _enableVerboseDebug = verb;
            _maxDownloadsText = _gameDetector.MaxConcurrentDownloads.ToString();
            CanDownloadModAsset = _modAsset != null && IsGameFound;
            OnPropertyChanged(nameof(CurrentAppVersion));
            OnPropertyChanged(nameof(InstalledQuestModVersion));
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
                _lastUpdateStatusKey = result.Release == null ? "Settings_NoReleasesFound"
                    : result.IsUpdateAvailable ? null : "Settings_UpToDate";

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
                _questModAsset = result.QuestModAsset;
                _questViewModel?.SetAvailableModRelease(_questModAsset);
                RefreshUpdateNotices();
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
                try { PcSteamVrBindings.Prepare(_gameDetector.GamePath!); }
                catch (Exception ex)
                {
                    UpdateStatusMessage = ex.Message;
                    _notifyCallback?.Invoke(ex.Message);
                    MessageBox.Show(ex.Message, LocalizationService.T("Settings_ModInstallFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                RefreshStatus();
                MessageBox.Show(msg, LocalizationService.T("Settings_ModUpdateBtn"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(msg, LocalizationService.T("Settings_ModUpdateBtn"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task<bool> UpdatePcModAsync()
        {
            if (IsUpdatingMod || !IsGameFound || !IsModInstalled) return false;
            IsUpdatingMod = true;
            string? downloadedPath = null;
            string? pendingPath = null;
            try
            {
                string? packaged = PackagedModAssemblyProvider.ExtractPcModAssembly();
                string? packagedVersion = ModAssemblyMetadata.GetVersion(packaged ?? "");
                string? source = packaged;
                if (_modAsset?.ModVersion != null
                    && UpdateService.CompareVersions(_modAsset.ModVersion, packagedVersion ?? "0") > 0)
                {
                    downloadedPath = Path.Combine(Path.GetTempPath(), $"USM_PcUpdate_{Guid.NewGuid():N}.dll");
                    var result = await _updateService.DownloadAndInstallModAssetAsync(_modAsset.BrowserDownloadUrl,
                        downloadedPath);
                    if (!result.Success) throw new IOException(result.Message);
                    if (ModAssemblyMetadata.GetVersion(downloadedPath) != _modAsset.ModVersion
                        || (_modAsset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(_modAsset.Digest[7..], ModAssemblyMetadata.GetHash(downloadedPath), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException(LocalizationService.T("Nav_ModUpdateFailed"));
                    source = downloadedPath;
                }
                if (source == null || !File.Exists(source))
                    throw new FileNotFoundException(LocalizationService.T("Nav_ModUpdateFailed"));
                string target = Path.Combine(GamePath, "Mods", "UnpluggedCustomSongs.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                pendingPath = target + ".pending";
                File.Copy(source, pendingPath, true);
                string? expectedHash = ModAssemblyMetadata.GetHash(source);
                if (expectedHash == null || !string.Equals(expectedHash, ModAssemblyMetadata.GetHash(pendingPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(LocalizationService.T("Nav_ModUpdateFailed"));

                // Stop only this installation of the game before replacing its
                // loaded DLL. Other programs and other game copies are untouched.
                string gameExe = Path.GetFullPath(Path.Combine(GamePath, "Unplugged.exe"));
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("Unplugged"))
                {
                    using (process)
                    {
                        if (!string.Equals(process.MainModule?.FileName, gameExe, StringComparison.OrdinalIgnoreCase)) continue;
                        process.CloseMainWindow();
                        if (!await Task.Run(() => process.WaitForExit(4000)))
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                    }
                }
                PcSteamVrBindings.Prepare(GamePath);
                File.Move(pendingPath, target, true);
                bool success = string.Equals(expectedHash, ModAssemblyMetadata.GetHash(target), StringComparison.OrdinalIgnoreCase);
                string message = LocalizationService.T(success ? "Settings_ModUpdatedSuccess" : "Nav_ModUpdateFailed");
                UpdateStatusMessage = message;
                RefreshStatus();
                _notifyCallback(message);
                return success;
            }
            catch (Exception ex)
            {
                UpdateStatusMessage = ex.Message;
                _notifyCallback(ex.Message);
                return false;
            }
            finally
            {
                foreach (string? temp in new[] { downloadedPath, pendingPath })
                    if (temp != null) try { File.Delete(temp); } catch { }
                IsUpdatingMod = false;
            }
        }

        private void BrowseGamePath()
        {
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationService.T("Settings_GameFolderPicker"),
                Multiselect = false
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            {
                if (_gameDetector.SetGamePath(dlg.FolderName))
                {
                    RefreshStatus();
                    _notifyCallback?.Invoke(LocalizationService.T("Settings_GameFolderUpdated"));
                }
                else
                {
                    MessageBox.Show(LocalizationService.T("Settings_GameFolderInvalid"), LocalizationService.T("Settings_GameFolderTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void InstallMod()
        {
            if (_gameDetector.InstallOrUpdateModPlugin(out string msg))
            {
                ModStatusMessage = msg;
                RefreshStatus();
                _notifyCallback?.Invoke(LocalizationService.T("Settings_ModInstallDone"));
                MessageBox.Show(LocalizationService.T("Settings_ModInstallDone"), LocalizationService.T("Settings_ModStatusTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ModStatusMessage = msg;
                MessageBox.Show(msg, LocalizationService.T("Settings_ModInstallFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UninstallMod()
        {
            if (!IsModInstalled || string.IsNullOrEmpty(GamePath)) return;
            if (MessageBox.Show(LocalizationService.T("Settings_ModUninstallConfirm"),
                LocalizationService.T("Settings_ModUninstallBtn"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            string expectedExe = Path.GetFullPath(Path.Combine(GamePath, "Unplugged.exe"));
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Unplugged"))
            {
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, expectedExe, StringComparison.OrdinalIgnoreCase))
                        {
                            MessageBox.Show(LocalizationService.T("Settings_ModCloseGame"),
                                LocalizationService.T("Settings_ModUninstallBtn"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                    catch { }
                }
            }
            bool success = _gameDetector.UninstallModPlugin(out string error);
            RefreshStatus();
            string message = success ? LocalizationService.T("Settings_ModUninstallDone") : error;
            _notifyCallback(message);
            MessageBox.Show(message, LocalizationService.T("Settings_ModUninstallBtn"), MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        public async Task InstallMelonAsync()
        {
            if (IsInstallingMelon) return;
            if (string.IsNullOrEmpty(_gameDetector.GamePath) || !Directory.Exists(_gameDetector.GamePath))
            {
                MessageBox.Show(
                    LocalizationService.T("Settings_GameFolderMissing"),
                    LocalizationService.T("Settings_GameFolderTitle"),
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
