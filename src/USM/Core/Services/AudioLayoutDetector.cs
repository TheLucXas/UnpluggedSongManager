using System;
using System.Collections.Generic;
using System.IO;

namespace USM.Core.Services
{
    public enum AudioTrackLayout
    {
        Unknown,
        Checking,
        SingleTrack,
        HalfTrack,
        MultiTrack
    }

    /// <summary>
    /// Infers the playable layout from audio entries in a chart package. Chorus
    /// instrument badges describe note charts, not separated audio stems.
    /// </summary>
    public static class AudioLayoutDetector
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".opus", ".ogg", ".mp3", ".wav", ".flac", ".m4a", ".aac"
        };

        public static AudioTrackLayout Detect(IEnumerable<string> fileNames)
        {
            return Analyze(fileNames).Layout;
        }

        public static (AudioTrackLayout Layout, int StemCount, bool HasVocals, HashSet<string> StemGroups) Analyze(IEnumerable<string> fileNames)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasVocals = false;
            bool hasNamedBacking = false;
            bool hasSongMix = false;

            foreach (string fileName in fileNames)
            {
                string safeName = Path.GetFileName(fileName);
                if (!AudioExtensions.Contains(Path.GetExtension(safeName)))
                    continue;

                string stem = Path.GetFileNameWithoutExtension(safeName).Trim().ToLowerInvariant();
                if (stem.Length == 0 || stem.StartsWith("sample", StringComparison.Ordinal) || stem.StartsWith("preview", StringComparison.Ordinal))
                    continue;

                string group = GetTrackGroup(stem);
                if (group == "vocals")
                    hasVocals = true;
                else if (group == "song")
                    hasSongMix = true;
                else
                    hasNamedBacking = true;

                groups.Add(group);
            }

            if (groups.Count == 0)
                return (AudioTrackLayout.Unknown, 0, hasVocals, groups);

            // song.opus is commonly the full preview/master mix accompanying
            // individual stems. Do not count it as another stem in that case.
            if (hasSongMix && hasNamedBacking)
                groups.Remove("song");

            // When a chart contains only a vocal stem and song.opus, the latter
            // is the available instrumental/backing track for the two-track layout.
            if (hasSongMix && hasVocals && !hasNamedBacking)
                groups.Add("song");

            if (groups.Count <= 1)
                return (AudioTrackLayout.SingleTrack, groups.Count, hasVocals, groups);

            if (groups.Count == 2 && hasVocals)
                return (AudioTrackLayout.HalfTrack, groups.Count, hasVocals, groups);

            return (AudioTrackLayout.MultiTrack, groups.Count, hasVocals, groups);
        }

        private static string GetTrackGroup(string stem)
        {
            // USM output names its independent channels "song-guitar.ogg",
            // "song-vocals.ogg", etc. The "song-" prefix is a file namespace,
            // not part of the channel name. Treating it as a master mix made
            // every packaged stem collapse into one group, so the game showed
            // Single Track and never marked complete multi-stem songs as Ideal.
            foreach (string separator in new[] { "-", "_", " " })
            {
                string prefix = "song" + separator;
                if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string channel = stem.Substring(prefix.Length);
                if (IsKnownChannelName(channel))
                {
                    stem = channel;
                    break;
                }
            }

            if (StartsWithStem(stem, "vocal", "vocals")) return "vocals";
            if (StartsWithStem(stem, "guitar", "lead")) return "guitar";
            if (StartsWithStem(stem, "bass")) return "bass";
            if (StartsWithStem(stem, "rhythm")) return "bass";
            if (StartsWithStem(stem, "drum", "drums")) return "drums";
            if (StartsWithStem(stem, "key", "keys", "keyboard")) return "keys";
            if (StartsWithStem(stem, "band")) return "band";
            if (StartsWithStem(stem, "crowd")) return "crowd";
            if (StartsWithStem(stem, "other")) return "other";
            if (StartsWithStem(stem, "instrumental", "backing")) return "other";
            if (StartsWithStem(stem, "song")) return "song";
            return stem;
        }

        private static bool IsKnownChannelName(string value)
        {
            return StartsWithStem(value,
                "vocal", "vocals", "guitar", "lead", "bass", "rhythm",
                "drum", "drums", "key", "keys", "keyboard", "band",
                "crowd", "other", "instrumental", "backing");
        }

        private static bool StartsWithStem(string value, params string[] stems)
        {
            foreach (string stem in stems)
            {
                if (value.Equals(stem, StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith(stem + " ", StringComparison.OrdinalIgnoreCase)
                    || (value.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                        && value.Length > stem.Length
                        && char.IsDigit(value[stem.Length]))
                    || (stem.EndsWith("s", StringComparison.Ordinal) && value.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }
    }
}
