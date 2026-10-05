import customtkinter as ctk
import sys
import os

try:
    from tkinterdnd2 import TkinterDnD, DND_FILES
    HAS_TKDND = True
except ImportError:
    HAS_TKDND = False

from core.game_detector import GameDetector
from core.audio_player import player_instance
from ui.theme import THEME, FONT_FAMILY
from ui.search_tab import SearchTab
from ui.converter_tab import ConverterTab
from ui.library_tab import LibraryTab
from ui.settings_tab import SettingsTab

BaseAppClass = (ctk.CTk, TkinterDnD.DnDWrapper) if HAS_TKDND else (ctk.CTk,)

class UnpluggedStudioApp(*BaseAppClass):
    def __init__(self):
        super().__init__()
        if HAS_TKDND:
            try:
                self.TkdndVersion = TkinterDnD._require(self)
            except Exception as e:
                print(f"[DnD] Init error: {e}")

        # Window configuration
        self.title("USM • Unplugged Song Manager")
        self.geometry("1020x720")
        self.minsize(860, 580)
        self.configure(fg_color=THEME["bg_main"])

        # Core manager
        self.game_detector = GameDetector()

        # Build interface
        self.setup_ui()

        # Window closing protocol
        self.protocol("WM_DELETE_WINDOW", self.on_closing)

    def setup_ui(self):
        # 1. Top Navbar / Header
        navbar = ctk.CTkFrame(self, fg_color=THEME["bg_card"], height=65, corner_radius=0, border_width=1, border_color=THEME["border_color"])
        navbar.pack(fill="x", side="top")
        navbar.pack_propagate(False)

        # Brand / Title
        brand_frame = ctk.CTkFrame(navbar, fg_color="transparent")
        brand_frame.pack(side="left", padx=20, fill="y")

        logo_lbl = ctk.CTkLabel(
            brand_frame,
            text="🎸 USM",
            font=(FONT_FAMILY, 20, "bold"),
            text_color=THEME["accent_pink"]
        )
        logo_lbl.pack(side="left")

        subtitle_lbl = ctk.CTkLabel(
            brand_frame,
            text="UNPLUGGED SONG MANAGER",
            font=(FONT_FAMILY, 14, "bold"),
            text_color=THEME["text_secondary"]
        )
        subtitle_lbl.pack(side="left", padx=(10, 0))

        # Game Path Status Badge
        self.badge_frame = ctk.CTkFrame(navbar, fg_color=THEME["bg_input"], corner_radius=8)
        self.badge_frame.pack(side="right", padx=20, pady=12)

        is_connected = bool(self.game_detector.game_path)
        self.badge_dot = ctk.CTkLabel(
            self.badge_frame,
            text="●",
            font=(FONT_FAMILY, 14),
            text_color=THEME["accent_green"] if is_connected else "orange"
        )
        self.badge_dot.pack(side="left", padx=(10, 4))

        self.badge_text = ctk.CTkLabel(
            self.badge_frame,
            text="Game Connected" if is_connected else "Game Not Detected",
            font=(FONT_FAMILY, 12, "bold"),
            text_color=THEME["text_secondary"]
        )
        self.badge_text.pack(side="left", padx=(0, 10))

        # 2. Main Tabview
        self.tabview = ctk.CTkTabview(
            self,
            fg_color="transparent",
            segmented_button_fg_color=THEME["bg_card"],
            segmented_button_selected_color=THEME["accent_pink"],
            segmented_button_selected_hover_color="#e0245e",
            segmented_button_unselected_color=THEME["bg_card"],
            segmented_button_unselected_hover_color=THEME["bg_card_hover"],
            text_color=THEME["text_primary"],
            corner_radius=10
        )
        self.tabview.pack(fill="both", expand=True, padx=15, pady=(5, 5))

        # Add tabs in English
        tab_search = self.tabview.add("🔍 Download (Chorus)")
        tab_converter = self.tabview.add("⚡ Local Converter")
        tab_library = self.tabview.add("📚 Song Library")
        tab_settings = self.tabview.add("⚙ Settings")

        # Instantiate tab contents
        self.search_view = SearchTab(tab_search, self.game_detector, notify_cb=self.notify)
        self.search_view.pack(fill="both", expand=True)

        self.converter_view = ConverterTab(tab_converter, self.game_detector, notify_cb=self.notify)
        self.converter_view.pack(fill="both", expand=True)

        self.library_view = LibraryTab(tab_library, self.game_detector, notify_cb=self.notify)
        self.library_view.pack(fill="both", expand=True)

        self.settings_view = SettingsTab(tab_settings, self.game_detector, notify_cb=self.notify)
        self.settings_view.pack(fill="both", expand=True)

        # 3. Bottom Notification Bar
        self.toast_bar = ctk.CTkFrame(self, fg_color=THEME["bg_card"], height=32, corner_radius=0)
        self.toast_bar.pack(fill="x", side="bottom")

        self.toast_lbl = ctk.CTkLabel(
            self.toast_bar,
            text="Ready. Search or drop chart files to add songs to your VR library.",
            font=(FONT_FAMILY, 11),
            text_color=THEME["text_muted"]
        )
        self.toast_lbl.pack(side="left", padx=15)

    def notify(self, message):
        self.toast_lbl.configure(text=message, text_color=THEME["accent_green"])
        # Update badge
        is_conn = bool(self.game_detector.game_path)
        self.badge_dot.configure(text_color=THEME["accent_green"] if is_conn else "orange")
        self.badge_text.configure(text="Game Connected" if is_conn else "Game Not Detected")
        try:
            self.library_view.refresh_library()
        except Exception:
            pass

    def on_closing(self):
        try:
            self.withdraw()
            self.update_idletasks()
        except Exception:
            pass
        try:
            player_instance.shutdown()
        except Exception:
            pass
        os._exit(0)

if __name__ == "__main__":
    ctk.set_appearance_mode("Dark")
    ctk.set_default_color_theme("blue")
    app = UnpluggedStudioApp()
    app.mainloop()
