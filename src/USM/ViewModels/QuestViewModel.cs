using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using USM.Core.Services;
using USM.Core.Services.Localization;
using USM.Core.Services.Quest;

namespace USM.ViewModels
{
    public class QuestViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;
        private readonly QuestService _questService;

        private ObservableCollection<QuestDevice> _devices = new();
        private QuestDevice? _selectedDevice;
        private string _wirelessIp = "192.168.1.";
        private int _wirelessPort = 5555;
        private bool _isConnectingWireless;
        private bool _isBusy;
        private int _progressPercent;
        private string _progressStatus = "Ready";
        private string _packageStatusText = LocalizationService.T("Quest_StatusNoDevice");
        private bool _isOriginalInstalled;
        private bool _isModdedInstalled;
        private bool _useCustomApk;
        private string _customApkPath = string.Empty;
        private string _logText = string.Empty;
        private readonly StringBuilder _logBuilder = new();

        public ObservableCollection<QuestDevice> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }

        public QuestDevice? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(IsDeviceConnected));
                    _ = CheckInstalledPackagesAsync();
                }
            }
        }

        public bool IsDeviceConnected => SelectedDevice != null;

        public string WirelessIp
        {
            get => _wirelessIp;
            set => SetProperty(ref _wirelessIp, value);
        }

        public int WirelessPort
        {
            get => _wirelessPort;
            set => SetProperty(ref _wirelessPort, value);
        }

        public bool IsConnectingWireless
        {
            get => _isConnectingWireless;
            set => SetProperty(ref _isConnectingWireless, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public int ProgressPercent
        {
            get => _progressPercent;
            set => SetProperty(ref _progressPercent, value);
        }

        public string ProgressStatus
        {
            get => _progressStatus;
            set => SetProperty(ref _progressStatus, value);
        }

        public string PackageStatusText
        {
            get => _packageStatusText;
            set => SetProperty(ref _packageStatusText, value);
        }

        public bool IsOriginalInstalled
        {
            get => _isOriginalInstalled;
            set => SetProperty(ref _isOriginalInstalled, value);
        }

        public bool IsModdedInstalled
        {
            get => _isModdedInstalled;
            set
            {
                if (SetProperty(ref _isModdedInstalled, value))
                {
                    OnPropertyChanged(nameof(CanUninstallModded));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private bool _skipIntroAndUnlockCampaign = true;
        public bool SkipIntroAndUnlockCampaign
        {
            get => _skipIntroAndUnlockCampaign;
            set => SetProperty(ref _skipIntroAndUnlockCampaign, value);
        }

        private string _patchButtonText = LocalizationService.T("Quest_PatchButton_Install");
        public string PatchButtonText
        {
            get => _patchButtonText;
            set => SetProperty(ref _patchButtonText, value);
        }

        private string _patchButtonDescription = LocalizationService.T("Quest_PatchDesc_Install");
        public string PatchButtonDescription
        {
            get => _patchButtonDescription;
            set => SetProperty(ref _patchButtonDescription, value);
        }

        private string? _originalVersion;
        public string? OriginalVersion
        {
            get => _originalVersion;
            set => SetProperty(ref _originalVersion, value);
        }

        private string? _moddedVersion;
        public string? ModdedVersion
        {
            get => _moddedVersion;
            set => SetProperty(ref _moddedVersion, value);
        }

        public bool CanUninstallModded => IsDeviceConnected && IsModdedInstalled;

        public bool UseCustomApk
        {
            get => _useCustomApk;
            set => SetProperty(ref _useCustomApk, value);
        }

        public string CustomApkPath
        {
            get => _customApkPath;
            set => SetProperty(ref _customApkPath, value);
        }

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        public int LocalSongCount
        {
            get
            {
                string? songsDir = _gameDetector.GetCustomSongsDir();
                if (!string.IsNullOrEmpty(songsDir) && Directory.Exists(songsDir))
                {
                    return Directory.GetFiles(songsDir, "*.usm").Length;
                }
                return 0;
            }
        }

        public bool AreQuestToolsReady => _questService.AreToolsReady();
        public bool NeedsQuestTools => !AreQuestToolsReady;
        public string QuestToolsStatusText => AreQuestToolsReady
            ? LocalizationService.T("Quest_ToolsStatusCached")
            : LocalizationService.T("Quest_ToolsStatusMissing");

        public ICommand RefreshDevicesCommand { get; }

        public ICommand ConnectWirelessCommand { get; }
        public ICommand BrowseApkCommand { get; }
        public ICommand StartPatchCommand { get; }
        public ICommand UninstallModdedCommand { get; }
        public ICommand SyncSongsCommand { get; }
        public ICommand ImportPcSaveCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand DownloadQuestToolsCommand { get; }

        public QuestViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;
            _questService = new QuestService();

            RefreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(), _ => !IsBusy);
            ConnectWirelessCommand = new AsyncRelayCommand(_ => ConnectWirelessAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(WirelessIp));
            BrowseApkCommand = new RelayCommand(BrowseApk, _ => !IsBusy);
            StartPatchCommand = new AsyncRelayCommand(_ => StartPatchPipelineAsync(), _ => !IsBusy && (IsDeviceConnected || (!string.IsNullOrEmpty(CustomApkPath) && File.Exists(CustomApkPath))));
            UninstallModdedCommand = new AsyncRelayCommand(_ => UninstallModdedAsync(), _ => !IsBusy && IsDeviceConnected && IsModdedInstalled);
            SyncSongsCommand = new AsyncRelayCommand(_ => SyncSongsAsync(), _ => !IsBusy && IsDeviceConnected && LocalSongCount > 0);
            ImportPcSaveCommand = new AsyncRelayCommand(_ => ImportPcSaveAsync(), _ => !IsBusy && IsDeviceConnected && IsModdedInstalled);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            DownloadQuestToolsCommand = new AsyncRelayCommand(_ => DownloadQuestToolsAsync(), _ => !IsBusy);

            LocalizationService.Instance.LanguageChanged += lang =>
            {
                OnPropertyChanged(nameof(QuestToolsStatusText));
                _ = CheckInstalledPackagesAsync();
            };
            AppendLog(LocalizationService.T("Log_QuestInit"));
            _ = RefreshDevicesAsync();
        }

        private async Task DownloadQuestToolsAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                var progress = new Progress<(double Progress, string Status)>(p =>
                {
                    ProgressPercent = (int)p.Progress;
                    ProgressStatus = p.Status;
                });
                var (success, msg) = await _questService.EnsureToolsReadyAsync(progress, AppendLog);
                OnPropertyChanged(nameof(AreQuestToolsReady));
                OnPropertyChanged(nameof(NeedsQuestTools));
                OnPropertyChanged(nameof(QuestToolsStatusText));
                _notifyCallback?.Invoke(msg);
            }
            finally
            {
                IsBusy = false;
                ProgressPercent = 0;
                ProgressStatus = "Ready";
            }
        }

        public void AppendLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{time}] {message}\n";
            _logBuilder.Append(line);

            // Cap logs at 100,000 characters
            if (_logBuilder.Length > 100000)
            {
                _logBuilder.Remove(0, 20000);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                LogText = _logBuilder.ToString();
            });
        }

        private void ClearLogs()
        {
            _logBuilder.Clear();
            LogText = string.Empty;
        }

        private async Task RefreshDevicesAsync()
        {
            try
            {
                var list = await _questService.GetDevicesAsync();
                Devices.Clear();
                foreach (var d in list)
                {
                    Devices.Add(d);
                }

                if (Devices.Count > 0)
                {
                    SelectedDevice = Devices.First();
                    AppendLog(LocalizationService.T("Log_FoundDevices", Devices.Count, SelectedDevice.DisplayName));
                }
                else
                {
                    SelectedDevice = null;
                    PackageStatusText = LocalizationService.T("Quest_StatusNoDevice");
                    AppendLog(LocalizationService.T("Log_NoDevicesFound"));
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error discovering devices: {ex.Message}");
            }
        }

        private async Task ConnectWirelessAsync()
        {
            if (string.IsNullOrWhiteSpace(WirelessIp)) return;
            IsConnectingWireless = true;
            AppendLog(LocalizationService.T("Log_ConnectingWireless", WirelessIp, WirelessPort));

            try
            {
                var (ok, msg) = await _questService.ConnectWirelessAsync(WirelessIp, WirelessPort);
                if (ok)
                {
                    AppendLog(LocalizationService.T("Log_WirelessConnected", msg));
                    _notifyCallback($"Quest Wi-Fi connected ({WirelessIp})");
                    await RefreshDevicesAsync();
                }
                else
                {
                    AppendLog(LocalizationService.T("Log_WirelessFailed", msg));
                    _notifyCallback($"Wireless connection failed: {msg}");
                }
            }
            finally
            {
                IsConnectingWireless = false;
            }
        }

        private async Task CheckInstalledPackagesAsync()
        {
            if (SelectedDevice == null)
            {
                PackageStatusText = LocalizationService.T("Quest_StatusNoDevice");
                IsOriginalInstalled = false;
                IsModdedInstalled = false;
                OriginalVersion = null;
                ModdedVersion = null;
                PatchButtonText = LocalizationService.T("Quest_PatchButton_Install");
                PatchButtonDescription = LocalizationService.T("Quest_PatchDesc_Install");
                return;
            }

            if (SelectedDevice.Status.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                PackageStatusText = LocalizationService.T("Quest_StatusUnauthorized");
                AppendLog(LocalizationService.T("Quest_StatusUnauthorized"));
                IsOriginalInstalled = false;
                IsModdedInstalled = false;
                return;
            }

            PackageStatusText = LocalizationService.T("Quest_StatusChecking");
            try
            {
                IsOriginalInstalled = await _questService.IsPackageInstalledAsync(SelectedDevice.Serial, QuestService.OriginalPackage);
                IsModdedInstalled = await _questService.IsPackageInstalledAsync(SelectedDevice.Serial, QuestService.ModdedPackage);

                if (IsOriginalInstalled)
                {
                    OriginalVersion = await _questService.GetPackageVersionAsync(SelectedDevice.Serial, QuestService.OriginalPackage);
                }
                else
                {
                    OriginalVersion = null;
                }

                if (IsModdedInstalled)
                {
                    ModdedVersion = await _questService.GetPackageVersionAsync(SelectedDevice.Serial, QuestService.ModdedPackage);
                }
                else
                {
                    ModdedVersion = null;
                }

                var sb = new StringBuilder();
                if (IsOriginalInstalled)
                {
                    sb.Append(LocalizationService.T("Quest_StockFound") + (OriginalVersion != null ? $" (v{OriginalVersion}). " : ". "));
                }
                else
                {
                    sb.Append(LocalizationService.T("Quest_StockNotFound") + ". ");
                }

                if (IsModdedInstalled)
                {
                    sb.Append(LocalizationService.T("Quest_ModdedFound") + (ModdedVersion != null ? $" (v{ModdedVersion})." : "."));
                }
                else
                {
                    sb.Append(LocalizationService.T("Quest_ModdedNotFound") + ".");
                }

                PackageStatusText = sb.ToString();
                AppendLog($"Quest Packages Status: {PackageStatusText}");

                // Dynamic button text & description based on installed state & versions
                if (!IsModdedInstalled)
                {
                    PatchButtonText = LocalizationService.T("Quest_PatchButton_Install");
                    PatchButtonDescription = LocalizationService.T("Quest_PatchDesc_Install");
                }
                else
                {
                    if (!string.IsNullOrEmpty(OriginalVersion) && !string.IsNullOrEmpty(ModdedVersion) && !OriginalVersion.Equals(ModdedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        PatchButtonText = LocalizationService.T("Quest_PatchButton_Update");
                        PatchButtonDescription = LocalizationService.T("Quest_PatchDesc_Update", OriginalVersion, ModdedVersion);
                    }
                    else
                    {
                        PatchButtonText = LocalizationService.T("Quest_PatchButton_Reinstall");
                        PatchButtonDescription = LocalizationService.T("Quest_PatchDesc_Reinstall", ModdedVersion != null ? $" (v{ModdedVersion})" : "");
                    }
                }

                OnPropertyChanged(nameof(CanUninstallModded));
            }
            catch (Exception ex)
            {
                PackageStatusText = $"Error checking packages: {ex.Message}";
            }
        }

        private void BrowseApk(object? _)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Unplugged APK",
                Filter = "Android Package (*.apk)|*.apk|All Files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                CustomApkPath = dlg.FileName;
                UseCustomApk = true;
                AppendLog($"Selected local APK: {CustomApkPath}");
            }
        }

        private async Task StartPatchPipelineAsync()
        {
            if (IsBusy) return;

            string? serial = SelectedDevice?.Serial;
            string? customApk = (UseCustomApk && File.Exists(CustomApkPath)) ? CustomApkPath : null;

            if (string.IsNullOrEmpty(serial) && string.IsNullOrEmpty(customApk))
            {
                MessageBox.Show(LocalizationService.T("Quest_Dialog_SelectHeadsetWarning"), LocalizationService.T("Quest_StandaloneModderTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string actionMsg = IsModdedInstalled ? LocalizationService.T("Quest_PatchButton_Reinstall") : LocalizationService.T("Quest_PatchButton_Install");
            string introMsg = SkipIntroAndUnlockCampaign ? (" (" + LocalizationService.T("Quest_SkipIntroTitle") + ")") : "";
            var confirm = MessageBox.Show(
                LocalizationService.T("Quest_Dialog_ConfirmPatchMsg", actionMsg, introMsg),
                LocalizationService.T("Quest_Dialog_ConfirmPatchTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = "Starting...";
            _notifyCallback("Quest modding pipeline in progress...");

            var progress = new Progress<(int Percent, string Status)>(report =>
            {
                ProgressPercent = report.Percent;
                ProgressStatus = report.Status;
            });

            try
            {
                using var cts = new CancellationTokenSource();
                bool success = await _questService.PatchAndDeployAsync(
                    serial,
                    customApk,
                    SkipIntroAndUnlockCampaign,
                    progress,
                    msg => AppendLog(msg),
                    cts.Token
                );

                if (success)
                {
                    ProgressPercent = 100;
                    ProgressStatus = "Done!";
                    _notifyCallback("Unplugged Modded successfully deployed to Quest!");
                    string introNote = SkipIntroAndUnlockCampaign ? LocalizationService.T("Quest_Dialog_PatchCompleteIntroNote") : "";
                    MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_PatchCompleteMsg", introNote),
                        LocalizationService.T("Quest_Dialog_PatchCompleteTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    await CheckInstalledPackagesAsync();
                }
                else
                {
                    ProgressStatus = "Failed.";
                    _notifyCallback("Quest modding pipeline failed. Check logs.");
                    MessageBox.Show(LocalizationService.T("Quest_Dialog_PatchErrorMsg"), LocalizationService.T("Quest_Dialog_PatchErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"FATAL ERROR: {ex.Message}");
                ProgressStatus = "Fatal Error";
                _notifyCallback($"Error: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task UninstallModdedAsync()
        {
            if (SelectedDevice == null || !IsModdedInstalled || IsBusy) return;

            var confirm = MessageBox.Show(
                LocalizationService.T("Quest_Dialog_ConfirmUninstallMsg"),
                LocalizationService.T("Quest_Dialog_ConfirmUninstallTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = "Uninstalling...";
            _notifyCallback("Uninstalling Unplugged Modded...");

            try
            {
                var (ok, msg) = await _questService.UninstallModdedPackageAsync(SelectedDevice.Serial, msg => AppendLog(msg));
                if (ok)
                {
                    ProgressPercent = 100;
                    ProgressStatus = "Uninstall Complete";
                    _notifyCallback("Unplugged Modded uninstalled.");
                    MessageBox.Show(LocalizationService.T("Quest_Dialog_UninstallCompleteMsg"), LocalizationService.T("Quest_Dialog_UninstallCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    await CheckInstalledPackagesAsync();
                }
                else
                {
                    ProgressStatus = "Uninstall Failed";
                    _notifyCallback($"Error: {msg}");
                    MessageBox.Show(LocalizationService.T("Quest_Dialog_UninstallErrorMsg", msg), LocalizationService.T("Quest_Dialog_UninstallErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Uninstall Error: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ImportPcSaveAsync()
        {
            if (SelectedDevice == null || IsBusy) return;

            string? defaultDirectory = FindPcSaveDirectory();
            var dialog = new OpenFileDialog
            {
                Title = LocalizationService.T("Quest_ImportPcSaveTitle"),
                Filter = "Unplugged PC save (SavedData.json)|SavedData.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                FileName = "SavedData.json"
            };
            if (!string.IsNullOrEmpty(defaultDirectory)) dialog.InitialDirectory = defaultDirectory;
            if (dialog.ShowDialog() != true) return;

            var confirm = MessageBox.Show(
                LocalizationService.T("Quest_Dialog_ImportPcSaveConfirmMsg", dialog.FileName),
                LocalizationService.T("Quest_Dialog_ImportPcSaveTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            string serial = SelectedDevice.Serial;
            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = LocalizationService.T("Quest_ImportPcSaveTitle");
            _notifyCallback(LocalizationService.T("Quest_ImportPcSaveTitle"));

            try
            {
                AppendLog($"Importing PC save from {dialog.FileName}...");
                var (success, message) = await _questService.ImportPcSaveToQuestAsync(
                    serial,
                    dialog.FileName,
                    AppendLog);
                if (success)
                {
                    ProgressPercent = 100;
                    MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_ImportPcSaveSuccessMsg", message),
                        LocalizationService.T("Quest_Dialog_ImportPcSaveSuccessTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    _notifyCallback(LocalizationService.T("Quest_Dialog_ImportPcSaveSuccessTitle"));
                }
                else
                {
                    AppendLog(message);
                    MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_ImportPcSaveErrorMsg", message),
                        LocalizationService.T("Quest_Dialog_ImportPcSaveErrorTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"PC save import failed: {ex.Message}");
                MessageBox.Show(
                    LocalizationService.T("Quest_Dialog_ImportPcSaveErrorMsg", ex.Message),
                    LocalizationService.T("Quest_Dialog_ImportPcSaveErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                await CheckInstalledPackagesAsync();
            }
        }

        private static string? FindPcSaveDirectory()
        {
            string localLow = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "..", "LocalLow", "Anotherway", "Unplugged", "CloudSave"));
            if (!Directory.Exists(localLow)) return null;

            try
            {
                return Directory.GetFiles(localLow, "SavedData.json", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path));
            }
            catch
            {
                return localLow;
            }
        }

        private async Task SyncSongsAsync()
        {
            if (SelectedDevice == null)
            {
                MessageBox.Show(LocalizationService.T("Quest_Dialog_SelectHeadsetWarning"), LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? songsDir = _gameDetector.GetCustomSongsDir();
            if (string.IsNullOrEmpty(songsDir) || !Directory.Exists(songsDir))
            {
                MessageBox.Show("No custom songs directory found in USM.", LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var allSongDirs = Directory.GetFiles(songsDir, "*.usm");
            if (allSongDirs.Length == 0)
            {
                MessageBox.Show("No custom songs found to transfer.", LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            ProgressStatus = "Checking Quest songs...";
            _notifyCallback("Checking installed songs on Quest...");

            List<string> songsToSync;
            try
            {
                var existingOnQuest = await _questService.GetQuestInstalledSongsAsync(SelectedDevice.Serial);
                songsToSync = allSongDirs
                    .Where(dir => !existingOnQuest.Contains(Path.GetFileName(dir)))
                    .ToList();

                if (songsToSync.Count == 0)
                {
                    var forceConfirm = MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_AllSongsSyncedMsg", allSongDirs.Length),
                        LocalizationService.T("Quest_Dialog_AllSongsSyncedTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    );

                    if (forceConfirm == MessageBoxResult.Yes)
                    {
                        songsToSync = allSongDirs.ToList();
                    }
                    else
                    {
                        AppendLog(LocalizationService.T("Log_SyncAllUpToDate", allSongDirs.Length, QuestService.ModdedSongsPath));
                        IsBusy = false;
                        ProgressStatus = "Ready";
                        return;
                    }
                }
                else if (existingOnQuest.Count > 0)
                {
                    var confirm = MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_ConfirmIncrementalSyncMsg", songsToSync.Count, existingOnQuest.Count),
                        LocalizationService.T("Quest_Dialog_ConfirmSyncTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );
                    if (confirm != MessageBoxResult.Yes)
                    {
                        IsBusy = false;
                        ProgressStatus = "Ready";
                        return;
                    }
                    AppendLog(LocalizationService.T("Log_SyncIncremental", existingOnQuest.Count, songsToSync.Count));
                }
                else
                {
                    var confirm = MessageBox.Show(
                        LocalizationService.T("Quest_Dialog_ConfirmSyncMsg", songsToSync.Count, QuestService.ModdedSongsPath),
                        LocalizationService.T("Quest_Dialog_ConfirmSyncTitle"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );
                    if (confirm != MessageBoxResult.Yes)
                    {
                        IsBusy = false;
                        ProgressStatus = "Ready";
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error checking Quest songs: {ex.Message}");
                songsToSync = allSongDirs.ToList();
            }

            ProgressPercent = 0;
            ProgressStatus = "Syncing Songs...";
            _notifyCallback($"Transferring {songsToSync.Count} song(s) to Quest...");

            var progress = new Progress<(int Current, int Total)>(report =>
            {
                ProgressPercent = (int)((double)report.Current / report.Total * 100);
                ProgressStatus = $"Transferring song {report.Current}/{report.Total}...";
            });

            try
            {
                using var cts = new CancellationTokenSource();
                int transferred = await _questService.SyncSongsToQuestAsync(
                    SelectedDevice.Serial,
                    songsDir,
                    msg => AppendLog(msg),
                    progress,
                    cts.Token,
                    songsToSync
                );

                ProgressPercent = 100;
                ProgressStatus = $"Sync Complete! ({transferred} songs)";
                _notifyCallback($"Successfully synced {transferred} songs to Quest!");
                MessageBox.Show(
                    LocalizationService.T("Quest_Dialog_SyncCompleteMsg", transferred),
                    LocalizationService.T("Quest_Dialog_SyncCompleteTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                AppendLog($"Song Sync Error: {ex.Message}");
                _notifyCallback($"Song sync failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
