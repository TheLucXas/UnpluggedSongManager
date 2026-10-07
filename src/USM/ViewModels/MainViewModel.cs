using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using USM.Core.Services;

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

        public string GameStatusText => IsGameConnected ? "Game Connected" : "Game Not Detected";

        public ICommand LaunchGameCommand { get; }

        public MainViewModel()
        {
            GameDetector = new GameDetector();
            IsGameConnected = !string.IsNullOrEmpty(GameDetector.GamePath) && GameDetector.IsValidGamePath(GameDetector.GamePath);

            SearchVM = new SearchViewModel(GameDetector, Notify);
            ConverterVM = new ConverterViewModel(GameDetector, Notify);
            LibraryVM = new LibraryViewModel(GameDetector, Notify);
            QuestVM = new QuestViewModel(GameDetector, Notify);
            SettingsVM = new SettingsViewModel(GameDetector, Notify);

            LaunchGameCommand = new RelayCommand(LaunchGame);
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
