import customtkinter as ctk
import os
import shutil
import re
import threading

from core.audio_player import player_instance
from ui.theme import THEME, FONT_FAMILY

def clean_text(s):
    if not s: return ""
    return re.sub(r'<[^>]*>', '', str(s)).strip()

class LibraryTab(ctk.CTkFrame):
    def __init__(self, master, game_detector, notify_cb=None):
        super().__init__(master, fg_color="transparent")
        self.game_detector = game_detector
        self.notify_cb = notify_cb
        self.currently_playing_path = None
        self.current_play_btn = None
        
        # High performance cache: folder_path -> {card, var, song, ...}
        self.song_cards = {}
        self.selected_folders = set()
        self.is_busy = False

        self.setup_ui()

    def setup_ui(self):
        # 1. Top Bar: Header & Actions
        header = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=12, border_width=1, border_color=THEME["border_color"])
        header.pack(fill="x", padx=20, pady=(15, 8))

        top_row = ctk.CTkFrame(header, fg_color="transparent")
        top_row.pack(fill="x", padx=15, pady=(10, 6))

        self.title_lbl = ctk.CTkLabel(
            top_row,
            text="Installed Custom Songs",
            font=(FONT_FAMILY, 16, "bold"),
            text_color=THEME["text_primary"]
        )
        self.title_lbl.pack(side="left", padx=(0, 10))

        self.count_badge = ctk.CTkLabel(
            top_row,
            text="0 songs",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["bg_input"],
            corner_radius=6,
            text_color=THEME["accent_blue"],
            padx=10,
            pady=4
        )
        self.count_badge.pack(side="left")

        # Open Folder and Refresh buttons on top right
        open_folder_btn = ctk.CTkButton(
            top_row,
            text="📁 Open CustomSongs",
            font=(FONT_FAMILY, 12),
            fg_color="#374151",
            hover_color="#4b5563",
            height=32,
            corner_radius=6,
            command=self.open_custom_songs_folder
        )
        open_folder_btn.pack(side="right", padx=(6, 0))

        self.refresh_btn = ctk.CTkButton(
            top_row,
            text="🔄 Refresh",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            height=32,
            width=85,
            corner_radius=6,
            command=self.refresh_library
        )
        self.refresh_btn.pack(side="right")

        # 2. Controls Row: Search Filter & Batch Operations
        controls_row = ctk.CTkFrame(header, fg_color="transparent")
        controls_row.pack(fill="x", padx=15, pady=(0, 10))

        # Search box
        self.search_var = ctk.StringVar()
        self.search_var.trace_add("write", lambda *args: self.apply_filter())
        self.search_entry = ctk.CTkEntry(
            controls_row,
            placeholder_text="🔍 Filter songs by title, artist or charter...",
            textvariable=self.search_var,
            font=(FONT_FAMILY, 12),
            height=34,
            corner_radius=8,
            border_color=THEME["border_color"],
            fg_color=THEME["bg_input"]
        )
        self.search_entry.pack(side="left", fill="x", expand=True, padx=(0, 12))

        # Select All / None toggle button
        self.select_all_btn = ctk.CTkButton(
            controls_row,
            text="Select All",
            font=(FONT_FAMILY, 12),
            fg_color="#2d3748",
            hover_color="#4a5568",
            height=34,
            width=100,
            corner_radius=6,
            command=self.toggle_select_all
        )
        self.select_all_btn.pack(side="left", padx=4)

        # Batch Delete button
        self.batch_delete_btn = ctk.CTkButton(
            controls_row,
            text="🗑 Delete Selected",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color="#451a1a",
            hover_color="#dc2626",
            text_color="#fca5a5",
            height=34,
            corner_radius=6,
            state="disabled",
            command=self.delete_selected_prompt
        )
        self.batch_delete_btn.pack(side="left", padx=(4, 0))

        # 3. Scrollable List of Songs
        self.scroll_list = ctk.CTkScrollableFrame(self, fg_color="transparent", corner_radius=0)
        self.scroll_list.pack(fill="both", expand=True, padx=15, pady=(5, 10))

        # Placeholder message label (reused, never recreated)
        self.empty_lbl = ctk.CTkLabel(
            self.scroll_list,
            text="",
            font=(FONT_FAMILY, 14),
            text_color=THEME["text_muted"]
        )

        self.refresh_library()

    def open_custom_songs_folder(self):
        d = self.game_detector.get_custom_songs_dir()
        if d and os.path.exists(d):
            os.startfile(d)

    def refresh_library(self):
        if self.is_busy:
            return

        self.is_busy = True
        self.refresh_btn.configure(state="disabled", text="Scanning...")

        threading.Thread(target=self._scan_songs_worker, daemon=True).start()

    def _scan_songs_worker(self):
        custom_dir = self.game_detector.get_custom_songs_dir()
        scanned = []

        if custom_dir and os.path.exists(custom_dir):
            try:
                entries = sorted(os.listdir(custom_dir), key=lambda x: x.lower())
                for entry in entries:
                    song_path = os.path.join(custom_dir, entry)
                    if os.path.isdir(song_path):
                        meta = self.read_song_meta(song_path, entry)
                        scanned.append(meta)
            except Exception as e:
                print(f"[Library] Scan error: {e}")

        self.after(0, lambda: self._on_scan_complete(scanned))

    def _on_scan_complete(self, songs):
        new_paths = {s["folder"]: s for s in songs}

        # Remove cards that no longer exist
        for folder in list(self.song_cards.keys()):
            if folder not in new_paths:
                self.song_cards[folder]["card"].destroy()
                del self.song_cards[folder]
                self.selected_folders.discard(folder)

        # Create cards only for new songs
        for s in songs:
            folder = s["folder"]
            if folder not in self.song_cards:
                self._create_card_for_song(s)

        self.is_busy = False
        self.refresh_btn.configure(state="normal", text="🔄 Refresh")
        self.apply_filter()

    def read_song_meta(self, folder_path, folder_name):
        info = {
            "folder": folder_path,
            "folder_name": folder_name,
            "title": folder_name,
            "artist": "Unknown Artist",
            "charter": "Custom Charter",
            "has_multitrack": os.path.isfile(os.path.join(folder_path, "song-guitar.mp3")),
            "cover_path": None,
            "sample_path": os.path.join(folder_path, "sample.mp3") if os.path.isfile(os.path.join(folder_path, "sample.mp3")) else os.path.join(folder_path, "song.mp3")
        }

        # Check cover
        for cn in ["cover.png", "album.jpg", "album.png"]:
            cp = os.path.join(folder_path, cn)
            if os.path.isfile(cp):
                info["cover_path"] = cp
                break

        # Read ini
        ini_path = os.path.join(folder_path, "song.ini")
        if os.path.isfile(ini_path):
            try:
                with open(ini_path, "r", encoding="utf-8", errors="ignore") as f:
                    for line in f:
                        if "=" in line:
                            k, v = line.split("=", 1)
                            k = k.strip().lower()
                            v = clean_text(v.strip().strip('"'))
                            if k == "name" and v: info["title"] = v
                            elif k == "artist" and v: info["artist"] = v
                            elif k == "charter" and v: info["charter"] = v
            except Exception:
                pass

        return info

    def _create_card_for_song(self, song):
        folder = song["folder"]
        is_selected = folder in self.selected_folders

        card = ctk.CTkFrame(
            self.scroll_list,
            fg_color=THEME["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color="#3b82f6" if is_selected else THEME["border_color"]
        )

        chk_val = ctk.BooleanVar(value=is_selected)

        def on_check():
            if chk_val.get():
                self.selected_folders.add(folder)
                card.configure(border_color="#3b82f6")
            else:
                self.selected_folders.discard(folder)
                card.configure(border_color=THEME["border_color"])
            self._update_selection_ui()

        chk = ctk.CTkCheckBox(
            card,
            text="",
            variable=chk_val,
            width=24,
            height=24,
            corner_radius=4,
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            command=on_check
        )
        chk.pack(side="left", padx=(12, 4), pady=10)

        # Info column
        info_col = ctk.CTkFrame(card, fg_color="transparent")
        info_col.pack(side="left", fill="both", expand=True, padx=(4, 15), pady=10)

        title_lbl = ctk.CTkLabel(
            info_col,
            text=song["title"],
            font=(FONT_FAMILY, 15, "bold"),
            text_color=THEME["text_primary"],
            anchor="w"
        )
        title_lbl.pack(fill="x")

        track_type = "🎸 MultiTrack Stems" if song["has_multitrack"] else "🎵 Full Master"
        sub_text = f"{song['artist']}  •  Charter: {song['charter']}  •  {track_type}"
        sub_lbl = ctk.CTkLabel(
            info_col,
            text=sub_text,
            font=(FONT_FAMILY, 12),
            text_color=THEME["accent_blue"],
            anchor="w"
        )
        sub_lbl.pack(fill="x", pady=(2, 0))

        # Actions
        actions_col = ctk.CTkFrame(card, fg_color="transparent")
        actions_col.pack(side="right", padx=12, pady=10)

        play_btn = ctk.CTkButton(
            actions_col,
            text="▶ Preview",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            height=32,
            width=88,
            corner_radius=6
        )
        play_btn.configure(command=lambda p=song["sample_path"], b=play_btn: self.toggle_play(p, b))
        play_btn.pack(side="left", padx=4)

        open_btn = ctk.CTkButton(
            actions_col,
            text="📂",
            font=(FONT_FAMILY, 13),
            fg_color="#374151",
            hover_color="#4b5563",
            height=32,
            width=36,
            corner_radius=6,
            command=lambda f=song["folder"]: os.startfile(f)
        )
        open_btn.pack(side="left", padx=4)

        del_btn = ctk.CTkButton(
            actions_col,
            text="🗑",
            font=(FONT_FAMILY, 13),
            fg_color="#451a1a",
            hover_color="#dc2626",
            text_color="#fca5a5",
            height=32,
            width=36,
            corner_radius=6,
            command=lambda f=song["folder"], t=song["title"]: self.delete_single_song_async(f, t)
        )
        del_btn.pack(side="left", padx=4)

        self.song_cards[folder] = {
            "card": card,
            "var": chk_val,
            "song": song,
            "title_l": song["title"].lower(),
            "artist_l": song["artist"].lower(),
            "charter_l": song["charter"].lower(),
            "folder_l": song["folder_name"].lower()
        }

    def apply_filter(self):
        query = self.search_var.get().strip().lower()

        if not self.song_cards:
            self.empty_lbl.configure(text="No custom songs installed yet.\nDrop charts in 'Local Converter' or download from 'Chorus'!")
            self.empty_lbl.pack(pady=40)
            self.count_badge.configure(text="0 songs")
            self._update_selection_ui()
            return

        visible_count = 0
        for item in self.song_cards.values():
            matches = not query or (
                query in item["title_l"] or
                query in item["artist_l"] or
                query in item["charter_l"] or
                query in item["folder_l"]
            )
            item["is_visible"] = matches
            if matches:
                item["card"].pack(fill="x", pady=4, padx=5)
                visible_count += 1
            else:
                item["card"].pack_forget()

        if visible_count == 0:
            self.empty_lbl.configure(text=f"No songs matched '{self.search_var.get()}'.")
            self.empty_lbl.pack(pady=30)
        else:
            self.empty_lbl.pack_forget()

        self._update_selection_ui()

    def _update_selection_ui(self):
        total = len(self.song_cards)
        sel_count = len(self.selected_folders)

        if sel_count > 0:
            self.count_badge.configure(text=f"{total} songs ({sel_count} selected)")
            self.batch_delete_btn.configure(
                state="normal",
                text=f"🗑 Delete Selected ({sel_count})",
                fg_color="#dc2626",
                text_color="#ffffff"
            )
            self.select_all_btn.configure(text="Deselect All")
        else:
            self.count_badge.configure(text=f"{total} songs")
            self.batch_delete_btn.configure(
                state="disabled",
                text="🗑 Delete Selected",
                fg_color="#451a1a",
                text_color="#fca5a5"
            )
            self.select_all_btn.configure(text="Select All")

    def toggle_select_all(self):
        # Instant toggle: no destruction or recreation of widgets!
        if len(self.selected_folders) > 0:
            self.selected_folders.clear()
            for item in self.song_cards.values():
                item["var"].set(False)
                item["card"].configure(border_color=THEME["border_color"])
        else:
            for folder, item in self.song_cards.items():
                if item.get("is_visible", True):
                    self.selected_folders.add(folder)
                    item["var"].set(True)
                    item["card"].configure(border_color="#3b82f6")

        self._update_selection_ui()

    def toggle_play(self, audio_path, button):
        if self.currently_playing_path == audio_path and player_instance.is_playing():
            player_instance.stop()
            self.currently_playing_path = None
            button.configure(text="▶ Preview", fg_color=THEME["accent_pink"])
        else:
            if self.current_play_btn:
                try:
                    self.current_play_btn.configure(text="▶ Preview", fg_color=THEME["accent_pink"])
                except Exception:
                    pass
            player_instance.play(audio_path)
            self.currently_playing_path = audio_path
            self.current_play_btn = button
            button.configure(text="⏹ Stop", fg_color=THEME["accent_green"])

    def delete_single_song_async(self, folder_path, title):
        if self.is_busy:
            return

        if player_instance.is_playing():
            player_instance.stop()
            self.currently_playing_path = None

        self.is_busy = True
        self.count_badge.configure(text=f"Deleting '{title}'...")

        def worker():
            try:
                if os.path.exists(folder_path):
                    shutil.rmtree(folder_path)
            except Exception as e:
                print(f"[Library] Error deleting song '{title}': {e}")
            
            self.after(0, lambda: self._on_single_deleted(folder_path, f"Deleted '{title}' from CustomSongs."))

        threading.Thread(target=worker, daemon=True).start()

    def _on_single_deleted(self, folder_path, msg):
        self.is_busy = False
        if folder_path in self.song_cards:
            self.song_cards[folder_path]["card"].destroy()
            del self.song_cards[folder_path]
        self.selected_folders.discard(folder_path)
        self.apply_filter()
        if self.notify_cb:
            self.notify_cb(msg)

    def delete_selected_prompt(self):
        if not self.selected_folders or self.is_busy:
            return

        count = len(self.selected_folders)
        
        if player_instance.is_playing():
            player_instance.stop()
            self.currently_playing_path = None

        self.is_busy = True
        self.batch_delete_btn.configure(state="disabled", text="Deleting...")
        self.count_badge.configure(text=f"Deleting {count} songs...")

        folders_to_delete = list(self.selected_folders)

        def worker():
            deleted_count = 0
            for f in folders_to_delete:
                try:
                    if os.path.exists(f):
                        shutil.rmtree(f)
                        deleted_count += 1
                except Exception as e:
                    print(f"[Library] Error deleting {f}: {e}")

            self.after(0, lambda: self._on_batch_deleted(folders_to_delete, f"Deleted {deleted_count} song(s) from CustomSongs."))

        threading.Thread(target=worker, daemon=True).start()

    def _on_batch_deleted(self, deleted_folders, msg):
        self.is_busy = False
        for f in deleted_folders:
            if f in self.song_cards:
                self.song_cards[f]["card"].destroy()
                del self.song_cards[f]
        self.selected_folders.clear()
        self.apply_filter()
        if self.notify_cb:
            self.notify_cb(msg)
