using System;
using System.IO;
using USM.Core.Services;

namespace USM.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        public GameDetector GameDetector { get; }
        public SearchViewModel SearchVM { get; }
        public ConverterViewModel ConverterVM { get; }
        public LibraryViewModel LibraryVM { get; }
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

        public MainViewModel()
        {
            GameDetector = new GameDetector();
            IsGameConnected = !string.IsNullOrEmpty(GameDetector.GamePath) && GameDetector.IsValidGamePath(GameDetector.GamePath);

            SearchVM = new SearchViewModel(GameDetector, Notify);
            ConverterVM = new ConverterViewModel(GameDetector, Notify);
            LibraryVM = new LibraryViewModel(GameDetector, Notify);
            SettingsVM = new SettingsViewModel(GameDetector, Notify);
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
