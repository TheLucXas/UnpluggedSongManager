import customtkinter as ctk
import threading
import tempfile
import os
import re
from PIL import Image

from core.api import ChorusAPI
from core.converter import SongConverter
from core.audio_player import player_instance
from ui.theme import THEME, FONT_FAMILY

def clean_text(s):
    if not s: return ""
    return re.sub(r'<[^>]*>', '', str(s)).strip()

class SearchTab(ctk.CTkFrame):
    def __init__(self, master, game_detector, notify_cb=None):
        super().__init__(master, fg_color="transparent")
        self.game_detector = game_detector
        self.notify_cb = notify_cb
        self.is_searching = False
        self.current_preview_btn = None
        self._pending_charts = []

        self.setup_ui()

    def setup_ui(self):
        # Top search container
        search_bar_frame = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=12, border_width=1, border_color=THEME["border_color"])
        search_bar_frame.pack(fill="x", padx=20, pady=(15, 10))

        self.search_entry = ctk.CTkEntry(
            search_bar_frame,
            placeholder_text="Search songs by title, artist, band or charter (e.g. Nirvana, Metallica, Polyphia)...",
            height=44,
            font=(FONT_FAMILY, 14),
            fg_color=THEME["bg_input"],
            border_width=0,
            text_color=THEME["text_primary"]
        )
        self.search_entry.pack(side="left", fill="x", expand=True, padx=(15, 10), pady=12)
        self.search_entry.bind("<Return>", lambda e: self.perform_search())

        self.search_btn = ctk.CTkButton(
            search_bar_frame,
            text="Search Charts",
            font=(FONT_FAMILY, 13, "bold"),
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            height=44,
            corner_radius=8,
            command=self.perform_search
        )
        self.search_btn.pack(side="right", padx=(0, 15), pady=12)

        # Status & results count header
        self.status_label = ctk.CTkLabel(
            self,
            text="Type a song or artist name to search the Chorus Encore / Clone Hero database.",
            font=(FONT_FAMILY, 13),
            text_color=THEME["text_secondary"],
            anchor="w"
        )
        self.status_label.pack(fill="x", padx=25, pady=(0, 5))

        # Scrollable container for search cards
        self.results_scroll = ctk.CTkScrollableFrame(
            self,
            fg_color="transparent",
            corner_radius=0
        )
        self.results_scroll.pack(fill="both", expand=True, padx=15, pady=(5, 10))

    def perform_search(self):
        if self.is_searching:
            return
        query = self.search_entry.get().strip()
        if not query:
            return

        self.is_searching = True
        self.search_btn.configure(state="disabled", text="Searching...")
        self.status_label.configure(text=f"Searching Chorus Encore for '{query}'...")

        # Clear existing cards
        self._pending_charts.clear()
        for widget in self.results_scroll.winfo_children():
            widget.destroy()

        threading.Thread(target=self._search_worker, args=(query,), daemon=True).start()

    def _search_worker(self, query):
        try:
            res = ChorusAPI.search(query=query, per_page=25)
            charts = res.get("charts", [])
            self.after(0, lambda: self._on_search_complete(charts))
        except Exception as e:
            self.after(0, lambda: self._on_search_error(str(e)))

    def _on_search_complete(self, charts):
        self.is_searching = False
        self.search_btn.configure(state="normal", text="Search Charts")

        if not charts:
            self.status_label.configure(text="No charts found for this query. Try a different search term.")
            return

        self.status_label.configure(text=f"Found {len(charts)} charts available for Unplugged VR:")

        # Smooth progressive rendering: add cards in chunks to avoid UI stutter
        self._pending_charts = list(charts)
        self._render_next_chunk()

    def _render_next_chunk(self):
        if not self._pending_charts:
            return
        chunk = self._pending_charts[:5]
        self._pending_charts = self._pending_charts[5:]
        for chart in chunk:
            self.create_chart_card(chart)
        if self._pending_charts:
            self.after(20, self._render_next_chunk)

    def _on_search_error(self, err_msg):
        self.is_searching = False
        self.search_btn.configure(state="normal", text="Search Charts")
        self.status_label.configure(text=f"Network error: {err_msg}")

    def create_chart_card(self, chart):
        card = ctk.CTkFrame(
            self.results_scroll,
            fg_color=THEME["bg_card"],
            corner_radius=10,
            border_width=1,
            border_color=THEME["border_color"]
        )
        card.pack(fill="x", pady=6, padx=5)

        # Info column
        info_col = ctk.CTkFrame(card, fg_color="transparent")
        info_col.pack(side="left", fill="both", expand=True, padx=15, pady=12)

        # Clean title & artist
        title_clean = clean_text(chart["title"])
        artist_clean = clean_text(chart["artist"])
        charter_clean = clean_text(chart["charter"])

        title_label = ctk.CTkLabel(
            info_col,
            text=title_clean,
            font=(FONT_FAMILY, 15, "bold"),
            text_color=THEME["text_primary"],
            anchor="w"
        )
        title_label.pack(fill="x")

        # Subtitle / Artist
        artist_text = artist_clean
        if chart['album']:
            artist_text += f" • {clean_text(chart['album'])}"
        if chart['year']:
            artist_text += f" ({chart['year']})"

        artist_label = ctk.CTkLabel(
            info_col,
            text=artist_text,
            font=(FONT_FAMILY, 13),
            text_color=THEME["accent_blue"],
            anchor="w"
        )
        artist_label.pack(fill="x", pady=(2, 0))

        # Metadata line
        diff_str = f"Guitar: Tier {chart['diff_guitar']}" if chart['diff_guitar'] >= 0 else "Guitar: Yes"
        meta_text = f"Charter: {charter_clean} | Duration: {chart['duration']} | {diff_str}"
        meta_label = ctk.CTkLabel(
            info_col,
            text=meta_text,
            font=(FONT_FAMILY, 11),
            text_color=THEME["text_muted"],
            anchor="w"
        )
        meta_label.pack(fill="x", pady=(3, 0))

        # Actions column
        actions_col = ctk.CTkFrame(card, fg_color="transparent")
        actions_col.pack(side="right", padx=15, pady=12)

        # Download / Install button
        dl_btn = ctk.CTkButton(
            actions_col,
            text="📥 Download & Install",
            font=(FONT_FAMILY, 12, "bold"),
            fg_color=THEME["accent_pink"],
            hover_color="#e0245e",
            height=34,
            width=170,
            corner_radius=8
        )
        dl_btn.pack(side="top", pady=2)

        prog_bar = ctk.CTkProgressBar(actions_col, width=170, height=8, corner_radius=4, fg_color=THEME["bg_input"], progress_color=THEME["accent_green"])
        prog_bar.set(0)

        card_status = ctk.CTkLabel(actions_col, text="", font=(FONT_FAMILY, 10), text_color=THEME["text_secondary"])

        # Check if already installed
        cs_dir = self.game_detector.get_custom_songs_dir()
        if cs_dir:
            folder_guess = f"{artist_clean} - {title_clean}"
            if os.path.isdir(os.path.join(cs_dir, folder_guess)):
                dl_btn.configure(state="disabled", text="✓ Installed", fg_color=THEME["accent_green"])
                card_status.pack(side="top")
                card_status.configure(text="In CustomSongs", text_color=THEME["accent_green"])
                return

        dl_btn.configure(command=lambda c=chart, b=dl_btn, pb=prog_bar, st=card_status: self.start_download_and_convert(c, b, pb, st))

    def start_download_and_convert(self, chart, button, prog_bar, status_lbl):
        custom_songs_dir = self.game_detector.get_custom_songs_dir()
        if not custom_songs_dir:
            status_lbl.configure(text="Please set Unplugged folder in Settings!", text_color="red")
            return

        button.configure(state="disabled", text="Downloading...")
        prog_bar.pack(side="top", pady=(4, 2))
        status_lbl.pack(side="top")
        status_lbl.configure(text="Downloading .sng package...")

        threading.Thread(
            target=self._download_worker,
            args=(chart, button, prog_bar, status_lbl, custom_songs_dir),
            daemon=True
        ).start()

    def _download_worker(self, chart, button, prog_bar, status_lbl, target_dir):
        temp_sng = None
        try:
            with tempfile.NamedTemporaryFile(suffix=".sng", delete=False) as tf:
                temp_sng = tf.name

            def dl_cb(curr, total):
                if total > 0:
                    frac = curr / total
                    self.after(0, lambda: prog_bar.set(frac * 0.5))

            ChorusAPI.download_file(chart["download_url"], temp_sng, progress_callback=dl_cb)

            self.after(0, lambda: status_lbl.configure(text="Converting to Unplugged VR..."))

            def conv_prog(frac, text):
                self.after(0, lambda: [prog_bar.set(0.5 + frac * 0.5), status_lbl.configure(text=text)])

            converter = SongConverter(progress_cb=conv_prog)
            success = converter.convert_song(temp_sng, target_dir)

            if success:
                self.after(0, lambda: self._on_install_success(button, prog_bar, status_lbl, chart["title"]))
            else:
                self.after(0, lambda: self._on_install_fail(button, prog_bar, status_lbl, "Conversion failed"))

        except Exception as e:
            self.after(0, lambda: self._on_install_fail(button, prog_bar, status_lbl, str(e)))
        finally:
            if temp_sng and os.path.exists(temp_sng):
                try: os.remove(temp_sng)
                except Exception: pass

    def _on_install_success(self, button, prog_bar, status_lbl, title):
        prog_bar.pack_forget()
        status_lbl.configure(text="✓ Installed in CustomSongs", text_color=THEME["accent_green"])
        button.configure(state="disabled", text="✓ Installed!", fg_color=THEME["accent_green"])
        if self.notify_cb:
            self.notify_cb(f"Song '{title}' downloaded and converted successfully!")

    def _on_install_fail(self, button, prog_bar, status_lbl, err):
        prog_bar.pack_forget()
        status_lbl.configure(text=f"Error: {err}", text_color="red")
        button.configure(state="normal", text="Retry Download")
