using System;
using System.Text.Json.Serialization;
using USM.ViewModels;

namespace USM.Core.Models
{
    public class ChorusChart : ViewModelBase
    {
        public string Id { get; set; } = string.Empty;
        public string Md5 { get; set; } = string.Empty;
        public string Title { get; set; } = "Unknown Title";
        public string Artist { get; set; } = "Unknown Artist";
        public string Album { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Charter { get; set; } = "Unknown";
        public string Duration { get; set; } = "--:--";
        public int DurationSeconds { get; set; } = 0;
        public int DiffGuitar { get; set; } = -1;
        public int DiffBass { get; set; } = -1;
        public double PreviewStart { get; set; } = 30.0;
        public string? DownloadUrl { get; set; }
        public string? ArtUrl { get; set; }

        public string SubtitleDisplay => string.IsNullOrEmpty(Album)
            ? (string.IsNullOrEmpty(Year) ? Artist : $"{Artist} ({Year})")
            : $"{Artist} • {Album}{(string.IsNullOrEmpty(Year) ? "" : $" ({Year})")}";

        public string MetaDisplay => $"Charter: {Charter} | Duration: {Duration} | {(DiffGuitar >= 0 ? $"Guitar: Tier {DiffGuitar}" : "Guitar: Yes")}";

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (SetProperty(ref _isInstalled, value))
                {
                    OnPropertyChanged(nameof(CanDownload));
                }
            }
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                if (SetProperty(ref _isDownloading, value))
                {
                    OnPropertyChanged(nameof(CanDownload));
                }
            }
        }

        private bool _isQueued;
        public bool IsQueued
        {
            get => _isQueued;
            set
            {
                if (SetProperty(ref _isQueued, value))
                {
                    OnPropertyChanged(nameof(CanDownload));
                }
            }
        }

        public bool CanDownload => !IsInstalled && !IsDownloading && !IsQueued;

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set => SetProperty(ref _downloadProgress, value);
        }

        private string _statusText = "📥 Download";
        public string StatusText
        {
            get => string.IsNullOrEmpty(_statusText) ? "📥 Download" : _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string? _previewUrl;
        public string? PreviewUrl
        {
            get => _previewUrl;
            set => SetProperty(ref _previewUrl, value);
        }

        private bool _isPreviewLoading;
        public bool IsPreviewLoading
        {
            get => _isPreviewLoading;
            set
            {
                if (SetProperty(ref _isPreviewLoading, value))
                {
                    OnPropertyChanged(nameof(PreviewButtonIcon));
                }
            }
        }

        private bool _isPreviewPlaying;
        public bool IsPreviewPlaying
        {
            get => _isPreviewPlaying;
            set
            {
                if (SetProperty(ref _isPreviewPlaying, value))
                {
                    OnPropertyChanged(nameof(PreviewButtonIcon));
                }
            }
        }

        public string PreviewButtonIcon => IsPreviewPlaying ? "⏹" : (IsPreviewLoading ? "⌛" : "▶");
    }

    public class SongIni
    {
        public string Name { get; set; } = "Unknown Title";
        public string Artist { get; set; } = "Unknown Artist";
        public string Charter { get; set; } = "Unknown";
        public double PreviewStart { get; set; } = 30.0;
        public double Delay { get; set; } = 3.0;
    }

    public class InstalledSong : ViewModelBase
    {
        public string FolderName { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Charter { get; set; } = string.Empty;
        public string? CoverPath { get; set; }
        public string? AudioPath { get; set; }
        public bool HasCover => !string.IsNullOrEmpty(CoverPath) && System.IO.File.Exists(CoverPath);
        public bool IsMultiTrack { get; set; } = false;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }
    }

    public class ConversionProgress
    {
        public double Fraction { get; set; }
        public string Message { get; set; } = string.Empty;

        public ConversionProgress(double fraction, string message)
        {
            Fraction = fraction;
            Message = message;
        }
    }
}
