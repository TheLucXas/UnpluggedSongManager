using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace USM.Core.Services
{
    public class GameDetector
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".unplugged_studio_config.json"
        );

        private static readonly string[] CommonPaths =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Unplugged",
            @"C:\SteamLibrary\steamapps\common\Unplugged",
            @"C:\Steam\steamapps\common\Unplugged",
            @"D:\SteamLibrary\steamapps\common\Unplugged",
            @"E:\SteamLibrary\steamapps\common\Unplugged",
            @"F:\SteamLibrary\steamapps\common\Unplugged",
            @"C:\SteamLibrary\steamapps\common\Unplugged"
        };

        private Dictionary<string, object> _config;

        public string? GamePath { get; private set; }

        private int _maxConcurrentDownloads = 5;
        public int MaxConcurrentDownloads
        {
            get => _maxConcurrentDownloads;
            set
            {
                int clamped = Math.Clamp(value, 1, 10);
                if (_maxConcurrentDownloads != clamped)
                {
                    _maxConcurrentDownloads = clamped;
                    SaveConfig("max_concurrent_downloads", _maxConcurrentDownloads);
                    MaxConcurrentDownloadsChanged?.Invoke(_maxConcurrentDownloads);
                }
            }
        }
        public event Action<int>? MaxConcurrentDownloadsChanged;

        public GameDetector()
        {
            _config = LoadConfig();
            if (_config.TryGetValue("game_path", out var gpObj) && gpObj is JsonElement elem && elem.ValueKind == JsonValueKind.String)
            {
                string? p = elem.GetString();
                if (!string.IsNullOrEmpty(p) && IsValidGamePath(p))
                {
                    GamePath = Path.GetFullPath(p);
                }
            }

            if (_config.TryGetValue("max_concurrent_downloads", out var mcdObj))
            {
                if (mcdObj is JsonElement mcdElem && mcdElem.TryGetInt32(out int mcd))
                {
                    _maxConcurrentDownloads = Math.Clamp(mcd, 1, 10);
                }
                else if (int.TryParse(mcdObj.ToString(), out int parsedMcd))
                {
                    _maxConcurrentDownloads = Math.Clamp(parsedMcd, 1, 10);
                }
            }

            if (string.IsNullOrEmpty(GamePath))
            {
                GamePath = AutoDetectGamePath();
                if (!string.IsNullOrEmpty(GamePath))
                {
                    SaveConfig("game_path", GamePath);
                }
            }
        }

        private Dictionary<string, object> LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    string json = File.ReadAllText(ConfigPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (dict != null) return dict;
                }
                catch { }
            }
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public void SaveConfig(string key, object value)
        {
            _config[key] = value;
            try
            {
                string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }

        public string? AutoDetectGamePath()
        {
            // 1. Check common drives
            foreach (var path in CommonPaths)
            {
                if (IsValidGamePath(path))
                    return Path.GetFullPath(path);
            }

            // 2. Steam Registry
            string? steamPath = GetSteamPathFromRegistry();
            if (!string.IsNullOrEmpty(steamPath))
            {
                string p = Path.Combine(steamPath, @"steamapps\common\Unplugged");
                if (IsValidGamePath(p)) return Path.GetFullPath(p);

                string vdf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var lib in ParseLibraryFolders(vdf))
                    {
                        string libPath = Path.Combine(lib, @"steamapps\common\Unplugged");
                        if (IsValidGamePath(libPath)) return Path.GetFullPath(libPath);
                    }
                }
            }

            return null;
        }

        public static bool IsValidGamePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return false;

            string exe = Path.Combine(path, "Unplugged.exe");
            string dataDir = Path.Combine(path, "Unplugged_Data");
            return File.Exists(exe) || Directory.Exists(dataDir);
        }

        public bool SetGamePath(string path)
        {
            if (IsValidGamePath(path))
            {
                GamePath = Path.GetFullPath(path);
                SaveConfig("game_path", GamePath);
                EnsureCustomSongsDir();
                return true;
            }
            return false;
        }

        public string? GetCustomSongsDir()
        {
            if (string.IsNullOrEmpty(GamePath)) return null;
            string d = Path.Combine(GamePath, "CustomSongs");
            Directory.CreateDirectory(d);
            return d;
        }

        public string? EnsureCustomSongsDir() => GetCustomSongsDir();

        public string? GetModsDir()
        {
            if (string.IsNullOrEmpty(GamePath)) return null;
            string d = Path.Combine(GamePath, "Mods");
            Directory.CreateDirectory(d);
            return d;
        }

        public bool IsMelonInstalled()
        {
            if (string.IsNullOrEmpty(GamePath)) return false;
            string melonDir = Path.Combine(GamePath, "MelonLoader");
            string versionDll = Path.Combine(GamePath, "version.dll");
            return Directory.Exists(melonDir) || File.Exists(versionDll);
        }

        public bool IsModInstalled()
        {
            if (string.IsNullOrEmpty(GamePath)) return false;
            string modDll = Path.Combine(GamePath, "Mods", "UnpluggedCustomSongs.dll");
            return File.Exists(modDll);
        }

        public bool InstallOrUpdateModPlugin(out string message)
        {
            if (string.IsNullOrEmpty(GamePath))
            {
                message = "Game location not set.";
                return false;
            }

            string modsDir = GetModsDir()!;
            string dest = Path.Combine(modsDir, "UnpluggedCustomSongs.dll");

            // Look for compiled mod dll next to app, in mod/ folder, or build outputs
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "UnpluggedCustomSongs.dll"),
                Path.Combine(appDir, @"..\..\..\..\mod\UnpluggedCustomSongs\UnpluggedCustomSongs.dll"),
                Path.Combine(appDir, @"..\..\..\..\mod\UnpluggedCustomSongs\bin\Release\netstandard2.0\UnpluggedCustomSongs.dll"),
                Path.Combine(appDir, @"..\..\..\UnpluggedCustomSongs.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), "UnpluggedCustomSongs.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), @"mod\UnpluggedCustomSongs\UnpluggedCustomSongs.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), @"mod\UnpluggedCustomSongs\bin\Release\netstandard2.0\UnpluggedCustomSongs.dll")
            };

            string? srcDll = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { srcDll = Path.GetFullPath(c); break; }
            }

            if (srcDll != null)
            {
                File.Copy(srcDll, dest, overwrite: true);
                message = "UnpluggedCustomSongs.dll successfully installed into Mods folder!";
                return true;
            }

            message = "UnpluggedCustomSongs.dll source file not found in application package.";
            return false;
        }

        public (bool DiscordRpc, bool VerboseDebug) ReadPreferences()
        {
            bool discord = true;
            bool verbose = false;

            if (string.IsNullOrEmpty(GamePath)) return (discord, verbose);
            string cfgPath = Path.Combine(GamePath, "UserData", "MelonPreferences.cfg");
            if (File.Exists(cfgPath))
            {
                try
                {
                    string content = File.ReadAllText(cfgPath);
                    var mDisc = Regex.Match(content, @"EnableDiscordRPC\s*=\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mDisc.Success) discord = bool.Parse(mDisc.Groups[1].Value);

                    var mVerb = Regex.Match(content, @"VerboseLogging\s*=\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mVerb.Success) verbose = bool.Parse(mVerb.Groups[1].Value);
                }
                catch { }
            }
            return (discord, verbose);
        }

        public void WritePreferences(bool discordRpc, bool verboseDebug)
        {
            if (string.IsNullOrEmpty(GamePath)) return;
            string userDir = Path.Combine(GamePath, "UserData");
            Directory.CreateDirectory(userDir);
            string cfgPath = Path.Combine(userDir, "MelonPreferences.cfg");

            string sectionHeader = "[UnpluggedCustomSongs]";
            string discordLine = $"EnableDiscordRPC = {discordRpc.ToString().ToLower()}";
            string verboseLine = $"VerboseLogging = {verboseDebug.ToString().ToLower()}";

            if (File.Exists(cfgPath))
            {
                string text = File.ReadAllText(cfgPath);
                if (text.Contains(sectionHeader))
                {
                    text = Regex.Replace(text, @"EnableDiscordRPC\s*=\s*(true|false)", discordLine, RegexOptions.IgnoreCase);
                    text = Regex.Replace(text, @"VerboseLogging\s*=\s*(true|false)", verboseLine, RegexOptions.IgnoreCase);
                    File.WriteAllText(cfgPath, text);
                    return;
                }
            }

            // Append if not found
            File.AppendAllText(cfgPath, $"\n\n{sectionHeader}\n{discordLine}\n{verboseLine}\n");
        }

        private static string? GetSteamPathFromRegistry()
        {
            string[] subkeys = { @"SOFTWARE\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam" };
            RegistryKey[] roots = { Registry.LocalMachine, Registry.CurrentUser };

            foreach (var root in roots)
            {
                foreach (var sk in subkeys)
                {
                    try
                    {
                        using var key = root.OpenSubKey(sk);
                        if (key != null)
                        {
                            var val = key.GetValue("InstallPath") as string;
                            if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                                return val;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static List<string> ParseLibraryFolders(string vdfPath)
        {
            var paths = new List<string>();
            try
            {
                foreach (var raw in File.ReadLines(vdfPath))
                {
                    string line = raw.Trim();
                    if (line.ToLowerInvariant().Contains("\"path\""))
                    {
                        var parts = line.Split('"');
                        if (parts.Length >= 4)
                        {
                            string p = parts[3].Replace(@"\\", @"\");
                            if (Directory.Exists(p)) paths.Add(p);
                        }
                    }
                }
            }
            catch { }
            return paths;
        }
    }
}
