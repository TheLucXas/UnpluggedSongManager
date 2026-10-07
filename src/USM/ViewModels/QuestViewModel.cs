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
        private string _packageStatusText = "Connect a Quest headset to detect Unplugged status.";
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
                }
            }
        }

        private bool _skipTutorialAndUnlock = true;
        public bool SkipTutorialAndUnlock
        {
            get => _skipTutorialAndUnlock;
            set => SetProperty(ref _skipTutorialAndUnlock, value);
        }

        private string _patchButtonText = "🚀 Parchear e Instalar 'Unplugged Modded'";
        public string PatchButtonText
        {
            get => _patchButtonText;
            set => SetProperty(ref _patchButtonText, value);
        }

        private string _patchButtonDescription = "Extrae el juego de tu Quest, lo clona en una app independiente ('Unplugged Modded') con soporte de custom songs y omisión de tutorial, y lo instala junto a tu juego original.";
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
                    return Directory.GetDirectories(songsDir).Length;
                }
                return 0;
            }
        }

        public ICommand RefreshDevicesCommand { get; }
        public ICommand ConnectWirelessCommand { get; }
        public ICommand BrowseApkCommand { get; }
        public ICommand StartPatchCommand { get; }
        public ICommand UninstallModdedCommand { get; }
        public ICommand SyncSongsCommand { get; }
        public ICommand ClearLogsCommand { get; }

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
            ClearLogsCommand = new RelayCommand(ClearLogs);

            AppendLog("Quest Patcher Initialized. Searching for Quest headsets via ADB...");
            _ = RefreshDevicesAsync();
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
                    AppendLog($"Found {Devices.Count} device(s). Selected: {SelectedDevice.DisplayName}");
                }
                else
                {
                    SelectedDevice = null;
                    PackageStatusText = "No Quest device found. Connect via USB or enter your Quest's IP address.";
                    AppendLog("No devices found on ADB.");
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
            AppendLog($"Attempting wireless connection to {WirelessIp}:{WirelessPort}...");

            try
            {
                var (ok, msg) = await _questService.ConnectWirelessAsync(WirelessIp, WirelessPort);
                if (ok)
                {
                    AppendLog($"Wireless ADB connected! ({msg})");
                    _notifyCallback($"Quest Wi-Fi connected ({WirelessIp})");
                    await RefreshDevicesAsync();
                }
                else
                {
                    AppendLog($"Failed to connect wirelessly: {msg}");
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
                PackageStatusText = "No Quest headset connected.";
                IsOriginalInstalled = false;
                IsModdedInstalled = false;
                OriginalVersion = null;
                ModdedVersion = null;
                PatchButtonText = "🚀 Parchear e Instalar 'Unplugged Modded'";
                PatchButtonDescription = "Extrae el juego de tu Quest, lo clona en una app independiente ('Unplugged Modded') con soporte de custom songs y omisión de tutorial, y lo instala junto a tu juego original.";
                return;
            }

            if (SelectedDevice.Status.Equals("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                PackageStatusText = "⚠ Visor no autorizado: Por favor ponte el Quest y presiona 'Permitir depuración USB'.";
                AppendLog("WARNING: Device is unauthorized. Please accept the USB debugging prompt inside your Quest headset.");
                IsOriginalInstalled = false;
                IsModdedInstalled = false;
                return;
            }

            PackageStatusText = "Checking Unplugged packages on Quest...";
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
                    sb.Append($"✔ Stock Unplugged detectado{(OriginalVersion != null ? $" (v{OriginalVersion})" : "")}. ");
                }
                else
                {
                    sb.Append("❌ Stock Unplugged no encontrado. ");
                }

                if (IsModdedInstalled)
                {
                    sb.Append($"✔ Modded Unplugged instalado{(ModdedVersion != null ? $" (v{ModdedVersion})" : "")}.");
                }
                else
                {
                    sb.Append("⚪ Modded Unplugged no instalado aún.");
                }

                PackageStatusText = sb.ToString();
                AppendLog($"Quest Packages Status: {PackageStatusText}");

                // Dynamic button text & description based on installed state & versions
                if (!IsModdedInstalled)
                {
                    PatchButtonText = "🚀 Parchear e Instalar 'Unplugged Modded'";
                    PatchButtonDescription = "Extrae el juego de tu Quest, lo clona en una app independiente ('Unplugged Modded') con soporte de custom songs y omisión de tutorial, y lo instala junto a tu juego original.";
                }
                else
                {
                    if (!string.IsNullOrEmpty(OriginalVersion) && !string.IsNullOrEmpty(ModdedVersion) && !OriginalVersion.Equals(ModdedVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        PatchButtonText = "🔄 Actualizar 'Unplugged Modded'";
                        PatchButtonDescription = $"Hay una actualización del juego oficial (v{OriginalVersion} vs Mod v{ModdedVersion}). Vuelve a parchear para mantener la versión al día.";
                    }
                    else
                    {
                        PatchButtonText = "🔨 Reinstalar / Reparar 'Unplugged Modded'";
                        PatchButtonDescription = $"La app modeada ya está instalada en tu visor{(ModdedVersion != null ? $" (v{ModdedVersion})" : "")}. Puedes repararla o reinstalarla para restaurar archivos o aplicar cambios.";
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
                MessageBox.Show("Por favor conecta un visor Quest o selecciona un APK local válido.", "Quest Patcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string actionMsg = IsModdedInstalled ? "reinstalar / actualizar" : "parchear y clonar";
            var confirm = MessageBox.Show(
                $"Esto va a {actionMsg} Unplugged: Air Guitar como app independiente ('Unplugged Modded')\n" +
                $"que coexistirá con el juego original, con soporte de Custom Songs{(SkipTutorialAndUnlock ? " y tutorial omitido (campaña desbloqueada)" : "")}.\n\n" +
                "¿Deseas continuar?",
                "Confirmar Mod Quest",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = "Iniciando...";
            _notifyCallback("Proceso de parcheo Quest en progreso...");

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
                    SkipTutorialAndUnlock,
                    progress,
                    msg => AppendLog(msg),
                    cts.Token
                );

                if (success)
                {
                    ProgressPercent = 100;
                    ProgressStatus = "¡Parcheo e Instalación completados!";
                    _notifyCallback("¡Unplugged Modded instalado correctamente en Quest!");
                    MessageBox.Show(
                        "¡'Unplugged Modded' fue creado e instalado exitosamente en tu Meta Quest!\n\n" +
                        "• Encontrarás 'Unplugged Modded' en Fuentes Desconocidas de tu visor.\n" +
                        (SkipTutorialAndUnlock ? "• Se ha omitido el tutorial: entrarás directamente al estudio con todas las salas base disponibles.\n" : "") +
                        "• Ahora puedes presionar 'Sync Songs to Quest' para transferir todas tus canciones custom.",
                        "Instalación en Quest Completa",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    await CheckInstalledPackagesAsync();
                }
                else
                {
                    ProgressStatus = "Fallo. Revisa la consola de registros.";
                    _notifyCallback("Fallo el parcheo de Quest. Revisa el registro.");
                    MessageBox.Show("Fallo al parchear o instalar el APK. Revisa la consola de registros para más detalles.", "Error de Parcheo", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"FATAL ERROR: {ex.Message}");
                ProgressStatus = "Error Fatal";
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
                "¿Estás seguro de que deseas desinstalar 'Unplugged Modded' de tu Meta Quest?\n\n" +
                "• Tu juego original (stock) NO se verá afectado.\n" +
                "• Las canciones custom guardadas en /sdcard/Unplugged/Songs NO se borrarán.\n" +
                "• La app modeada clonada y sus datos temporales serán desinstalados.",
                "Confirmar Desinstalación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = "Desinstalando app modeada del Quest...";
            _notifyCallback("Desinstalando Unplugged Modded...");

            try
            {
                var (ok, msg) = await _questService.UninstallModdedPackageAsync(SelectedDevice.Serial, msg => AppendLog(msg));
                if (ok)
                {
                    ProgressPercent = 100;
                    ProgressStatus = "Desinstalación exitosa";
                    _notifyCallback("Unplugged Modded ha sido desinstalado.");
                    MessageBox.Show("Unplugged Modded ha sido desinstalado correctamente de tu Meta Quest.", "Desinstalación Completa", MessageBoxButton.OK, MessageBoxImage.Information);
                    await CheckInstalledPackagesAsync();
                }
                else
                {
                    ProgressStatus = "Fallo al desinstalar";
                    _notifyCallback($"Error: {msg}");
                    MessageBox.Show($"Fallo al desinstalar la app modeada:\n{msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error al desinstalar: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SyncSongsAsync()
        {
            if (SelectedDevice == null)
            {
                MessageBox.Show("Please connect a Quest device first.", "Sync Songs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? songsDir = _gameDetector.GetCustomSongsDir();
            if (string.IsNullOrEmpty(songsDir) || !Directory.Exists(songsDir))
            {
                MessageBox.Show("No custom songs directory found in USM.", "Sync Songs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var songDirs = Directory.GetDirectories(songsDir);
            if (songDirs.Length == 0)
            {
                MessageBox.Show("No custom songs found to transfer.", "Sync Songs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Sync {songDirs.Length} custom song(s) to Quest (/sdcard/Unplugged/Songs)?",
                "Sync Custom Songs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            ProgressPercent = 0;
            ProgressStatus = "Syncing Songs...";
            _notifyCallback($"Transferring {songDirs.Length} songs to Quest...");

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
                    cts.Token
                );

                ProgressPercent = 100;
                ProgressStatus = $"Sync Complete! ({transferred} songs)";
                _notifyCallback($"Successfully synced {transferred} songs to Quest!");
                MessageBox.Show(
                    $"Successfully synced {transferred} songs to your Quest!\n" +
                    "They will appear inside Unplugged Modded.",
                    "Songs Synced",
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
