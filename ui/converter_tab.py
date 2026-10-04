import customtkinter as ctk
from tkinter import filedialog
import threading
import os

from core.converter import SongConverter
from ui.theme import THEME, FONT_FAMILY

class ConverterTab(ctk.CTkFrame):
    def __init__(self, master, game_detector, notify_cb=None):
        super().__init__(master, fg_color="transparent")
        self.game_detector = game_detector
        self.notify_cb = notify_cb
        self.queued_files = []
        self.is_converting = False
        self.cancel_requested = False

        self.setup_ui()

    def setup_ui(self):
        # 1. Large Drag & Drop / Selection Dropzone
        self.dropzone = ctk.CTkFrame(
            self,
            fg_color=THEME["bg_card"],
            corner_radius=14,
            border_width=2,
            border_color=THEME["accent_pink"]
        )
        self.dropzone.pack(fill="x", padx=20, pady=(15, 10))

        # Big icon / title inside dropzone
        icon_lbl = ctk.CTkLabel(
            self.dropzone,
            text="🎸  DRAG & DROP YOUR CHARTS HERE",
            font=(FONT_FAMILY, 18, "bold"),
            text_color=THEME["text_primary"]
        )
        icon_lbl.pack(pady=(20, 4))

        sub_lbl = ctk.CTkLabel(
            self.dropzone,
            text="Drop single or multiple .zip, .sng files, or uncompressed Clone Hero / Rock Band song folders",
            font=(FONT_FAMILY, 12),
            text_color=THEME["text_secondary"]
        )
        sub_lbl.pack(pady=(0, 15))

        # Browse buttons row
        btn_row = ctk.CTkFrame(self.dropzone, fg_color="transparent")
        btn_row.pack(pady=(0, 20))

        browse_files_btn = ctk.CTkButton(
            btn_row,
            text="📁 Select Files (.zip / .sng)",
            font=(FONT_FAMILY, 13, "bold"),
            fg_color=THEME["accent_purple"],
            hover_color="#7c3aed",
            height=38,
            corner_radius=8,
            command=self.browse_files
        )
        browse_files_btn.pack(side="left", padx=8)

        browse_folder_btn = ctk.CTkButton(
            btn_row,
            text="📂 Select Folder",
            font=(FONT_FAMILY, 13, "bold"),
            fg_color="#374151",
            hover_color="#4b5563",
            height=38,
            corner_radius=8,
            command=self.browse_folder
        )
        browse_folder_btn.pack(side="left", padx=8)

        clear_btn = ctk.CTkButton(
            btn_row,
            text="Clear Queue",
            font=(FONT_FAMILY, 12),
            fg_color="transparent",
            hover_color="#272733",
            border_width=1,
            border_color=THEME["border_color"],
            height=38,
            corner_radius=8,
            command=self.clear_queue
        )
        clear_btn.pack(side="left", padx=8)

        # 2. Queue & Action Bar
        action_bar = ctk.CTkFrame(self, fg_color=THEME["bg_card"], corner_radius=10, border_width=1, border_color=THEME["border_color"])
        action_bar.pack(fill="x", padx=20, pady=5)

        self.queue_count_lbl = ctk.CTkLabel(
            action_bar,
            text="Status: Ready (Drop or select files to convert automatically)",
            font=(FONT_FAMILY, 13, "bold"),
            text_color=THEME["accent_blue"]
        )
        self.queue_count_lbl.pack(side="left", padx=15, pady=10)

        self.cancel_btn = ctk.CTkButton(
            action_bar,
            text="⏹ Stop / Cancel",
            font=(FONT_FAMILY, 13, "bold"),
            fg_color="#374151",
            hover_color="#dc2626",
            text_color="#ffffff",
            height=36,
            corner_radius=8,
            state="disabled",
            command=self.cancel_conversion
        )
        self.cancel_btn.pack(side="right", padx=15, pady=10)

        # 3. Overall Progress Bar
        self.overall_prog = ctk.CTkProgressBar(self, height=10, corner_radius=5, fg_color=THEME["bg_input"], progress_color=THEME["accent_green"])
        self.overall_prog.pack(fill="x", padx=20, pady=(8, 4))
        self.overall_prog.set(0)

        # 4. Conversion Log Output
        self.log_box = ctk.CTkTextbox(
            self,
            font=("Consolas", 11),
            fg_color=THEME["bg_card"],
            text_color=THEME["text_primary"],
            border_width=1,
            border_color=THEME["border_color"],
            corner_radius=10
        )
        self.log_box.pack(fill="both", expand=True, padx=20, pady=(4, 15))

        # Register TkinterDnD drop target
        self._register_dnd()

    def _register_dnd(self):
        try:
            from tkinterdnd2 import DND_FILES
            targets = [self.dropzone, self]
            def collect(w):
                targets.append(w)
                for c in w.winfo_children():
                    collect(c)
            collect(self.dropzone)

            for target in targets:
                try:
                    target.drop_target_register(DND_FILES)
                    target.dnd_bind("<<Drop>>", self.on_drop)
                except Exception:
                    pass
        except Exception as e:
            print(f"[DnD] Registration note: {e}")

    def on_drop(self, event):
        raw_data = event.data
        if not raw_data:
            return

        files = []
        try:
            if hasattr(self, 'tk') and hasattr(self.tk, 'splitlist'):
                files = list(self.tk.splitlist(raw_data))
        except Exception:
            pass

        if not files:
            if "{" in raw_data:
                import re
                files = re.findall(r'\{([^}]+)\}', raw_data)
            else:
                files = raw_data.split()

        self.add_to_queue(files)

    def browse_files(self):
        f = filedialog.askopenfilenames(
            title="Select Chart Files",
            filetypes=[("Clone Hero Charts (*.zip, *.sng)", "*.zip *.sng"), ("ZIP Archives", "*.zip"), ("SNG Files", "*.sng"), ("All Files", "*.*")]
        )
        if f:
            self.add_to_queue(list(f))

    def browse_folder(self):
        d = filedialog.askdirectory(title="Select Song Folder")
        if d:
            self.add_to_queue([d])

    def add_to_queue(self, paths):
        added = []
        for p in paths:
            clean_p = p.strip('"').strip("'")
            if os.path.exists(clean_p) and clean_p not in self.queued_files:
                self.queued_files.append(clean_p)
                added.append(clean_p)
                self.log(f"[Added to Queue] {os.path.basename(clean_p)}")
        self.update_queue_label()

        # Automatically start conversion upon adding files if not already converting
        if added and not self.is_converting:
            self.start_batch_conversion()

    def clear_queue(self):
        if self.is_converting:
            self.cancel_conversion()
        self.queued_files.clear()
        self.update_queue_label()
        self.log("[Queue] Queue cleared.")

    def cancel_conversion(self):
        if not self.is_converting:
            return
        self.cancel_requested = True
        self.cancel_btn.configure(state="disabled", text="Stopping...")
        self.log("[Cancel] User requested stop. Stopping current conversion...")

    def update_queue_label(self):
        if not self.is_converting:
            if self.queued_files:
                self.queue_count_lbl.configure(text=f"Queue: {len(self.queued_files)} items ready")
            else:
                self.queue_count_lbl.configure(text="Status: Ready (Drop or select files to convert automatically)")

    def log(self, text):
        self.log_box.insert("end", text + "\n")
        self.log_box.see("end")

    def start_batch_conversion(self):
        if self.is_converting or not self.queued_files:
            return

        target_dir = self.game_detector.get_custom_songs_dir()
        if not target_dir:
            self.log("[ERROR] Unplugged CustomSongs folder not found. Please set it in Settings.")
            return

        self.is_converting = True
        self.cancel_requested = False
        self.cancel_btn.configure(state="normal", text="⏹ Stop / Cancel", fg_color="#ef4444")
        self.overall_prog.set(0.01)

        threading.Thread(target=self._batch_worker, args=(target_dir,), daemon=True).start()

    def _batch_worker(self, dest_dir):
        successes = 0
        total_at_start = len(self.queued_files)

        self.after(0, lambda: self.log(f"\n=========================================="))
        self.after(0, lambda: self.log(f"Auto-converting {total_at_start} chart(s)..."))
        self.after(0, lambda: self.log(f"=========================================="))

        processed = 0
        while self.queued_files and not self.cancel_requested:
            item = self.queued_files.pop(0)
            processed += 1
            remaining = len(self.queued_files)

            self.after(0, lambda i=item, r=remaining: self.queue_count_lbl.configure(
                text=f"Converting: {os.path.basename(i)} ({r} left in queue)"
            ))
            self.after(0, lambda i=item: self.log(f"\nProcessing: {os.path.basename(i)}"))

            def log_cb(msg):
                self.after(0, lambda m=msg: self.log(m))

            converter = SongConverter(log_cb=log_cb, cancel_check=lambda: self.cancel_requested)
            ok = converter.convert_song(item, dest_dir)
            if ok:
                successes += 1

            prog = processed / max(total_at_start, processed)
            self.after(0, lambda p=prog: self.overall_prog.set(p))

            if self.cancel_requested:
                break

        was_cancelled = self.cancel_requested
        self.after(0, lambda s=successes, c=was_cancelled: self._on_batch_complete(s, c))

    def _on_batch_complete(self, successes, was_cancelled):
        self.is_converting = False
        self.cancel_requested = False
        self.cancel_btn.configure(state="disabled", text="⏹ Stop / Cancel", fg_color="#374151")
        self.overall_prog.set(1.0 if not was_cancelled else 0.0)
        self.update_queue_label()

        if was_cancelled:
            summary = f"\n[Cancelled] Conversion stopped by user. {successes} song(s) completed."
        else:
            summary = f"\nConversion finished: {successes} song(s) converted successfully to CustomSongs!"
        self.log(summary)
        if self.notify_cb:
            self.notify_cb(summary)
