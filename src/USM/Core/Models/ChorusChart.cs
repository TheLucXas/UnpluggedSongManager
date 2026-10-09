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
        public string Genre { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Charter { get; set; } = "Unknown";
        public string Duration { get; set; } = "--:--";
        public int DurationSeconds { get; set; } = 0;
        public int DiffGuitar { get; set; } = -1;
        public int DiffBass { get; set; } = -1;
        public double PreviewStart { get; set; } = 30.0;
        public string? DownloadUrl { get; set; }
        public string? ArtUrl { get; set; }
        public DateTime? ModifiedTime { get; set; }
        public string ResyncChoiceLabel => $"{Artist} — {Title}  |  {Charter}  |  {Id}";

        public string SubtitleDisplay => string.IsNullOrEmpty(Album)
            ? (string.IsNullOrEmpty(Year) ? Artist : $"{Artist} ({Year})")
            : $"{Artist} • {Album}{(string.IsNullOrEmpty(Year) ? "" : $" ({Year})")}";

        public string MetaDisplay => $"Charter: {Charter} | Duration: {Duration} | {(DiffGuitar >= 0 ? $"Guitar: Tier {DiffGuitar}" : "Guitar: Yes")}";
        public string GuitarDifficultyDisplay => DiffGuitar >= 0 ? $"🎸 Tier {DiffGuitar}" : "🎸 Guitar";

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
        public System.Windows.Media.ImageSource? CoverImage { get; set; }
        public string? AudioPath { get; set; }
        public bool HasCover => CoverImage != null || (!string.IsNullOrEmpty(CoverPath) && System.IO.File.Exists(CoverPath));
        public bool IsMultiTrack { get; set; } = false;
        public bool IsHalfTrack { get; set; } = false;
        public string? ChorusChartId { get; set; }
        public string? ChorusChartMd5 { get; set; }
        public int ChartParserVersion { get; set; }
        public bool NeedsChartResync => ChartParserVersion < USM.Core.Parsers.JsonGenerator.ParserVersion;
        public string TrackTypeDisplay => IsHalfTrack ? "🎚 2-Track (Instrumental + Vocals)" : IsMultiTrack ? "🎸 MultiTrack" : "🎵 Stereo";

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
