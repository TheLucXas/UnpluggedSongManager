using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using USM.Core.Services;
using USM.Core.Services.Localization;

namespace USM.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public GameDetector GameDetector { get; }
        public SearchViewModel SearchVM { get; }
        public ConverterViewModel ConverterVM { get; }
        public LibraryViewModel LibraryVM { get; }
        public QuestViewModel QuestVM { get; }
        public SettingsViewModel SettingsVM { get; }

        private int _selectedTabIndex = 0;
        private string _toastMessage = "Ready. Search or drop chart files to add songs to your VR library.";
        private bool _isGameConnected;

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => SetProperty(ref _selectedTabIndex, value);
        }

        public string ToastMessage
        {
            get => _toastMessage;
            set => SetProperty(ref _toastMessage, value);
        }

        public bool IsGameConnected
        {
            get => _isGameConnected;
            set => SetProperty(ref _isGameConnected, value);
        }

        public string GameStatusText => IsGameConnected ? USM.Core.Services.Localization.LocalizationService.T("Nav_GameConnected") : USM.Core.Services.Localization.LocalizationService.T("Nav_GameNotDetected");

        public ICommand LaunchGameCommand { get; }
        public ICommand ShowUpdatesCommand { get; }
        public ICommand ShowChartAttentionCommand { get; }
        public ICommand LaunchQuestGameCommand => QuestVM.LaunchGameCommand;
        public bool CanLaunchQuestGame => QuestVM.CanLaunchGame;
        private bool _isUpdatingMods;
        public bool IsUpdatingMods
        {
            get => _isUpdatingMods;
            private set
            {
                if (SetProperty(ref _isUpdatingMods, value))
                {
                    OnPropertyChanged(nameof(UpdateNoticeText));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        public bool HasAvailableUpdates => SettingsVM.IsUpdateAvailable || SettingsVM.HasPcModUpdate || SettingsVM.HasQuestModUpdate;
        public bool HasChartAttention => LibraryVM.OutdatedChartCount > 0 || LibraryVM.QuestSongsNeedingSyncCount > 0;
        public string ChartAttentionText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (LibraryVM.OutdatedChartCount > 0)
                    parts.Add(LocalizationService.T("Nav_ChartResyncPc", LibraryVM.OutdatedChartCount));
                if (LibraryVM.QuestSongsNeedingSyncCount > 0)
                    parts.Add(LocalizationService.T("Nav_ChartSyncQuest", LibraryVM.QuestSongsNeedingSyncCount));
                return "⚠ " + string.Join("  •  ", parts);
            }
        }
        public string UpdateNoticeText
        {
            get
            {
                if (IsUpdatingMods) return LocalizationService.T("Nav_UpdatingMods");
                var components = new System.Collections.Generic.List<string>();
                if (SettingsVM.IsUpdateAvailable) components.Add("USM");
                if (SettingsVM.HasPcModUpdate) components.Add("PC");
                if (SettingsVM.HasQuestModUpdate) components.Add("Quest");
                return USM.Core.Services.Localization.LocalizationService.T("Nav_UpdatesAvailable", string.Join(" / ", components));
            }
        }

        private void RefreshUpdateNotice()
        {
            OnPropertyChanged(nameof(HasAvailableUpdates));
            OnPropertyChanged(nameof(UpdateNoticeText));
        }

        public MainViewModel()
        {
            GameDetector = new GameDetector();
            USM.Core.Services.Localization.LocalizationService.Instance.Initialize(GameDetector.Language);
            _toastMessage = USM.Core.Services.Localization.LocalizationService.T("Toast_Ready");

            IsGameConnected = !string.IsNullOrEmpty(GameDetector.GamePath) && GameDetector.IsValidGamePath(GameDetector.GamePath);

            SearchVM = new SearchViewModel(GameDetector, Notify);
            ConverterVM = new ConverterViewModel(GameDetector, Notify);
            QuestVM = new QuestViewModel(GameDetector, Notify);
            LibraryVM = new LibraryViewModel(GameDetector, Notify, QuestVM);
            LibraryVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(LibraryViewModel.OutdatedChartCount)
                    or nameof(LibraryViewModel.QuestSongsNeedingSyncCount))
                {
                    OnPropertyChanged(nameof(HasChartAttention));
                    OnPropertyChanged(nameof(ChartAttentionText));
                }
            };
            SettingsVM = new SettingsViewModel(GameDetector, Notify, questViewModel: QuestVM);
            SettingsVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SettingsViewModel.IsUpdateAvailable) or nameof(SettingsViewModel.HasPcModUpdate) or nameof(SettingsViewModel.HasQuestModUpdate))
                    RefreshUpdateNotice();
            };
            ShowUpdatesCommand = new AsyncRelayCommand(_ => ShowUpdatesAsync(),
                _ => !IsUpdatingMods && !QuestVM.IsBusy && !SettingsVM.IsUpdatingMod && !SettingsVM.IsUpdatingApp);
            ShowChartAttentionCommand = new RelayCommand(_ =>
            {
                SelectedTabIndex = LibraryVM.OutdatedChartCount > 0 ? 2 : 3;
                if (SelectedTabIndex == 2) LibraryVM.SelectedLibraryTab = 0;
            });
            QuestVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(QuestViewModel.CanLaunchGame) or nameof(QuestViewModel.IsDeviceConnected))
                    OnPropertyChanged(nameof(CanLaunchQuestGame));
            };

            USM.Core.Services.Localization.LocalizationService.Instance.LanguageChanged += _ =>
            {
                OnPropertyChanged(nameof(GameStatusText));
                RefreshUpdateNotice();
                OnPropertyChanged(nameof(ChartAttentionText));
                ToastMessage = USM.Core.Services.Localization.LocalizationService.T("Toast_Ready");
            };

            LaunchGameCommand = new RelayCommand(LaunchGame);
        }

        private void NavigateToUpdates() => SelectedTabIndex = SettingsVM.HasQuestModUpdate
            && !SettingsVM.IsUpdateAvailable && !SettingsVM.HasPcModUpdate ? 3 : 4;

        private async Task ShowUpdatesAsync()
        {
            bool updatePc = SettingsVM.HasPcModUpdate;
            bool updateQuest = SettingsVM.HasQuestModUpdate;
            if (!updatePc && !updateQuest)
            {
                if (SettingsVM.CanUpdateApp) await SettingsVM.UpdateAppAsync();
                else NavigateToUpdates();
                return;
            }
            string targets = string.Join(" / ", new[] { updatePc ? "PC" : null, updateQuest ? "Quest" : null }
                .Where(value => value != null));
            if (MessageBox.Show(LocalizationService.T("Nav_UpdateModsConfirm", targets),
                LocalizationService.T("Nav_UpdateModsTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                NavigateToUpdates();
                return;
            }
            IsUpdatingMods = true;
            try
            {
                var results = new System.Collections.Generic.List<string>();
                if (updatePc)
                    results.Add("PC: " + LocalizationService.T(await SettingsVM.UpdatePcModAsync() ? "Nav_ModUpdateDone" : "Nav_ModUpdateFailed"));
                if (updateQuest)
                    results.Add("Quest: " + LocalizationService.T(await QuestVM.UpdateModAsync() ? "Nav_ModUpdateDone" : "Nav_ModUpdateFailed"));
                string summary = string.Join(Environment.NewLine, results);
                Notify(summary);
                MessageBox.Show(summary, LocalizationService.T("Nav_UpdateModsTitle"), MessageBoxButton.OK,
                    SettingsVM.HasPcModUpdate || SettingsVM.HasQuestModUpdate ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            finally { IsUpdatingMods = false; RefreshUpdateNotice(); }
        }

        private void LaunchGame()
        {
            try
            {
                var processes = Process.GetProcessesByName("Unplugged");
                if (processes.Length > 0)
                {
                    Notify("Unplugged VR is already running!");
                    MessageBox.Show("Unplugged VR is already running on your PC.", "Game Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Clean up any lingering zombie crash handlers from previous runs that lock Steam
                try
                {
                    foreach (var crashProc in Process.GetProcessesByName("UnityCrashHandler64"))
                    {
                        try
                        {
                            crashProc.Kill();
                            crashProc.WaitForExit(1000);
                        }
                        catch { }
                    }
                }
                catch { }

                if (GameDetector.IsModInstalled() && !string.IsNullOrEmpty(GameDetector.GamePath))
                    PcSteamVrBindings.Prepare(GameDetector.GamePath);

                Notify("Launching Unplugged VR via Steam...");

                // 1. Try launching through Steam URL protocol (App ID 1623390)
                try
                {
                    Process.Start(new ProcessStartInfo("steam://rungameid/1623390")
                    {
                        UseShellExecute = true
                    });
                    Notify("Unplugged VR launched via Steam!");
                    return;
                }
                catch { }

                // 2. Fallback to direct executable if configured
                string? gamePath = GameDetector.GamePath;
                if (!string.IsNullOrEmpty(gamePath))
                {
                    string exePath = Path.Combine(gamePath, "Unplugged.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo(exePath)
                        {
                            WorkingDirectory = gamePath,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                MessageBox.Show("Could not launch Unplugged VR. Ensure Steam is running or check the game folder path in Settings.", "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch game: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Notify(string message)
        {
            ToastMessage = message;
            IsGameConnected = !string.IsNullOrEmpty(GameDetector.GamePath) && GameDetector.IsValidGamePath(GameDetector.GamePath);
            OnPropertyChanged(nameof(GameStatusText));

            // Automatically refresh song library
            try
            {
                LibraryVM.RefreshLibrary();
            }
            catch { }
        }
    }
}
