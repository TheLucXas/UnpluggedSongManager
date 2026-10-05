using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using USM.Core.Models;
using USM.Core.Services;

namespace USM.ViewModels
{
    public class ConverterViewModel : ViewModelBase
    {
        private readonly GameDetector _gameDetector;
        private readonly Action<string> _notifyCallback;

        private bool _isConverting;
        private double _overallProgress;
        private string _statusText = "Status: Ready (Drop or select files to convert automatically)";
        private string _logText = string.Empty;
        private CancellationTokenSource? _cts;

        public bool IsConverting
        {
            get => _isConverting;
            set => SetProperty(ref _isConverting, value);
        }

        public double OverallProgress
        {
            get => _overallProgress;
            set => SetProperty(ref _overallProgress, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string LogText
        {
            get => _logText;
            set => SetProperty(ref _logText, value);
        }

        public ObservableCollection<string> Queue { get; } = new();

        public ICommand BrowseFilesCommand { get; }
        public ICommand BrowseFolderCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand CancelCommand { get; }

        public ConverterViewModel(GameDetector gameDetector, Action<string> notifyCallback)
        {
            _gameDetector = gameDetector;
            _notifyCallback = notifyCallback;

            BrowseFilesCommand = new RelayCommand(BrowseFiles);
            BrowseFolderCommand = new RelayCommand(BrowseFolder);
            ClearQueueCommand = new RelayCommand(ClearQueue);
            CancelCommand = new RelayCommand(CancelConversion);
        }

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".sng"
        };

        public void AddFilesToQueue(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    try
                    {
                        bool hasChart = Directory.GetFiles(p, "*.*", SearchOption.AllDirectories)
                            .Any(f =>
                            {
                                string fn = Path.GetFileName(f).ToLowerInvariant();
                                return fn == "notes.chart" || fn == "notes.mid" || fn == "song.chart";
                            });

                        if (hasChart)
                        {
                            if (!Queue.Contains(p)) Queue.Add(p);
                        }
                        else
                        {
                            AppendLog($"[SKIP] Ignored folder '{Path.GetFileName(p)}': No notes.chart or notes.mid found.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[SKIP] Error inspecting folder '{Path.GetFileName(p)}': {ex.Message}");
                    }
                }
                else if (File.Exists(p))
                {
                    string ext = Path.GetExtension(p);
                    if (AllowedExtensions.Contains(ext))
                    {
                        if (!Queue.Contains(p)) Queue.Add(p);
                    }
                    else
                    {
                        AppendLog($"[SKIP] Ignored unsupported file '{Path.GetFileName(p)}' (Only .zip and .sng archives are supported).");
                    }
                }
            }

            if (!IsConverting && Queue.Count > 0)
            {
                _ = ProcessQueueAsync();
            }
        }

        private void BrowseFiles()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Chart Packages (.zip or .sng)",
                Filter = "Supported Chart Packages (*.zip;*.sng)|*.zip;*.sng|Clone Hero SNG (*.sng)|*.sng|Zip Archives (*.zip)|*.zip",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                AddFilesToQueue(dlg.FileNames);
            }
        }

        private void BrowseFolder()
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Uncompressed Song Folder",
                Multiselect = false
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
            {
                AddFilesToQueue(new[] { dlg.FolderName });
            }
        }

        private void ClearQueue()
        {
            if (!IsConverting)
            {
                Queue.Clear();
                StatusText = "Queue cleared. Ready for new files.";
            }
        }

        private void CancelConversion()
        {
            if (IsConverting && _cts != null)
            {
                AppendLog("[Cancel] User requested cancellation.");
                _cts.Cancel();
            }
        }

        public void AppendLog(string message)
        {
            LogText += message + Environment.NewLine;
        }

        private async Task ProcessQueueAsync()
        {
            string? customSongsDir = _gameDetector.GetCustomSongsDir();
            if (string.IsNullOrEmpty(customSongsDir))
            {
                AppendLog("[ERROR] Unplugged CustomSongs folder is not configured! Check Settings.");
                return;
            }

            IsConverting = true;
            _cts = new CancellationTokenSource();

            int total = Queue.Count;
            int completed = 0;

            try
            {
                while (Queue.Count > 0)
                {
                    if (_cts.IsCancellationRequested) break;

                    string currentItem = Queue[0];
                    string itemName = Path.GetFileName(currentItem);
                    if (string.IsNullOrEmpty(itemName)) itemName = currentItem;

                    StatusText = $"Converting ({completed + 1}/{total}): {itemName}...";
                    AppendLog($"\n>>> Processing: {itemName}");

                    var progress = new Progress<ConversionProgress>(cp =>
                    {
                        double baseProg = (double)completed / total;
                        OverallProgress = baseProg + (cp.Fraction / total);
                    });

                    bool success = await SongConverter.ConvertSongAsync(
                        currentItem,
                        customSongsDir,
                        progress,
                        AppendLog,
                        _cts.Token
                    );

                    Queue.RemoveAt(0);
                    completed++;

                    if (success)
                    {
                        _notifyCallback?.Invoke($"Converted: {itemName}");
                    }
                }

                StatusText = _cts.IsCancellationRequested
                    ? "Conversion stopped by user."
                    : $"Completed! {completed} songs converted and ready in Unplugged VR.";
                OverallProgress = 1.0;
            }
            catch (Exception ex)
            {
                AppendLog($"[FATAL ERROR] {ex.Message}");
                StatusText = "Conversion error occurred.";
            }
            finally
            {
                IsConverting = false;
                _cts = null;
            }
        }
    }
}
