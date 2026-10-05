import os
import sys
import re
import shutil
import tempfile
import zipfile
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

try:
    import sng_parser
except ImportError:
    sng_parser = None

try:
    import mido
except ImportError:
    mido = None

def find_ffmpeg():
    # 0. PyInstaller bundled temp dir (_MEIPASS)
    if hasattr(sys, '_MEIPASS'):
        p = os.path.join(sys._MEIPASS, 'ffmpeg.exe')
        if os.path.isfile(p): return p
        p_int = os.path.join(sys._MEIPASS, '_internal', 'ffmpeg.exe')
        if os.path.isfile(p_int): return p_int

    # 1. Next to executable or _internal
    exe_dir = os.path.dirname(sys.executable)
    p_exe = os.path.join(exe_dir, 'ffmpeg.exe')
    if os.path.isfile(p_exe): return p_exe
    p_exe_int = os.path.join(exe_dir, '_internal', 'ffmpeg.exe')
    if os.path.isfile(p_exe_int): return p_exe_int

    # 2. System PATH
    p = shutil.which("ffmpeg")
    if p: return p

    # 3. Local app dir
    local_p = os.path.join(os.path.dirname(__file__), "..", "ffmpeg.exe")
    if os.path.isfile(local_p): return local_p

    # 4. User local AppData ffmpegio
    appdata_p = os.path.expandvars(r"%LOCALAPPDATA%\ffmpegio\ffmpeg-downloader\ffmpeg\bin\ffmpeg.exe")
    if os.path.isfile(appdata_p): return appdata_p

    return "ffmpeg"

def sanitize_folder_name(name):
    clean = re.sub(r'[\\/*?:"<>|]', "", name).strip()
    return clean if clean else "Custom Song"

FINGER_MAP = {
    0: (0, ["Index"]),
    1: (0, ["Index", "Middle"]),
    2: (1, ["Index", "Middle", "Ring"]),
    3: (2, ["Index", "Middle", "Ring", "Pinky"]),
    4: (3, ["Index", "Middle", "Ring", "Pinky"]),
    7: (0, ["Index"]) # Open note
}

CHORD_MAP = {
    (0, 1): (0, ["Index", "Middle"]),
    (0, 2): (0, ["Index", "Ring"]),       # Classic Power Chord
    (1, 2): (1, ["Index", "Middle"]),
    (1, 3): (1, ["Index", "Ring"]),       # Power chord
    (2, 3): (2, ["Index", "Middle"]),
    (2, 4): (2, ["Index", "Ring"]),       # Power chord
    (3, 4): (3, ["Index", "Middle"]),
    (0, 1, 2): (0, ["Index", "Middle", "Ring"]),
    (1, 2, 3): (1, ["Index", "Middle", "Ring"]),
    (2, 3, 4): (2, ["Index", "Middle", "Ring"]),
}

class SongConverter:
    def __init__(self, log_cb=None, progress_cb=None, cancel_check=None):
        self.log_cb = log_cb or print
        self.progress_cb = progress_cb or (lambda p, t: None)
        self.cancel_check = cancel_check or (lambda: False)
        self.ffmpeg_path = find_ffmpeg()

    def _run_ffmpeg(self, cmd_args):
        if self.cancel_check():
            raise RuntimeError("Operation cancelled by user.")
        flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
        cmd = [self.ffmpeg_path] + cmd_args
        return subprocess.run(
            cmd,
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=flags
        )

    def log(self, msg):
        self.log_cb(msg)

    def parse_ini(self, ini_path):
        info = {
            'name': 'Unknown Title',
            'artist': 'Unknown Artist',
            'charter': 'Unknown',
            'preview_start': 30.0,
            'delay': 3.0
        }
        if not os.path.exists(ini_path):
            return info
        try:
            with open(ini_path, 'r', encoding='utf-8', errors='ignore') as f:
                for line in f:
                    line = line.strip()
                    if '=' in line and not line.startswith('['):
                        k, v = line.split('=', 1)
                        k = k.strip().lower()
                        v = v.strip().strip('"')
                        if k == 'name' and v: info['name'] = re.sub(r'<[^>]*>', '', v).strip()
                        elif k == 'artist' and v: info['artist'] = re.sub(r'<[^>]*>', '', v).strip()
                        elif k == 'charter' and v: info['charter'] = re.sub(r'<[^>]*>', '', v).strip()
                        elif k == 'preview_start_time':
                            try: info['preview_start'] = float(v) / 1000.0
                            except: pass
                        elif k == 'delay':
                            try: info['delay'] = max(2.0, float(v) / 1000.0)
                            except: pass
        except Exception:
            pass
        return info

    def parse_chart(self, chart_path):
        sections = {}
        cur_sec = None
        with open(chart_path, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                line = line.strip()
                if line.startswith('[') and line.endswith(']'):
                    cur_sec = line[1:-1]
                    sections[cur_sec] = []
                elif cur_sec and line != '{' and line != '}':
                    sections[cur_sec].append(line)
        return sections

    def process_sync_track(self, sync_lines, resolution=192):
        events = []
        for l in sync_lines:
            m_bpm = re.match(r'(\d+)\s*=\s*B\s+(\d+)', l)
            if m_bpm:
                tick = int(m_bpm.group(1))
                bpm = int(m_bpm.group(2)) / 1000.0
                events.append((tick, bpm))
        events.sort(key=lambda x: x[0])
        if not events or events[0][0] != 0:
            events.insert(0, (0, 120.0))

        timed_bpms = []
        cur_time = 0.0
        last_tick = 0
        last_bpm = events[0][1]

        for tick, bpm in events:
            delta_ticks = tick - last_tick
            cur_time += (delta_ticks / resolution) * (60.0 / last_bpm)
            timed_bpms.append((tick, cur_time, bpm))
            last_tick = tick
            last_bpm = bpm

        def tick_to_seconds(tick):
            for i in range(len(timed_bpms) - 1, -1, -1):
                btick, btime, bbpm = timed_bpms[i]
                if tick >= btick:
                    return btime + (tick - btick) * (60.0 / (bbpm * resolution))
            return 0.0

        return timed_bpms, tick_to_seconds

    def parse_notes_from_chart_section(self, lines):
        notes_by_tick = {}
        for l in lines:
            m_n = re.match(r'(\d+)\s*=\s*N\s+(\d+)\s+(\d+)', l)
            if m_n:
                tick = int(m_n.group(1))
                lane = int(m_n.group(2))
                dur = int(m_n.group(3))
                if tick not in notes_by_tick:
                    notes_by_tick[tick] = []
                notes_by_tick[tick].append(('N', lane, dur))
        return notes_by_tick

    def build_unplugged_xml(self, timed_bpms, tick_to_sec, notes_by_tick, ticks_per_beat=480):
        ET.register_namespace('a', 'http://schemas.microsoft.com/2003/10/Serialization/Arrays')
        ET.register_namespace('i', 'http://www.w3.org/2001/XMLSchema-instance')

        root = ET.Element("SongLevel", {
            "xmlns": "http://schemas.datacontract.org/2004/07/"
        })

        initial_bpm = int(timed_bpms[0][2]) if timed_bpms else 120
        ET.SubElement(root, "BPM").text = str(initial_bpm)

        bpm_grid = ET.SubElement(root, "BPMGrid")
        for tick, time_sec, bpm_val in timed_bpms:
            bpm_key = ET.SubElement(bpm_grid, "BPMKey")
            ET.SubElement(bpm_key, "Duration").text = "0"
            ET.SubElement(bpm_key, "Time").text = f"{time_sec:.6f}"
            ET.SubElement(bpm_key, "BPM").text = f"{bpm_val:.3f}"

        ET.SubElement(root, "BeatGrid", {"{http://www.w3.org/2001/XMLSchema-instance}nil": "true"})
        ET.SubElement(root, "BeatGridFromFile").text = "false"
        ET.SubElement(root, "EditorBPM").text = "0"

        keys_elem = ET.SubElement(root, "Keys")

        sorted_ticks = sorted(notes_by_tick.keys())
        last_tick = sorted_ticks[-1] if sorted_ticks else 0
        total_dur = tick_to_sec(last_tick) + 10.0

        # 1. Hand Tracking Track (GuitarGame)
        key_time_hand = ET.SubElement(keys_elem, "KeyTime", {"{http://www.w3.org/2001/XMLSchema-instance}type": "GuitarGame"})
        ET.SubElement(key_time_hand, "Duration").text = f"{total_dur:.6f}"
        ET.SubElement(key_time_hand, "Time").text = "0"
        ET.SubElement(key_time_hand, "PrewarmTime").text = "4"
        notes_hand_elem = ET.SubElement(key_time_hand, "Notes")

        # 2. Controller Track (GuitarControllerGame)
        key_time_ctrl = ET.SubElement(keys_elem, "KeyTime", {"{http://www.w3.org/2001/XMLSchema-instance}type": "GuitarControllerGame"})
        ET.SubElement(key_time_ctrl, "Duration").text = f"{total_dur:.6f}"
        ET.SubElement(key_time_ctrl, "Time").text = "0"
        ET.SubElement(key_time_ctrl, "PrewarmTime").text = "4"
        notes_ctrl_elem = ET.SubElement(key_time_ctrl, "Notes")

        prev_time = -999.0
        prev_tier = -1

        for tick in sorted_ticks:
            entries = notes_by_tick[tick]
            note_time = tick_to_sec(tick)

            lanes = [e[0] for e in entries]
            max_dur_ticks = max(e[1] for e in entries)

            if max_dur_ticks > (ticks_per_beat // 2):
                dur_sec = max(0.0, tick_to_sec(tick + max_dur_ticks) - note_time)
            else:
                dur_sec = 0.0

            sorted_lanes = tuple(sorted(set(lanes)))
            if not sorted_lanes:
                continue

            if len(sorted_lanes) == 1:
                tier, fingers = FINGER_MAP.get(sorted_lanes[0], (0, ["Index"]))
            else:
                if sorted_lanes in CHORD_MAP:
                    tier, fingers = CHORD_MAP[sorted_lanes]
                else:
                    tier = min(3, sorted_lanes[-1])
                    fingers = ["Index", "Middle", "Ring"]

            is_pulloff = (note_time - prev_time < 0.22 and prev_tier != tier)
            note_type = "PullOffGuitarNote" if is_pulloff else None

            # Add to Hand Tracking
            tier_attrib = {"{http://www.w3.org/2001/XMLSchema-instance}type": note_type} if note_type else {}
            tier_note = ET.SubElement(notes_hand_elem, "TierNote", tier_attrib)
            ET.SubElement(tier_note, "Duration").text = f"{dur_sec:.6f}"
            ET.SubElement(tier_note, "Time").text = f"{note_time:.6f}"
            ET.SubElement(tier_note, "Tier").text = str(tier)
            ET.SubElement(tier_note, "CursableProbability").text = "0"
            ET.SubElement(tier_note, "IsBlind").text = "false"
            fingers_elem = ET.SubElement(tier_note, "Fingers")
            for f in fingers:
                ET.SubElement(fingers_elem, "TierFinger").text = f

            # Determine Controller Buttons & Rotation
            if len(sorted_lanes) == 1:
                lane = sorted_lanes[0]
                if lane in (0, 1): # Green / Red -> Trigger (Red band)
                    btn_vals, rot_val = ("true", "false"), 0
                elif lane in (2, 3): # Yellow / Blue -> Grip (Yellow band)
                    btn_vals, rot_val = ("false", "true"), 0
                elif lane == 4: # Orange -> Grip + Inverted
                    btn_vals, rot_val = ("false", "true"), 1
                else:
                    btn_vals, rot_val = ("true", "false"), 0
            else:
                has_lower = any(l in (0, 1) for l in sorted_lanes)
                has_upper = any(l in (2, 3, 4) for l in sorted_lanes)
                rot_val = 1 if (4 in sorted_lanes and not has_lower) else 0
                if has_lower and has_upper:
                    btn_vals = ("true", "true")
                elif has_lower:
                    btn_vals = ("true", "false")
                else:
                    btn_vals = ("false", "true")

            # Add to Controller
            ctrl_note = ET.SubElement(notes_ctrl_elem, "ControllerGuitarNote")
            ET.SubElement(ctrl_note, "Duration").text = f"{dur_sec:.6f}"
            ET.SubElement(ctrl_note, "Time").text = f"{note_time:.6f}"
            ET.SubElement(ctrl_note, "Tier").text = str(tier)
            ET.SubElement(ctrl_note, "CursableProbability").text = "0"
            ET.SubElement(ctrl_note, "IsBlind").text = "false"
            buttons_elem = ET.SubElement(ctrl_note, "Buttons")
            b1 = ET.SubElement(buttons_elem, "{http://schemas.microsoft.com/2003/10/Serialization/Arrays}boolean")
            b1.text = btn_vals[0]
            b2 = ET.SubElement(buttons_elem, "{http://schemas.microsoft.com/2003/10/Serialization/Arrays}boolean")
            b2.text = btn_vals[1]
            ET.SubElement(ctrl_note, "ChordRotation").text = str(rot_val)
            ET.SubElement(ctrl_note, "Intensity").text = "0"

            prev_time = note_time
            prev_tier = tier

        return root

    def convert_midi(self, midi_path, out_dir):
        global mido
        if mido is None:
            import mido as m
            mido = m

        mid = mido.MidiFile(midi_path, clip=True)
        ticks_per_beat = mid.ticks_per_beat

        tempo_events = []
        cur_tick = 0
        for msg in mid.tracks[0]:
            cur_tick += msg.time
            if msg.type == 'set_tempo':
                bpm = 60000000.0 / msg.tempo
                tempo_events.append((cur_tick, msg.tempo, bpm))

        if not tempo_events:
            tempo_events = [(0, 500000, 120.0)]

        timed_bpms = []
        timed_tempo = []
        t = 0.0
        last_tick = 0
        cur_tempo = 500000
        for evt_tick, evt_tempo, evt_bpm in tempo_events:
            delta = evt_tick - last_tick
            t += delta * (cur_tempo / 1000000.0) / ticks_per_beat
            timed_tempo.append((evt_tick, t, evt_tempo, evt_bpm))
            timed_bpms.append((evt_tick, t, evt_bpm))
            last_tick = evt_tick
            cur_tempo = evt_tempo

        def tick_to_sec(tick):
            last_evt_tick = 0
            last_t = 0.0
            last_tempo = 500000
            for evt_tick, evt_t, evt_tempo, evt_bpm in timed_tempo:
                if tick <= evt_tick:
                    delta = tick - last_evt_tick
                    return last_t + delta * (last_tempo / 1000000.0) / ticks_per_beat
                last_evt_tick = evt_tick
                last_t = evt_t
                last_tempo = evt_tempo
            delta = tick - last_evt_tick
            return last_t + delta * (last_tempo / 1000000.0) / ticks_per_beat

        guitar_track = None
        for track in mid.tracks:
            tname = track.name.strip().upper() if track.name else ""
            if "PART GUITAR" in tname or "GUITAR" in tname:
                guitar_track = track
                break
        if not guitar_track:
            guitar_track = mid.tracks[1] if len(mid.tracks) > 1 else mid.tracks[0]

        diff_ranges = {
            'songdata-Hard.xml': (96, 100),
            'songdata-Normal.xml': (84, 88),
            'songdata-Easy.xml': (72, 76),
            'songdata-SuperEasy.xml': (60, 64)
        }

        diff_notes = {k: {} for k in diff_ranges}
        cur_tick = 0
        active_notes = {}

        for msg in guitar_track:
            cur_tick += msg.time
            if msg.type == 'note_on' and msg.velocity > 0:
                active_notes[msg.note] = (cur_tick, cur_tick)
            elif msg.type == 'note_off' or (msg.type == 'note_on' and msg.velocity == 0):
                if msg.note in active_notes:
                    start_tick, _ = active_notes.pop(msg.note)
                    dur = cur_tick - start_tick
                    for fname, (base, top) in diff_ranges.items():
                        if base <= msg.note <= top:
                            lane = msg.note - base
                            if start_tick not in diff_notes[fname]:
                                diff_notes[fname][start_tick] = []
                            diff_notes[fname][start_tick].append((lane, dur))

        if not any(diff_notes['songdata-Hard.xml'].values()):
            for fname in diff_ranges:
                if diff_notes[fname]:
                    diff_notes['songdata-Hard.xml'] = diff_notes[fname]
                    break

        for fname in diff_ranges:
            notes = diff_notes[fname]
            if not notes:
                notes = diff_notes['songdata-Hard.xml']
            root = self.build_unplugged_xml(timed_bpms, tick_to_sec, notes, ticks_per_beat)
            xml_bytes = ET.tostring(root, encoding='utf-8')
            with open(os.path.join(out_dir, fname), 'wb') as f:
                f.write(xml_bytes)
            self.log(f"  [XML] {fname}: {len(notes)} chords/notes.")

    def convert_chart(self, chart_path, out_dir):
        sections = self.parse_chart(chart_path)
        res = 192
        if 'Song' in sections:
            for l in sections['Song']:
                m = re.match(r'Resolution\s*=\s*(\d+)', l)
                if m: res = int(m.group(1))

        timed_bpms, tick_to_sec = self.process_sync_track(sections.get('SyncTrack', []), res)

        diff_sections = {
            'songdata-Hard.xml': ['ExpertSingle', 'HardSingle', 'MediumSingle', 'EasySingle'],
            'songdata-Normal.xml': ['HardSingle', 'ExpertSingle', 'MediumSingle', 'EasySingle'],
            'songdata-Easy.xml': ['MediumSingle', 'EasySingle', 'HardSingle', 'ExpertSingle'],
            'songdata-SuperEasy.xml': ['EasySingle', 'MediumSingle', 'HardSingle', 'ExpertSingle']
        }

        for fname, sec_candidates in diff_sections.items():
            notes = {}
            for sec_name in sec_candidates:
                if sec_name in sections:
                    parsed = self.parse_notes_from_chart_section(sections[sec_name])
                    if parsed:
                        for tick, entries in parsed.items():
                            notes[tick] = [(e[1], e[2]) for e in entries]
                        break

            root = self.build_unplugged_xml(timed_bpms, tick_to_sec, notes, res)
            xml_bytes = ET.tostring(root, encoding='utf-8')
            with open(os.path.join(out_dir, fname), 'wb') as f:
                f.write(xml_bytes)
            self.log(f"  [XML] {fname}: {len(notes)} chords/notes.")

    def _encode_or_copy(self, src_file, out_file):
        if not src_file or not os.path.exists(src_file):
            return
        if src_file.lower().endswith('.mp3'):
            shutil.copy2(src_file, out_file)
        else:
            self._run_ffmpeg(['-y', '-threads', '0', '-i', src_file, '-b:a', '192k', out_file])

    def convert_audio(self, src_dir, out_dir, preview_start=30.0):
        stem_patterns = {
            'guitar': ['guitar.ogg', 'guitar.opus', 'guitar.mp3', 'guitar.wav'],
            'bass': ['bass.ogg', 'bass.opus', 'bass.mp3', 'rhythm.ogg', 'rhythm.opus', 'rhythm.mp3'],
            'vocals': ['vocals.ogg', 'vocals.opus', 'vocals.mp3', 'vocals.wav'],
            'other': ['song.ogg', 'song.opus', 'song.mp3', 'band.ogg', 'band.opus', 'crowd.ogg']
        }

        stems_found = {}
        for stype, fnames in stem_patterns.items():
            for fn in fnames:
                p = os.path.join(src_dir, fn)
                if os.path.exists(p):
                    stems_found[stype] = p
                    break

        drum_files = [os.path.join(src_dir, f) for f in os.listdir(src_dir) if f.lower().startswith('drums') and f.lower().endswith(('.opus', '.ogg', '.mp3', '.wav'))]
        if drum_files:
            stems_found['drums'] = drum_files

        # Multitrack requires guitar AND at least one other backing stem
        has_multitrack = ('guitar' in stems_found and len(stems_found) > 1)

        if has_multitrack:
            self.log("  [Audio] Multitrack stems detected. Processing channels in parallel...")
            
            def process_stem(stype, src_item, target_filename):
                if self.cancel_check(): return
                out_path = os.path.join(out_dir, target_filename)
                if isinstance(src_item, list):
                    if len(src_item) > 1:
                        cmd = ['-y', '-threads', '0']
                        for df in src_item: cmd.extend(['-i', df])
                        cmd.extend(['-filter_complex', f'amix=inputs={len(src_item)}:dropout_transition=0:normalize=0', '-b:a', '192k', out_path])
                        self._run_ffmpeg(cmd)
                    elif len(src_item) == 1:
                        self._encode_or_copy(src_item[0], out_path)
                elif src_item:
                    self._encode_or_copy(src_item, out_path)

            tasks = [
                ('guitar', stems_found.get('guitar'), 'song-guitar.mp3'),
            ]
            if 'bass' in stems_found: tasks.append(('bass', stems_found['bass'], 'song-bass.mp3'))
            if 'vocals' in stems_found: tasks.append(('vocals', stems_found['vocals'], 'song-vocals.mp3'))
            if 'drums' in stems_found: tasks.append(('drums', stems_found['drums'], 'song-drums.mp3'))
            if 'other' in stems_found: tasks.append(('other', stems_found['other'], 'song-other.mp3'))

            from concurrent.futures import ThreadPoolExecutor
            workers = min(5, os.cpu_count() or 4)
            with ThreadPoolExecutor(max_workers=workers) as executor:
                futures = [executor.submit(process_stem, t[0], t[1], t[2]) for t in tasks]
                for f in futures:
                    f.result()

            # Master mix for sample preview: mix only the actual generated stems
            generated_stems = [os.path.join(out_dir, t[2]) for t in tasks if os.path.exists(os.path.join(out_dir, t[2]))]
            if len(generated_stems) > 1:
                cmd = ['-y', '-threads', '0']
                for s in generated_stems: cmd.extend(['-i', s])
                cmd.extend(['-filter_complex', f'amix=inputs={len(generated_stems)}:dropout_transition=0:normalize=0', '-b:a', '192k', os.path.join(out_dir, 'song.mp3')])
                self._run_ffmpeg(cmd)
            elif len(generated_stems) == 1:
                shutil.copy2(generated_stems[0], os.path.join(out_dir, 'song.mp3'))

        else:
            self.log("  [Audio] Standard stereo audio track detected. Converting...")
            audio_src = None
            if len(stems_found) == 1:
                single_val = list(stems_found.values())[0]
                if isinstance(single_val, str) and os.path.exists(single_val):
                    audio_src = single_val
                elif isinstance(single_val, list) and single_val and os.path.exists(single_val[0]):
                    audio_src = single_val[0]

            if not audio_src:
                for fn in ['song.opus', 'song.ogg', 'song.mp3', 'song.wav', 'guitar.opus', 'guitar.ogg', 'guitar.mp3', 'guitar.wav', 'rhythm.opus', 'rhythm.ogg', 'rhythm.mp3']:
                    p = os.path.join(src_dir, fn)
                    if os.path.exists(p):
                        audio_src = p
                        break

            if not audio_src:
                for f in os.listdir(src_dir):
                    if f.lower().endswith(('.opus', '.ogg', '.mp3', '.wav', '.flac', '.m4a')):
                        audio_src = os.path.join(src_dir, f)
                        break

            if audio_src:
                self._encode_or_copy(audio_src, os.path.join(out_dir, 'song.mp3'))
            else:
                self.log("  [WARN] Main audio track not found.")

        # Preview sample.mp3 (fast cut)
        master_p = os.path.join(out_dir, 'song.mp3')
        if os.path.exists(master_p):
            sample_dest = os.path.join(out_dir, 'sample.mp3')
            try:
                self._run_ffmpeg(['-y', '-ss', str(preview_start), '-t', '25', '-i', master_p, '-c:a', 'copy', sample_dest])
            except Exception:
                self._run_ffmpeg(['-y', '-threads', '0', '-ss', str(preview_start), '-t', '25', '-i', master_p, '-b:a', '192k', sample_dest])

        return has_multitrack

    def process_cover(self, src_dir, out_dir):
        cover_names = ['album.jpg', 'album.png', 'album.jpeg', 'cover.jpg', 'cover.png', 'cover.jpeg', 'background.jpg', 'background.png']
        for cn in cover_names:
            p = os.path.join(src_dir, cn)
            if os.path.exists(p):
                shutil.copy2(p, os.path.join(out_dir, 'album.jpg'))
                cover_dest = os.path.join(out_dir, 'cover.png')
                try:
                    from PIL import Image
                    with Image.open(p) as img:
                        img.convert('RGB').save(cover_dest, 'PNG')
                except Exception:
                    self._run_ffmpeg(['-y', '-i', p, cover_dest])
                self.log(f"  [Cover] Album art found: {cn}")
                return True
        return False

    def convert_song(self, input_path, output_parent_dir):
        input_path = os.path.abspath(input_path)
        if not os.path.exists(input_path):
            self.log(f"[ERROR] File does not exist: {input_path}")
            return False

        temp_dir_to_clean = None
        input_dir = input_path

        try:
            if os.path.isfile(input_path) and input_path.lower().endswith('.zip'):
                self.log(f"  [ZIP] Extracting '{os.path.basename(input_path)}'...")
                temp_dir_to_clean = tempfile.mkdtemp(prefix="unplugged_zip_")
                with zipfile.ZipFile(input_path, 'r') as zf:
                    zf.extractall(temp_dir_to_clean)
                input_dir = temp_dir_to_clean
                for root, dirs, files in os.walk(temp_dir_to_clean):
                    if any(f.lower().startswith(('notes.', 'song.')) for f in files):
                        input_dir = root
                        break

            elif os.path.isfile(input_path) and input_path.lower().endswith('.sng'):
                self.log(f"  [SNG] Extracting '{os.path.basename(input_path)}'...")
                temp_dir_to_clean = tempfile.mkdtemp(prefix="unplugged_sng_")
                if sng_parser is not None:
                    sng_parser.decode_sng(input_path, outdir=temp_dir_to_clean, overwrite=True)
                    input_dir = temp_dir_to_clean
                    for root, dirs, files in os.walk(temp_dir_to_clean):
                        if any(f.lower().startswith(('notes.', 'song.')) for f in files):
                            input_dir = root
                            break
                else:
                    self.log("[ERROR] Missing sng_parser library.")
                    return False

            ini_info = self.parse_ini(os.path.join(input_dir, 'song.ini'))
            folder_name = sanitize_folder_name(f"{ini_info['artist']} - {ini_info['name']}")
            out_dir = os.path.join(output_parent_dir, folder_name)
            os.makedirs(out_dir, exist_ok=True)

            self.log(f"--- Starting conversion: {ini_info['artist']} - {ini_info['name']} ---")
            self.progress_cb(0.1, "Converting audio...")

            # 1. Convert Audio
            has_multitrack = self.convert_audio(input_dir, out_dir, ini_info['preview_start'])
            if self.cancel_check():
                self.log("[Cancel] Operation cancelled by user.")
                return False
            self.progress_cb(0.5, "Processing album art...")

            # 2. Cover
            self.process_cover(input_dir, out_dir)
            if self.cancel_check():
                self.log("[Cancel] Operation cancelled by user.")
                return False
            self.progress_cb(0.7, "Generating chart note data...")

            # 3. Chart
            mid_path = os.path.join(input_dir, 'notes.mid')
            chart_path = os.path.join(input_dir, 'notes.chart')
            if not os.path.exists(chart_path):
                chart_path = os.path.join(input_dir, 'song.chart')

            if os.path.exists(mid_path):
                self.log("  [Chart] Detected notes.mid (MIDI).")
                self.convert_midi(mid_path, out_dir)
            elif os.path.exists(chart_path):
                self.log("  [Chart] Detected notes.chart (Clone Hero).")
                self.convert_chart(chart_path, out_dir)
            else:
                self.log("  [WARN] No chart file found (.mid or .chart).")

            # 4. Copy/write song.ini and data.xml
            if os.path.exists(os.path.join(input_dir, 'song.ini')):
                shutil.copy2(os.path.join(input_dir, 'song.ini'), os.path.join(out_dir, 'song.ini'))
            else:
                with open(os.path.join(out_dir, 'song.ini'), 'w', encoding='utf-8') as f:
                    f.write(f"[song]\nname = {ini_info['name']}\nartist = {ini_info['artist']}\ncharter = {ini_info['charter']}\n")

            def clean_xml_str(val):
                if not val: return ""
                v = re.sub(r'<[^>]+>', '', str(val))
                return v.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;')

            # data.xml
            xml_data = f"""<?xml version="1.0" encoding="utf-8"?>
<SongLevelData xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.datacontract.org/2004/07/">
  <BandName>{clean_xml_str(ini_info['artist'])}</BandName>
  <FileName>{folder_name}</FileName>
  <InitialDelay>{ini_info['delay']}</InitialDelay>
  <IsBuiltIn>false</IsBuiltIn>
  <IsMultiTrack>{'true' if has_multitrack else 'false'}</IsMultiTrack>
  <LevelAuthorName>{clean_xml_str(ini_info['charter'])}</LevelAuthorName>
  <SongID>CUSTOM_{folder_name}</SongID>
  <SongName>{clean_xml_str(ini_info['name'])}</SongName>
</SongLevelData>"""
            with open(os.path.join(out_dir, 'data.xml'), 'w', encoding='utf-8') as f:
                f.write(xml_data)

            self.progress_cb(1.0, "Conversion complete!")
            self.log(f"[SUCCESS] Song converted and installed: {folder_name}")
            return True

        except Exception as e:
            self.log(f"[ERROR] Error during conversion: {e}")
            import traceback
            traceback.print_exc()
            return False

        finally:
            if temp_dir_to_clean and os.path.exists(temp_dir_to_clean):
                shutil.rmtree(temp_dir_to_clean, ignore_errors=True)
