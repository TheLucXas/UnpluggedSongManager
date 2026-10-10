using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using USM.Core.Services;
using USM.Core.Services.Localization;
using USM.ViewModels;

namespace USM.Core.Models
{
    public sealed class ChorusChartMetadata
    {
        public bool HasNotesData { get; set; }
        public Dictionary<string, string> InstrumentDifficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasVocals { get; set; }
        public bool HasLyrics { get; set; }
        public bool? HasVideoBackground { get; set; }
    }

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
        public ChorusChartMetadata ChartMetadata { get; set; } = new();
        public string ChartContentDisplay
        {
            get
            {
                if (!ChartMetadata.HasNotesData)
                    return string.Empty;

                var instrumentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guitar"] = "Search_InstrumentGuitar",
                    ["guitar_coop"] = "Search_InstrumentCoopGuitar",
                    ["rhythm"] = "Search_InstrumentRhythmGuitar",
                    ["bass"] = "Search_InstrumentBass",
                    ["drums"] = "Search_InstrumentDrums",
                    ["keys"] = "Search_InstrumentKeys",
                    ["guitarghl"] = "Search_InstrumentGhlGuitar",
                    ["guitar_coop_ghl"] = "Search_InstrumentGhlCoopGuitar",
                    ["rhythm_ghl"] = "Search_InstrumentGhlRhythmGuitar",
                    ["bassghl"] = "Search_InstrumentGhlBass"
                };
                string[] instrumentOrder =
                {
                    "guitar", "bass", "drums", "keys", "rhythm", "guitar_coop",
                    "guitarghl", "guitar_coop_ghl", "rhythm_ghl", "bassghl"
                };
                var parts = new System.Collections.Generic.List<string>();
                foreach (string instrument in instrumentOrder)
                {
                    if (!ChartMetadata.InstrumentDifficulties.TryGetValue(instrument, out string? difficulties))
                        continue;
                    string name = instrumentNames.TryGetValue(instrument, out string? key)
                        ? LocalizationService.T(key)
                        : instrument;
                    parts.Add(string.IsNullOrEmpty(difficulties) ? name : $"{name} {difficulties}");
                }

                if (ChartMetadata.HasVocals && ChartMetadata.HasLyrics)
                    parts.Add(LocalizationService.T("Search_VocalsLyrics"));
                else if (ChartMetadata.HasVocals)
                    parts.Add(LocalizationService.T("Search_VocalChart"));
                else if (ChartMetadata.HasLyrics)
                    parts.Add(LocalizationService.T("Search_Lyrics"));

                if (ChartMetadata.HasVideoBackground == true)
                    parts.Add(LocalizationService.T("Search_VideoBackground"));
                else if (ChartMetadata.HasVideoBackground == false)
                    parts.Add(LocalizationService.T("Search_NoVideo"));

                return parts.Count == 0
                    ? LocalizationService.T("Search_ChartDetailsUnavailable")
                    : LocalizationService.T("Search_ChartPrefix") + " " + string.Join(" · ", parts);
            }
        }
        public string ResyncChoiceLabel => $"{Artist} — {Title}  |  {Charter}  |  {Id}";

        [JsonIgnore]
        public Task? QualityProbeTask { get; set; }

        private AudioTrackLayout _audioTrackLayout = AudioTrackLayout.Unknown;
        public AudioTrackLayout TrackLayout
        {
            get => _audioTrackLayout;
            set
            {
                if (SetProperty(ref _audioTrackLayout, value))
                {
                    OnPropertyChanged(nameof(TrackLayoutDisplay));
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
                    OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
                }
            }
        }

        public string TrackLayoutDisplay => TrackLayout switch
        {
            AudioTrackLayout.Checking => LocalizationService.T("Search_CheckingChartQuality"),
            AudioTrackLayout.SingleTrack => LocalizationService.T("Search_SingleTrack"),
            AudioTrackLayout.HalfTrack => LocalizationService.T("Search_HalfTrack"),
            AudioTrackLayout.MultiTrack => LocalizationService.T("Search_MultiTrack"),
            _ => LocalizationService.T("Search_AudioLayoutUnavailable")
        };

        public int AudioStemCount { get; set; }
        public int SourceDifficultyCount { get; set; }
        private bool _hasChartQualityProfile;
        public bool HasChartQualityProfile
        {
            get => _hasChartQualityProfile;
            set
            {
                if (SetProperty(ref _hasChartQualityProfile, value))
                {
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
                    OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
                }
            }
        }
        private int _chartNoteCount;
        public int ChartNoteCount
        {
            get => _chartNoteCount;
            set
            {
                if (SetProperty(ref _chartNoteCount, value))
                {
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
                    OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
                }
            }
        }

        private double _chartCoverageRatio;
        public double ChartCoverageRatio
        {
            get => _chartCoverageRatio;
            set
            {
                if (SetProperty(ref _chartCoverageRatio, value))
                {
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
                    OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
                }
            }
        }

        public bool HasChartCoverageWarning => HasChartQualityProfile && !IsCheckingQuality
            && (ChartNoteCount <= 0 || ChartCoverageRatio < 0.50);
        public string ChartCoverageWarningDisplay => ChartNoteCount <= 0
            ? LocalizationService.T("Search_NoPlayableGuitarNotes")
            : LocalizationService.T("Search_IncompleteChartPercent", ChartCoverageRatio);

        public void RefreshLocalizedDisplay()
        {
            OnPropertyChanged(nameof(TrackLayoutDisplay));
            OnPropertyChanged(nameof(HasChartCoverageWarning));
            OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
            OnPropertyChanged(nameof(MetaDisplay));
            OnPropertyChanged(nameof(GuitarDifficultyDisplay));
            OnPropertyChanged(nameof(ChartContentDisplay));
        }

        private bool _isIdealForUnplugged;
        public bool IsIdealForUnplugged
        {
            get => _isIdealForUnplugged;
            set => SetProperty(ref _isIdealForUnplugged, value);
        }

        private bool _isCheckingQuality;
        public bool IsCheckingQuality
        {
            get => _isCheckingQuality;
            set
            {
                if (SetProperty(ref _isCheckingQuality, value))
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
            }
        }

        public string SubtitleDisplay => string.IsNullOrEmpty(Album)
            ? (string.IsNullOrEmpty(Year) ? Artist : $"{Artist} ({Year})")
            : $"{Artist} • {Album}{(string.IsNullOrEmpty(Year) ? "" : $" ({Year})")}";

        public string MetaDisplay => DiffGuitar >= 0
            ? LocalizationService.T("Search_MetaWithTier", Charter, Duration, DiffGuitar)
            : LocalizationService.T("Search_MetaWithoutTier", Charter, Duration);
        public string GuitarDifficultyDisplay => DiffGuitar >= 0
            ? LocalizationService.T("Search_GuitarTier", DiffGuitar)
            : LocalizationService.T("Search_Guitar");

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
        public double DurationSeconds { get; set; }
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
        private bool _isMultiTrack;
        public bool IsMultiTrack
        {
            get => _isMultiTrack;
            set
            {
                if (SetProperty(ref _isMultiTrack, value))
                    OnPropertyChanged(nameof(TrackTypeDisplay));
            }
        }

        private bool _isHalfTrack;
        public bool IsHalfTrack
        {
            get => _isHalfTrack;
            set
            {
                if (SetProperty(ref _isHalfTrack, value))
                    OnPropertyChanged(nameof(TrackTypeDisplay));
            }
        }

        private bool _isIdealForUnplugged;
        public bool IsIdealForUnplugged
        {
            get => _isIdealForUnplugged;
            set => SetProperty(ref _isIdealForUnplugged, value);
        }

        private bool _isCheckingCompatibility;
        public bool IsCheckingCompatibility
        {
            get => _isCheckingCompatibility;
            set => SetProperty(ref _isCheckingCompatibility, value);
        }

        private bool _hasCompatibilityProfile;
        public bool HasCompatibilityProfile
        {
            get => _hasCompatibilityProfile;
            set
            {
                if (SetProperty(ref _hasCompatibilityProfile, value))
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
            }
        }
        public int AudioStemCount { get; set; }
        public int SourceDifficultyCount { get; set; }
        private double _chartCoverageRatio;
        public double ChartCoverageRatio
        {
            get => _chartCoverageRatio;
            set
            {
                if (SetProperty(ref _chartCoverageRatio, value))
                {
                    OnPropertyChanged(nameof(HasChartCoverageWarning));
                    OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
                }
            }
        }
        public string? ChorusChartId { get; set; }
        public string? ChorusChartMd5 { get; set; }
        public int ChartParserVersion { get; set; }
        public bool NeedsChartResync => ChartParserVersion < USM.Core.Parsers.JsonGenerator.ParserVersion;
        public bool HasChartCoverageWarning => (HasCompatibilityProfile
            || ChartParserVersion >= USM.Core.Parsers.JsonGenerator.ParserVersion)
            && ChartCoverageRatio < 0.50;
        public string ChartCoverageWarningDisplay => ChartCoverageRatio > 0
            ? LocalizationService.T("Library_IncompleteChartPercent", ChartCoverageRatio)
            : LocalizationService.T("Library_NoPlayableNotes");
        public string TrackTypeDisplay => IsHalfTrack
            ? LocalizationService.T("Library_HalfTrack")
            : IsMultiTrack ? LocalizationService.T("Library_MultiTrack") : LocalizationService.T("Library_SingleTrack");

        public void RefreshLocalizedDisplay()
        {
            OnPropertyChanged(nameof(TrackTypeDisplay));
            OnPropertyChanged(nameof(HasChartCoverageWarning));
            OnPropertyChanged(nameof(ChartCoverageWarningDisplay));
        }

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
