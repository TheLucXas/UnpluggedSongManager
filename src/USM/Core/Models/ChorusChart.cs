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
            set => SetProperty(ref _isInstalled, value);
        }

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set => SetProperty(ref _isDownloading, value);
        }

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
