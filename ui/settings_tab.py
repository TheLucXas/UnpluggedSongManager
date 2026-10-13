import customtkinter as ctk
from tkinter import filedialog
import os
import shutil

from ui.theme import THEME, FONT_FAMILY

class SettingsTab(ctk.CTkFrame):
    def __init__(self, master, game_detector, notify_cb=None):
        super().__init__(master, fg_color="transparent")
        self.game_detector = game_detector
        self.notify_cb = notify_cb

        self.setup_ui()

    def setup_ui(self):
        # 1. Game Path Card
        path_card = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=12, border_width=1, border_color=THEME["border_color"])
        path_card.pack(fill="x", padx=20, pady=(15, 10))

        ctk.CTkLabel(
            path_card,
            text="Unplugged VR Game Location",
            font=(FONT_FAMILY, 15, "bold"),
            text_color=THEME["text_primary"],
            anchor="w"
        ).pack(fill="x", padx=15, pady=(12, 4))

        ctk.CTkLabel(
            path_card,
            text="Root folder where Unplugged.exe is installed on your PC:",
            font=(FONT_FAMILY, 12),
            text_color=THEME["text_secondary"],
            anchor="w"
        ).pack(fill="x", padx=15, pady=(0, 10))

        row = ctk.CTkFrame(path_card, fg_color="transparent")
        row.pack(fill="x", padx=15, pady=(0, 15))

        self.path_entry = ctk.CTkEntry(
            row,
            font=(FONT_FAMILY, 13),
            height=38,
            fg_color=THEME["bg_input"],
            border_width=0,
            text_color=THEME["text_primary"]
        )
        self.path_entry.pack(side="left", fill="x", expand=True, padx=(0, 10))
        if self.game_detector.game_path:
            self.path_entry.insert(0, self.game_detector.game_path)

        change_btn = ctk.CTkButton(
            row,
            text="Browse Folder",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            height=38,
            command=self.browse_game_dir
        )
        change_btn.pack(side="left")

        # 2. Mod & Game Status Card
        status_card = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=12, border_width=1, border_color=THEME["border_color"])
        status_card.pack(fill="x", padx=20, pady=10)

        status_header = ctk.CTkFrame(status_card, fg_color="transparent")
        status_header.pack(fill="x", padx=15, pady=(12, 8))

        ctk.CTkLabel(
            status_header,
            text="Modding & Installation Status",
            font=(FONT_FAMILY, 15, "bold"),
            text_color=THEME["text_primary"],
            anchor="w"
        ).pack(side="left")

        self.install_mod_btn = ctk.CTkButton(
            status_header,
            text="⚡ Install / Update Mod Plugin",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["accent_green"],
            hover_color="#00c853",
            text_color="#000000",
            height=32,
            command=self.install_or_update_mod
        )
        self.install_mod_btn.pack(side="right")

        self.game_status_lbl = self.create_status_row(status_card, "Unplugged.exe Executable:")
        self.melon_status_lbl = self.create_status_row(status_card, "MelonLoader (Mod Loader):")
        self.mod_status_lbl = self.create_status_row(status_card, "UnpluggedCustomSongs.dll:")
        self.songs_status_lbl = self.create_status_row(status_card, "CustomSongs Library Folder:")

        # 3. Preferences & Debug Options Card
        pref_card = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=12, border_width=1, border_color=THEME["border_color"])
        pref_card.pack(fill="x", padx=20, pady=10)

        ctk.CTkLabel(
            pref_card,
            text="In-Game VR Mod Settings (MelonPreferences.cfg)",
            font=(FONT_FAMILY, 15, "bold"),
            text_color=THEME["text_primary"],
            anchor="w"
        ).pack(fill="x", padx=15, pady=(12, 4))

        ctk.CTkLabel(
            pref_card,
            text="These options are executed exclusively by the in-game plugin (UnpluggedCustomSongs.dll) when playing in VR. Song Studio does NOT run Discord RPC.",
            font=(FONT_FAMILY, 11),
            text_color=THEME["text_secondary"],
            wraplength=700,
            justify="left",
            anchor="w"
        ).pack(fill="x", padx=15, pady=(0, 8))

        # Discord Rich Presence switch
        self.discord_switch = ctk.CTkSwitch(
            pref_card,
            text="Enable In-Game Discord Rich Presence (Shows custom song & difficulty on Discord while playing)",
            font=(FONT_FAMILY, 13),
            progress_color=THEME["accent_pink"],
            command=self.save_preferences
        )
        self.discord_switch.pack(anchor="w", padx=15, pady=6)
        if self.game_detector.config.get("discord_rpc", True):
            self.discord_switch.select()
        else:
            self.discord_switch.deselect()

        # Verbose Debug Logging switch
        self.debug_switch = ctk.CTkSwitch(
            pref_card,
            text="Verbose / Debug In-Game Logging (Log detailed chart & audio loading in MelonLoader console)",
            font=(FONT_FAMILY, 13),
            progress_color=THEME["accent_purple"],
            command=self.save_preferences
        )
        self.debug_switch.pack(anchor="w", padx=15, pady=(6, 15))
        if self.game_detector.config.get("verbose_debug", False):
            self.debug_switch.select()
        else:
            self.debug_switch.deselect()

        self.update_status()

    def create_status_row(self, parent, label_text):
        row = ctk.CTkFrame(parent, fg_color="transparent")
        row.pack(fill="x", padx=15, pady=4)

        ctk.CTkLabel(row, text=label_text, font=(FONT_FAMILY, 13), text_color=THEME["text_primary"], width=260, anchor="w").pack(side="left")
        status_lbl = ctk.CTkLabel(row, text="", font=(FONT_FAMILY, 13, "bold"), anchor="w")
        status_lbl.pack(side="left")
        return status_lbl

    def update_status(self):
        if self.game_detector.game_path and os.path.exists(os.path.join(self.game_detector.game_path, "Unplugged.exe")):
            self.game_status_lbl.configure(text="✓ Detected", text_color=THEME["accent_green"])
        else:
            self.game_status_lbl.configure(text="✗ Not found", text_color="red")

        if self.game_detector.is_melon_installed():
            self.melon_status_lbl.configure(text="✓ Installed", text_color=THEME["accent_green"])
        else:
            self.melon_status_lbl.configure(text="✗ Not found (Install MelonLoader first)", text_color="orange")

        if self.game_detector.is_mod_installed():
            self.mod_status_lbl.configure(text="✓ Active and Loaded", text_color=THEME["accent_green"])
        else:
            self.mod_status_lbl.configure(text="✗ Not in Mods folder", text_color="orange")

        cs_dir = self.game_detector.get_custom_songs_dir()
        if cs_dir and os.path.exists(cs_dir):
            self.songs_status_lbl.configure(text=f"✓ Ready ({cs_dir})", text_color=THEME["accent_green"])
        else:
            self.songs_status_lbl.configure(text="✗ Not created", text_color="orange")

    def browse_game_dir(self):
        d = filedialog.askdirectory(title="Select Unplugged VR game directory")
        if d:
            if self.game_detector.set_game_path(d):
                self.path_entry.delete(0, "end")
                self.path_entry.insert(0, d)
                self.update_status()
                if self.notify_cb:
                    self.notify_cb("Unplugged VR path updated successfully.")
            else:
                if self.notify_cb:
                    self.notify_cb("Selected directory does not appear to contain Unplugged.exe.")

    def save_preferences(self):
        discord_val = bool(self.discord_switch.get())
        debug_val = bool(self.debug_switch.get())
        self.game_detector.save_config({
            "discord_rpc": discord_val,
            "verbose_debug": debug_val
        })
        
        # Also sync directly to MelonPreferences.cfg for the Unplugged mod
        try:
            if self.game_detector.game_path:
                user_data_dir = os.path.join(self.game_detector.game_path, "UserData")
                if not os.path.isdir(user_data_dir):
                    os.makedirs(user_data_dir, exist_ok=True)
                cfg_path = os.path.join(user_data_dir, "MelonPreferences.cfg")
                lines = []
                if os.path.exists(cfg_path):
                    with open(cfg_path, "r", encoding="utf-8", errors="ignore") as f:
                        lines = f.readlines()
                new_lines = []
                skip = False
                for line in lines:
                    if line.strip().startswith("[UnpluggedCustomSongs]"):
                        skip = True
                        continue
                    if skip and line.strip().startswith("["):
                        skip = False
                    if not skip:
                        new_lines.append(line)
                new_section = [
                    "[UnpluggedCustomSongs]\n",
                    f"EnableDiscordRPC = {'true' if discord_val else 'false'}\n",
                    f"VerboseLogging = {'true' if debug_val else 'false'}\n",
                    'DiscordClientId = "1441987286022619299"\n\n'
                ]
                with open(cfg_path, "w", encoding="utf-8") as f:
                    f.writelines(new_lines + new_section)
        except Exception:
            pass

        if self.notify_cb:
            self.notify_cb("Settings & MelonLoader preferences saved.")

    def install_or_update_mod(self):
        mods_dir = self.game_detector.get_mods_dir()
        if not mods_dir:
            if self.notify_cb:
                self.notify_cb("Please set your Unplugged folder first.")
            return

        # Look for source dll in app folder, PyInstaller bundle, or scratch build
        exe_dir = os.path.dirname(sys.executable)
        meipass = getattr(sys, '_MEIPASS', '')
        dll_candidates = [
            os.path.join(meipass, "UnpluggedCustomSongs.dll"),
            os.path.join(meipass, "_internal", "UnpluggedCustomSongs.dll"),
            os.path.join(exe_dir, "UnpluggedCustomSongs.dll"),
            os.path.join(exe_dir, "_internal", "UnpluggedCustomSongs.dll"),
            os.path.join(os.path.dirname(__file__), "..", "UnpluggedCustomSongs.dll")
        ]

        found = next((p for p in dll_candidates if os.path.isfile(p)), None)
        if found:
            dest = os.path.join(mods_dir, "UnpluggedCustomSongs.dll")
            try:
                shutil.copy2(found, dest)
                self.update_status()
                if self.notify_cb:
                    self.notify_cb("UnpluggedCustomSongs.dll installed/updated successfully!")
            except Exception as e:
                if self.notify_cb:
                    self.notify_cb(f"Error copying mod DLL: {e}")
        else:
            if self.notify_cb:
                self.notify_cb("Source UnpluggedCustomSongs.dll not found.")
