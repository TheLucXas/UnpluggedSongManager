using System;
using System.Text.Json.Serialization;

namespace USM.Core.Models
{
    public class ChorusChart
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

        public bool IsInstalled { get; set; } = false;
        public bool IsDownloading { get; set; } = false;
        public double DownloadProgress { get; set; } = 0.0;
        public string StatusText { get; set; } = string.Empty;
    }

    public class SongIni
    {
        public string Name { get; set; } = "Unknown Title";
        public string Artist { get; set; } = "Unknown Artist";
        public string Charter { get; set; } = "Unknown";
        public double PreviewStart { get; set; } = 30.0;
        public double Delay { get; set; } = 3.0;
    }

    public class InstalledSong
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
        public bool IsSelected { get; set; } = false;
        public bool IsPlaying { get; set; } = false;
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
