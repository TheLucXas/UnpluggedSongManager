import os
import json
import winreg
from pathlib import Path

CONFIG_FILE = os.path.join(os.path.expanduser("~"), ".unplugged_studio_config.json")

COMMON_PATHS = [
    r"C:\Program Files (x86)\Steam\steamapps\common\Unplugged",
    r"C:\SteamLibrary\steamapps\common\Unplugged",
    r"C:\Steam\steamapps\common\Unplugged",
    r"D:\SteamLibrary\steamapps\common\Unplugged",
    r"E:\SteamLibrary\steamapps\common\Unplugged",
    r"F:\SteamLibrary\steamapps\common\Unplugged",
    r"C:\SteamLibrary\steamapps\common\Unplugged",
]

class GameDetector:
    def __init__(self):
        self.config = self.load_config()
        self.game_path = self.config.get("game_path") or self.auto_detect_game_path()
        if self.game_path:
            self.save_config({"game_path": self.game_path})

    def load_config(self):
        if os.path.exists(CONFIG_FILE):
            try:
                with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                    return json.load(f)
            except Exception:
                pass
        return {}

    def save_config(self, data):
        self.config.update(data)
        try:
            with open(CONFIG_FILE, "w", encoding="utf-8") as f:
                json.dump(self.config, f, indent=2)
        except Exception as e:
            print(f"[Config] Error saving config: {e}")

    def auto_detect_game_path(self):
        # 1. Check known common paths first
        for path in COMMON_PATHS:
            if self.is_valid_game_path(path):
                return os.path.abspath(path)

        # 2. Try Steam Registry
        steam_path = self.get_steam_path_from_registry()
        if steam_path:
            # Check default steamapps
            p = os.path.join(steam_path, "steamapps", "common", "Unplugged")
            if self.is_valid_game_path(p):
                return os.path.abspath(p)

            # Check libraryfolders.vdf
            vdf_path = os.path.join(steam_path, "steamapps", "libraryfolders.vdf")
            if os.path.exists(vdf_path):
                libs = self.parse_library_folders(vdf_path)
                for lib in libs:
                    p = os.path.join(lib, "steamapps", "common", "Unplugged")
                    if self.is_valid_game_path(p):
                        return os.path.abspath(p)

        return None

    def get_steam_path_from_registry(self):
        for subkey in [r"SOFTWARE\Valve\Steam", r"SOFTWARE\WOW6432Node\Valve\Steam"]:
            for root in [winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER]:
                try:
                    with winreg.OpenKey(root, subkey) as key:
                        val, _ = winreg.QueryValueEx(key, "InstallPath")
                        if val and os.path.isdir(val):
                            return val
                except OSError:
                    pass
        return None

    def parse_library_folders(self, vdf_path):
        paths = []
        try:
            with open(vdf_path, "r", encoding="utf-8", errors="ignore") as f:
                for line in f:
                    line = line.strip()
                    if '"path"' in line.lower():
                        parts = line.split('"')
                        if len(parts) >= 4:
                            p = parts[3].replace(r"\\", "\\")
                            if os.path.isdir(p):
                                paths.append(p)
        except Exception:
            pass
        return paths

    def is_valid_game_path(self, path):
        if not path or not os.path.isdir(path):
            return False
        exe = os.path.join(path, "Unplugged.exe")
        data_dir = os.path.join(path, "Unplugged_Data")
        return os.path.isfile(exe) or os.path.isdir(data_dir)

    def set_game_path(self, path):
        if self.is_valid_game_path(path):
            self.game_path = os.path.abspath(path)
            self.save_config({"game_path": self.game_path})
            self.ensure_custom_songs_dir()
            return True
        return False

    def get_custom_songs_dir(self):
        if self.game_path:
            custom_dir = os.path.join(self.game_path, "CustomSongs")
            os.makedirs(custom_dir, exist_ok=True)
            return custom_dir
        return None

    def ensure_custom_songs_dir(self):
        d = self.get_custom_songs_dir()
        if d and not os.path.exists(d):
            os.makedirs(d, exist_ok=True)
        return d

    def get_mods_dir(self):
        if self.game_path:
            mods_dir = os.path.join(self.game_path, "Mods")
            os.makedirs(mods_dir, exist_ok=True)
            return mods_dir
        return None

    def is_mod_installed(self):
        if not self.game_path:
            return False
        mod_dll = os.path.join(self.game_path, "Mods", "UnpluggedCustomSongs.dll")
        return os.path.isfile(mod_dll)

    def is_melon_installed(self):
        if not self.game_path:
            return False
        melon_dir = os.path.join(self.game_path, "MelonLoader")
        version_dll = os.path.join(self.game_path, "version.dll")
        return os.path.isdir(melon_dir) or os.path.isfile(version_dll)
