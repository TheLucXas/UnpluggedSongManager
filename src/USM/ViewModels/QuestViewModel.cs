using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
        private readonly DispatcherTimer _deviceRefreshTimer;
        private bool _refreshingDevices;
        private bool _wirelessSettingsAreManual;
        private bool _applyingDetectedWirelessSettings;

        private ObservableCollection<QuestDevice> _devices = new();
        private QuestDevice? _selectedDevice;
        private string _wirelessIp = GameDetector.DefaultQuestWirelessIp;
        private int _wirelessPort = GameDetector.DefaultQuestWirelessPort;
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
                    OnPropertyChanged(nameof(CanLaunchGame));
                    _ = CheckInstalledPackagesAsync();
                }
            }
        }

        public bool IsDeviceConnected => SelectedDevice?.Status.Equals("device", StringComparison.OrdinalIgnoreCase) == true;
        public bool CanLaunchGame => IsDeviceConnected && IsModdedInstalled;

        public string WirelessIp
        {
            get => _wirelessIp;
            set
            {
                if (SetProperty(ref _wirelessIp, value) && !_applyingDetectedWirelessSettings)
                    SaveWirelessSettingsAfterUserEdit();
            }
        }

        public int WirelessPort
        {
            get => _wirelessPort;
            set
            {
                if (SetProperty(ref _wirelessPort, value) && !_applyingDetectedWirelessSettings)
                    SaveWirelessSettingsAfterUserEdit();
            }
        }

        public QuestService QuestService => _questService;

        public event EventHandler? QuestSongsChanged;

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
                    OnPropertyChanged(nameof(CanLaunchGame));
                    RefreshModUpdateProperties();
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

        private QuestModInstallationInfo? _installedModInfo;
        public QuestModInstallationInfo? InstalledModInfo
        {
            get => _installedModInfo;
            private set
            {
                if (SetProperty(ref _installedModInfo, value)) RefreshModUpdateProperties();
            }
        }

        public GitHubAssetInfo? AvailableModRelease { get; private set; }
        public void SetAvailableModRelease(GitHubAssetInfo? asset)
        {
            AvailableModRelease = asset;
            RefreshModUpdateProperties();
        }
        public bool IsModUpdateAvailable => IsModdedInstalled && InstalledModInfo != null &&
            (ModAssemblyMetadata.IsNewPackagedVersion(ModAssemblyMetadata.GetVersion(PackagedModAssemblyProvider.ExtractQuestModAssembly() ?? ""), InstalledModInfo.Version)
            || (AvailableModRelease?.ModVersion != null && UpdateService.CompareVersions(AvailableModRelease.ModVersion, InstalledModInfo.Version) > 0));
        public string ModActionText => LocalizationService.T(InstalledModInfo == null
            ? "Quest_ModInstall" : IsModUpdateAvailable ? "Quest_ModUpdate" : "Quest_ModReinstall");
        public string ModStatusText => LocalizationService.T(InstalledModInfo == null
            ? "Quest_ModMissing" : IsModUpdateAvailable ? "Quest_ModOutdated" : "Quest_ModCurrent");
        public bool IsQuestModDllInstalled => InstalledModInfo != null;

        private void RefreshModUpdateProperties()
        {
            OnPropertyChanged(nameof(IsModUpdateAvailable));
            OnPropertyChanged(nameof(ModActionText));
            OnPropertyChanged(nameof(ModStatusText));
            OnPropertyChanged(nameof(IsQuestModDllInstalled));
            CommandManager.InvalidateRequerySuggested();
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
        public ICommand UpdateModCommand { get; }
        public ICommand UninstallModCommand { get; }
        public ICommand LaunchGameCommand { get; }

        public QuestViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;
            _questService = new QuestService();

            var wirelessSettings = _gameDetector.GetQuestWirelessSettings();
            _wirelessIp = wirelessSettings.Ip;
            _wirelessPort = wirelessSettings.Port;
            _wirelessSettingsAreManual = wirelessSettings.IsManual;

            RefreshDevicesCommand = new AsyncRelayCommand(_ => RefreshDevicesAsync(discoverWireless: true), _ => !IsBusy);
            ConnectWirelessCommand = new AsyncRelayCommand(_ => ConnectWirelessAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(WirelessIp) && WirelessPort is >= 1 and <= 65535);
            BrowseApkCommand = new RelayCommand(BrowseApk, _ => !IsBusy);
            StartPatchCommand = new AsyncRelayCommand(_ => StartPatchPipelineAsync(), _ => !IsBusy && (IsDeviceConnected || (!string.IsNullOrEmpty(CustomApkPath) && File.Exists(CustomApkPath))));
            UninstallModdedCommand = new AsyncRelayCommand(_ => UninstallModdedAsync(), _ => !IsBusy && IsDeviceConnected && IsModdedInstalled);
            SyncSongsCommand = new AsyncRelayCommand(_ => SyncSongsAsync(), _ => !IsBusy && IsDeviceConnected && LocalSongCount > 0);
            ImportPcSaveCommand = new AsyncRelayCommand(_ => ImportPcSaveAsync(), _ => !IsBusy && IsDeviceConnected && IsModdedInstalled);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            UpdateModCommand = new AsyncRelayCommand(_ => UpdateModAsync(), _ => !IsBusy && IsDeviceConnected && IsModdedInstalled);
            UninstallModCommand = new AsyncRelayCommand(_ => UninstallModAsync(), _ => !IsBusy && IsDeviceConnected && InstalledModInfo != null);
            LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync, _ => !IsBusy && CanLaunchGame);
            DownloadQuestToolsCommand = new AsyncRelayCommand(_ => DownloadQuestToolsAsync(), _ => !IsBusy);

            LocalizationService.Instance.LanguageChanged += lang =>
            {
                RefreshModUpdateProperties();
                OnPropertyChanged(nameof(QuestToolsStatusText));
                _ = CheckInstalledPackagesAsync();
            };
            AppendLog(LocalizationService.T("Log_QuestInit"));
            _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _deviceRefreshTimer.Tick += async (_, _) =>
            {
                if (!IsBusy && !_refreshingDevices)
                    await RefreshDevicesAsync(discoverWireless: false);
            };
            _deviceRefreshTimer.Start();
            _ = RefreshDevicesAsync(discoverWireless: true);
        }

        private async Task LaunchGameAsync(object? parameter)
        {
            if (IsBusy || !CanLaunchGame || SelectedDevice == null) return;
            IsBusy = true;
            try
            {
                var result = await _questService.LaunchModdedGameAsync(SelectedDevice.Serial);
                AppendLog(result.Message);
                _notifyCallback(result.Message);
            }
            catch (Exception ex) { AppendLog(ex.Message); _notifyCallback(ex.Message); }
            finally { IsBusy = false; }
        }

        public async Task<bool> UpdateModAsync(bool showDialog = true)
        {
            if (IsBusy || SelectedDevice == null || !IsModdedInstalled) return false;
            if (showDialog && MessageBox.Show(LocalizationService.T("Quest_ModInstallConfirm"),
                LocalizationService.T("Quest_ModMaintenanceTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            string serial = SelectedDevice.Serial;
            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = LocalizationService.T("Quest_ModUpdating");
            try
            {
                var result = await _questService.UpdateModOnlyAsync(serial, AppendLog, AvailableModRelease);
                ProgressPercent = result.Success ? 100 : 0;
                ProgressStatus = result.Message;
                AppendLog(result.Message);
                _notifyCallback(result.Message);
                await CheckInstalledPackagesAsync();
                if (showDialog)
                    MessageBox.Show(result.Message, LocalizationService.T("Quest_ModMaintenanceTitle"),
                        MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
                return result.Success;
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
                _notifyCallback(LocalizationService.T("Quest_ModUpdateFailed"));
                return false;
            }
            finally { IsBusy = false; }
        }

        private async Task UninstallModAsync()
        {
            if (IsBusy || SelectedDevice == null || InstalledModInfo == null) return;
            if (MessageBox.Show(LocalizationService.T("Quest_ModUninstallConfirm"),
                LocalizationService.T("Quest_ModMaintenanceTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            IsBusy = true;
            try
            {
                var result = await _questService.UninstallModOnlyAsync(SelectedDevice.Serial, AppendLog);
                AppendLog(result.Message);
                _notifyCallback(result.Message);
                await CheckInstalledPackagesAsync();
                MessageBox.Show(result.Message, LocalizationService.T("Quest_ModMaintenanceTitle"),
                    MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
                MessageBox.Show(ex.Message, LocalizationService.T("Quest_ModMaintenanceTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { IsBusy = false; }
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
                ProgressStatus = LocalizationService.T("Ui_Ready");
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

        private async Task RefreshDevicesAsync(bool discoverWireless)
        {
            if (_refreshingDevices) return;
            _refreshingDevices = true;
            try
            {
                var list = await _questService.GetDevicesAsync();

                if (discoverWireless && !_wirelessSettingsAreManual)
                {
                    WirelessAdbEndpoint? endpoint = await _questService.DiscoverWirelessAdbAsync(AppendLog, attempts: 2);
                    if (endpoint != null)
                    {
                        SetDetectedWirelessSettings(endpoint.Ip, endpoint.Port);
                        var (connected, message) = await _questService.ConnectWirelessAsync(endpoint.Ip, endpoint.Port);
                        AppendLog(connected
                            ? $"Connected to the mDNS-discovered Quest: {message}"
                            : $"mDNS found {endpoint.Ip}:{endpoint.Port}, but ADB could not connect: {message}");
                        list = await _questService.GetDevicesAsync();
                    }
                    else
                    {
                        SetDetectedWirelessSettings(GameDetector.DefaultQuestWirelessIp, GameDetector.DefaultQuestWirelessPort);
                        AppendLog($"Wireless ADB was not found. Enter the Quest IP manually (default: {GameDetector.DefaultQuestWirelessIp} / port {GameDetector.DefaultQuestWirelessPort}).");
                    }
                }

                if (Devices.Count == list.Count && Devices.Zip(list, (oldDevice, newDevice) =>
                    oldDevice.Serial == newDevice.Serial && oldDevice.Status == newDevice.Status &&
                    oldDevice.Model == newDevice.Model).All(same => same))
                    return;

                string? selectedSerial = SelectedDevice?.Serial;
                Devices.Clear();
                foreach (var d in list)
                {
                    Devices.Add(d);
                }

                var connectedDevice = Devices.FirstOrDefault(d => d.Serial == selectedSerial && d.Status == "device")
                    ?? Devices.FirstOrDefault(d => d.Status == "device");
                if (connectedDevice != null)
                {
                    SelectedDevice = connectedDevice;
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
            finally
            {
                _refreshingDevices = false;
            }
        }

        private async Task ConnectWirelessAsync()
        {
            if (string.IsNullOrWhiteSpace(WirelessIp) || WirelessPort is < 1 or > 65535) return;
            _gameDetector.SaveQuestWirelessSettings(WirelessIp, WirelessPort, _wirelessSettingsAreManual);
            IsConnectingWireless = true;
            AppendLog(LocalizationService.T("Log_ConnectingWireless", WirelessIp, WirelessPort));

            try
            {
                var (ok, msg) = await _questService.ConnectWirelessAsync(WirelessIp, WirelessPort);
                if (ok)
                {
                    AppendLog(LocalizationService.T("Log_WirelessConnected", msg));
                    _notifyCallback($"Quest Wi-Fi connected ({WirelessIp})");
                    await RefreshDevicesAsync(discoverWireless: false);
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

        private void SaveWirelessSettingsAfterUserEdit()
        {
            _wirelessSettingsAreManual =
                !string.Equals(WirelessIp, GameDetector.DefaultQuestWirelessIp, StringComparison.OrdinalIgnoreCase) ||
                WirelessPort != GameDetector.DefaultQuestWirelessPort;
            _gameDetector.SaveQuestWirelessSettings(WirelessIp, WirelessPort, _wirelessSettingsAreManual);
        }

        private void SetDetectedWirelessSettings(string ip, int port)
        {
            _applyingDetectedWirelessSettings = true;
            try
            {
                WirelessIp = ip;
                WirelessPort = port;
            }
            finally
            {
                _applyingDetectedWirelessSettings = false;
            }

            _wirelessSettingsAreManual = false;
            _gameDetector.SaveQuestWirelessSettings(WirelessIp, WirelessPort, isManual: false);
        }

        private async Task CheckInstalledPackagesAsync()
        {
            if (!IsDeviceConnected)
            {
                PackageStatusText = LocalizationService.T("Quest_StatusNoDevice");
                IsOriginalInstalled = false;
                IsModdedInstalled = false;
                OriginalVersion = null;
                ModdedVersion = null;
                InstalledModInfo = null;
                PatchButtonText = LocalizationService.T("Quest_PatchButton_Install");
                PatchButtonDescription = LocalizationService.T("Quest_PatchDesc_Install");
                return;
            }

            string serial = SelectedDevice!.Serial;
            bool StillConnected() => IsDeviceConnected && SelectedDevice?.Serial == serial;
            PackageStatusText = LocalizationService.T("Quest_StatusChecking");
            IsOriginalInstalled = false;
            IsModdedInstalled = false;
            OriginalVersion = null;
            ModdedVersion = null;
            InstalledModInfo = null;
            try
            {
                bool originalInstalled = await _questService.IsPackageInstalledAsync(serial, QuestService.OriginalPackage);
                if (!StillConnected()) return;
                bool moddedInstalled = await _questService.IsPackageInstalledAsync(serial, QuestService.ModdedPackage);
                if (!StillConnected()) return;
                IsOriginalInstalled = originalInstalled;
                IsModdedInstalled = moddedInstalled;

                if (IsOriginalInstalled)
                {
                    string? originalVersion = await _questService.GetPackageVersionAsync(serial, QuestService.OriginalPackage);
                    if (!StillConnected()) return;
                    OriginalVersion = originalVersion;
                }
                else
                {
                    OriginalVersion = null;
                }

                if (IsModdedInstalled)
                {
                    string? moddedVersion = await _questService.GetPackageVersionAsync(serial, QuestService.ModdedPackage);
                    if (!StillConnected()) return;
                    ModdedVersion = moddedVersion;
                    var installedMod = await _questService.GetInstalledQuestModInfoAsync(serial, QuestService.ModdedPackage);
                    if (!StillConnected()) return;
                    InstalledModInfo = installedMod;
                }
                else
                {
                    ModdedVersion = null;
                    InstalledModInfo = null;
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
                if (!StillConnected()) return;
                InstalledModInfo = null;
                PackageStatusText = $"Error checking packages: {ex.Message}";
            }
        }

        private void BrowseApk(object? _)
        {
            var dlg = new OpenFileDialog
            {
                Title = LocalizationService.T("Ui_SelectApk"),
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
            ProgressStatus = LocalizationService.T("Ui_Starting");
            _notifyCallback(LocalizationService.T("Ui_QuestPatching"));

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
                    QuestSongsChanged?.Invoke(this, EventArgs.Empty);
                    ProgressPercent = 100;
                    ProgressStatus = LocalizationService.T("Ui_Done");
                    _notifyCallback(LocalizationService.T("Ui_QuestPatched"));
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
                    ProgressStatus = LocalizationService.T("Ui_Failed");
                    _notifyCallback(LocalizationService.T("Ui_QuestPatchFailed"));
                    MessageBox.Show(LocalizationService.T("Quest_Dialog_PatchErrorMsg"), LocalizationService.T("Quest_Dialog_PatchErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"FATAL ERROR: {ex.Message}");
                ProgressStatus = LocalizationService.T("Ui_FatalError");
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
            ProgressStatus = LocalizationService.T("Ui_Uninstalling");
            _notifyCallback(LocalizationService.T("Ui_QuestUninstalling"));

            try
            {
                var (ok, msg) = await _questService.UninstallModdedPackageAsync(SelectedDevice.Serial, msg => AppendLog(msg));
                if (ok)
                {
                    ProgressPercent = 100;
                    ProgressStatus = LocalizationService.T("Ui_UninstallDone");
                    _notifyCallback(LocalizationService.T("Ui_QuestUninstalled"));
                    MessageBox.Show(LocalizationService.T("Quest_Dialog_UninstallCompleteMsg"), LocalizationService.T("Quest_Dialog_UninstallCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    await CheckInstalledPackagesAsync();
                }
                else
                {
                    ProgressStatus = LocalizationService.T("Ui_UninstallFailed");
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
                MessageBox.Show(LocalizationService.T("Ui_NoSongsFolder"), LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var allSongDirs = Directory.GetFiles(songsDir, "*.usm");
            if (allSongDirs.Length == 0)
            {
                MessageBox.Show(LocalizationService.T("Ui_NoSongs"), LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IsBusy = true;
            ProgressStatus = LocalizationService.T("Ui_PreparingQuestCharts");
            _notifyCallback(LocalizationService.T("Ui_QuestPreparingSongs"));

            // An older .usm may have the same filename on both devices but lack the
            // native hand mapping required by the current Quest mod. Prepare the
            // local archive before deciding which songs need transferring.
            var upgradedSongNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string songPath in allSongDirs)
            {
                try
                {
                    int added = await Task.Run(() => UsmPacker.EnsureQuestHandCharts(songPath));
                    if (added > 0)
                    {
                        upgradedSongNames.Add(Path.GetFileName(songPath));
                        AppendLog($"Prepared {added} Quest hand chart(s) for {Path.GetFileName(songPath)}.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Could not prepare Quest hand charts for {Path.GetFileName(songPath)}: {ex.Message}");
                    MessageBox.Show(LocalizationService.T("Ui_QuestPreparingSongFailed", Path.GetFileName(songPath), ex.Message),
                        LocalizationService.T("Quest_CustomSongsSyncTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    IsBusy = false;
                    ProgressStatus = LocalizationService.T("Ui_Ready");
                    return;
                }
            }

            ProgressStatus = LocalizationService.T("Ui_CheckingQuestSongs");
            _notifyCallback(LocalizationService.T("Ui_QuestCheckingSongs"));

            List<string> songsToSync;
            try
            {
                var existingOnQuest = await _questService.GetQuestInstalledSongsAsync(SelectedDevice.Serial);
                var remoteSizes = existingOnQuest.Count == 0
                    ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                    : await _questService.GetQuestInstalledSongSizesAsync(SelectedDevice.Serial);
                songsToSync = allSongDirs
                    .Where(dir => !existingOnQuest.Contains(Path.GetFileName(dir)) ||
                        upgradedSongNames.Contains(Path.GetFileName(dir)) ||
                        !remoteSizes.TryGetValue(Path.GetFileName(dir), out long remoteSize) ||
                        remoteSize != new FileInfo(dir).Length)
                    .ToList();

                var sameSizeSongs = allSongDirs.Except(songsToSync).ToList();
                if (sameSizeSongs.Count > 0)
                {
                    // Resync can change notes without changing the archive size.
                    // Compare contents before calling a song up to date on Quest.
                    var remoteHashes = await _questService.GetQuestInstalledSongHashesAsync(SelectedDevice.Serial);
                    var changedSongs = await Task.Run(() => sameSizeSongs.Where(path =>
                    {
                        if (!remoteHashes.TryGetValue(Path.GetFileName(path), out string? remoteHash))
                            return true;
                        using var file = File.OpenRead(path);
                        string localHash = Convert.ToHexString(SHA256.HashData(file));
                        return !string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
                    }).ToList());
                    songsToSync.AddRange(changedSongs);
                }

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
                        ProgressStatus = LocalizationService.T("Ui_Ready");
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
                        ProgressStatus = LocalizationService.T("Ui_Ready");
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
                        ProgressStatus = LocalizationService.T("Ui_Ready");
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
            ProgressStatus = LocalizationService.T("Ui_SyncingSongs");
            _notifyCallback($"Transferring {songsToSync.Count} song(s) to Quest...");

            var progress = new Progress<(int Current, int Total)>(report =>
            {
                ProgressPercent = (int)((double)report.Current / report.Total * 100);
                ProgressStatus = LocalizationService.T("Ui_QuestTransferringSong", report.Current, report.Total);
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

                QuestSongsChanged?.Invoke(this, EventArgs.Empty);

                ProgressPercent = 100;
                ProgressStatus = LocalizationService.T("Ui_QuestSyncDone", transferred);
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
