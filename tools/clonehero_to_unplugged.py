import os
import sys
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
from pathlib import Path

CUSTOM_SONGS_DIR = r"C:\SteamLibrary\steamapps\common\Unplugged\CustomSongs"

FINGER_MAP = {
    # Clone Hero Lane -> (Tier, [Fingers])
    0: (0, ["Index"]),
    1: (0, ["Index", "Middle"]),
    2: (1, ["Index", "Middle", "Ring"]),
    3: (2, ["Index", "Middle", "Ring", "Pinky"]),
    4: (3, ["Index", "Middle", "Ring", "Pinky"]),
    7: (0, ["Index"]) # Open note
}

CHORD_MAP = {
    # Tuple of sorted lanes -> (Tier, [Fingers])
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

def decode_ug_fingers(f_str):
    names = ["Index", "Middle", "Ring", "Pinky"]
    return [names[i] for i in range(min(4, len(f_str))) if f_str[i].upper() == 'O']

def parse_ini(ini_path):
    info = {
        'name': 'Unknown Song',
        'artist': 'Unknown Artist',
        'charter': 'Custom Charter',
        'preview_start': 30.0,
        'delay': 4.0
    }
    if not os.path.exists(ini_path):
        return info

    with open(ini_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if '=' in line and not line.startswith('['):
                k, v = line.split('=', 1)
                k = k.strip().lower()
                v = v.strip().strip('"')
                if k == 'name': info['name'] = v
                elif k == 'artist': info['artist'] = v
                elif k == 'charter': info['charter'] = v
                elif k == 'preview_start_time':
                    try: info['preview_start'] = float(v) / 1000.0
                    except: pass
                elif k == 'delay':
                    try: info['delay'] = max(2.0, float(v) / 1000.0)
                    except: pass
    return info

def parse_chart(chart_path):
    sections = {}
    current_sec = None
    
    with open(chart_path, 'r', encoding='utf-8', errors='ignore') as f:
        for line in f:
            line = line.strip()
            if line.startswith('[') and line.endswith(']'):
                current_sec = line[1:-1]
                sections[current_sec] = []
            elif current_sec and line and line != '{' and line != '}':
                sections[current_sec].append(line)
                
    return sections

def process_sync_track(sync_lines, resolution=192):
    bpm_markers = []
    
    for l in sync_lines:
        m = re.match(r'(\d+)\s*=\s*B\s*(\d+)', l)
        if m:
            tick = int(m.group(1))
            bpm = float(m.group(2)) / 1000.0
            bpm_markers.append((tick, bpm))
            
    if not bpm_markers:
        bpm_markers = [(0, 120.0)]
        
    bpm_markers.sort(key=lambda x: x[0])
    
    timed_bpms = []
    curr_time = 0.0
    for i in range(len(bpm_markers)):
        tick, bpm = bpm_markers[i]
        if i == 0:
            timed_bpms.append((tick, 0.0, bpm))
        else:
            prev_tick, prev_time, prev_bpm = timed_bpms[i-1]
            dt = (tick - prev_tick) * (60.0 / (prev_bpm * resolution))
            curr_time = prev_time + dt
            timed_bpms.append((tick, curr_time, bpm))
            
    def tick_to_seconds(tick):
        for i in range(len(timed_bpms) - 1, -1, -1):
            btick, btime, bbpm = timed_bpms[i]
            if tick >= btick:
                return btime + (tick - btick) * (60.0 / (bbpm * resolution))
        return 0.0

    return timed_bpms, tick_to_seconds

def parse_notes_from_section(lines):
    # Detect if UG format or standard N format
    notes_by_tick = {}
    for l in lines:
        # Check UG format: <tick> = UG <tier> <sustain> <fingers> <type> <cursed> <blind>
        m_ug = re.match(r'(\d+)\s*=\s*UG\s+(\d+)\s+(\d+)\s+([OXox]{4})', l)
        if m_ug:
            tick = int(m_ug.group(1))
            tier = int(m_ug.group(2))
            dur = int(m_ug.group(3))
            fingers = decode_ug_fingers(m_ug.group(4))
            if tick not in notes_by_tick:
                notes_by_tick[tick] = []
            notes_by_tick[tick].append(('UG', tier, dur, fingers))
            continue
            
        # Check standard Clone Hero format: <tick> = N <lane> <duration>
        m_n = re.match(r'(\d+)\s*=\s*N\s+(\d+)\s+(\d+)', l)
        if m_n:
            tick = int(m_n.group(1))
            lane = int(m_n.group(2))
            dur = int(m_n.group(3))
            if tick not in notes_by_tick:
                notes_by_tick[tick] = []
            notes_by_tick[tick].append(('N', lane, dur))
            
    return notes_by_tick

def build_song_xml(song_name, artist, timed_bpms, tick_to_time, notes_by_tick, resolution=192):
    root = ET.Element("SongLevel", {
        "xmlns": "http://schemas.datacontract.org/2004/07/",
        "xmlns:i": "http://www.w3.org/2001/XMLSchema-instance"
    })
    
    initial_bpm = int(timed_bpms[0][2]) if timed_bpms else 120
    ET.SubElement(root, "BPM").text = str(initial_bpm)
    
    bpm_grid = ET.SubElement(root, "BPMGrid")
    for tick, time_sec, bpm_val in timed_bpms:
        bpm_key = ET.SubElement(bpm_grid, "BPMKey")
        ET.SubElement(bpm_key, "Duration").text = "0"
        ET.SubElement(bpm_key, "Time").text = f"{time_sec:.6f}"
        ET.SubElement(bpm_key, "BPM").text = f"{bpm_val:.3f}"
        
    ET.SubElement(root, "BeatGrid", {"i:nil": "true"})
    ET.SubElement(root, "BeatGridFromFile").text = "false"
    ET.SubElement(root, "EditorBPM").text = "0"
    
    keys_elem = ET.SubElement(root, "Keys")
    key_time = ET.SubElement(keys_elem, "KeyTime", {"i:type": "GuitarGame"})
    
    sorted_ticks = sorted(notes_by_tick.keys())
    last_tick = sorted_ticks[-1] if sorted_ticks else 0
    total_dur = tick_to_time(last_tick) + 10.0
    
    ET.SubElement(key_time, "Duration").text = f"{total_dur:.6f}"
    ET.SubElement(key_time, "Time").text = "0"
    ET.SubElement(key_time, "PrewarmTime").text = "4"
    
    notes_elem = ET.SubElement(key_time, "Notes")
    
    prev_time = -999.0
    prev_tier = -1
    
    for tick in sorted_ticks:
        entries = notes_by_tick[tick]
        note_time = tick_to_time(tick)
        
        # Check if UG or standard
        if entries[0][0] == 'UG':
            _, tier, dur_ticks, fingers = entries[0]
            dur_sec = (tick_to_time(tick + dur_ticks) - note_time) if dur_ticks > (resolution // 4) else 0.0
        else:
            # Standard Clone Hero lane mapping
            lanes = [e[1] for e in entries]
            dur_ticks = max(e[2] for e in entries)
            dur_sec = (tick_to_time(tick + dur_ticks) - note_time) if dur_ticks > (resolution // 4) else 0.0
            
            sorted_lanes = tuple(sorted(set(l for l in lanes if l in [0, 1, 2, 3, 4, 7])))
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
        
        tier_note_attrib = {"i:type": note_type} if note_type else {}
        tier_note = ET.SubElement(notes_elem, "TierNote", tier_note_attrib)
        
        ET.SubElement(tier_note, "Duration").text = f"{dur_sec:.6f}"
        ET.SubElement(tier_note, "Time").text = f"{note_time:.6f}"
        ET.SubElement(tier_note, "Tier").text = str(tier)
        ET.SubElement(tier_note, "CursableProbability").text = "0"
        ET.SubElement(tier_note, "IsBlind").text = "false"
        
        fingers_elem = ET.SubElement(tier_note, "Fingers")
        for f in fingers:
            ET.SubElement(fingers_elem, "TierFinger").text = f
            
        prev_time = note_time
        prev_tier = tier
        
    return root

def convert_audio(src_folder, dst_folder, preview_start=30.0):
    candidates = ["song.ogg", "song.mp3", "guitar.ogg", "notes.ogg", "song.wav", "song.flac"]
    main_audio = None
    for c in candidates:
        p = os.path.join(src_folder, c)
        if os.path.exists(p):
            main_audio = p
            break
            
    if not main_audio:
        for f in os.listdir(src_folder):
            if f.endswith(('.ogg', '.mp3', '.wav', '.flac')) and not f.startswith('sample'):
                main_audio = os.path.join(src_folder, f)
                break
                
    if not main_audio:
        print("  [Warning] No audio file found!")
        return False
        
    dst_song_mp3 = os.path.join(dst_folder, "song.mp3")
    dst_sample_mp3 = os.path.join(dst_folder, "sample.mp3")
    
    if main_audio.endswith(".mp3"):
        shutil.copy2(main_audio, dst_song_mp3)
    else:
        print(f"  Converting {os.path.basename(main_audio)} to MP3...")
        cmd = ["ffmpeg", "-y", "-i", main_audio, "-b:a", "192k", dst_song_mp3]
        subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
        
    print(f"  Generating sample preview at {preview_start}s...")
    cmd_sample = ["ffmpeg", "-y", "-ss", str(preview_start), "-t", "30", "-i", dst_song_mp3, "-b:a", "192k", dst_sample_mp3]
    subprocess.run(cmd_sample, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    
    stem_map = {
        'guitar': 'song-guitar.mp3',
        'bass': 'song-bass.mp3',
        'drums': 'song-drums.mp3',
        'vocals': 'song-vocals.mp3',
        'song': 'song-other.mp3'
    }
    for stem_name, out_name in stem_map.items():
        for ext in ['.ogg', '.mp3', '.wav']:
            stem_p = os.path.join(src_folder, f"{stem_name}{ext}")
            if os.path.exists(stem_p):
                out_p = os.path.join(dst_folder, out_name)
                if ext == '.mp3':
                    shutil.copy2(stem_p, out_p)
                else:
                    cmd_stem = ["ffmpeg", "-y", "-i", stem_p, "-b:a", "192k", out_p]
                    subprocess.run(cmd_stem, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                break
                
    return True

def convert_clone_hero_song(src_folder, output_base=CUSTOM_SONGS_DIR):
    print(f"\n==========================================")
    print(f"Processing: {src_folder}")
    print(f"==========================================")
    
    ini_path = os.path.join(src_folder, "song.ini")
    info = parse_ini(ini_path)
    
    safe_folder_name = re.sub(r'[\\/*?:"<>|]', "", f"{info['artist']} - {info['name']}")
    dst_folder = os.path.join(output_base, safe_folder_name)
    os.makedirs(dst_folder, exist_ok=True)
    
    # 1. Check if songdata XMLs already exist in source
    xml_files = ["songdata-Normal.xml", "songdata-Hard.xml", "songdata-Easy.xml", "songdata-SuperEasy.xml"]
    copied_xmls = 0
    for xf in xml_files:
        src_xml = os.path.join(src_folder, xf)
        if os.path.exists(src_xml):
            shutil.copy2(src_xml, os.path.join(dst_folder, xf))
            copied_xmls += 1
            print(f"  Copied existing {xf}")
            
    # 2. If no XMLs, generate from chart
    if copied_xmls == 0:
        chart_path = os.path.join(src_folder, "notes.chart")
        if not os.path.exists(chart_path):
            chart_path = os.path.join(src_folder, "song.chart")
            
        if os.path.exists(chart_path):
            sections = parse_chart(chart_path)
            resolution = 192
            if 'Song' in sections:
                for l in sections['Song']:
                    m = re.match(r'Resolution\s*=\s*(\d+)', l)
                    if m: resolution = int(m.group(1))
                    
            if 'SyncTrack' in sections:
                timed_bpms, tick_to_time = process_sync_track(sections['SyncTrack'], resolution)
                
                diff_map = {
                    'songdata-Hard.xml': ['ExpertSingle', 'HardSingle'],
                    'songdata-Normal.xml': ['HardSingle', 'MediumSingle', 'ExpertSingle'],
                    'songdata-Easy.xml': ['MediumSingle', 'EasySingle']
                }
                
                for xml_filename, section_preferences in diff_map.items():
                    found_section = None
                    for sec in section_preferences:
                        if sec in sections and len(sections[sec]) > 0:
                            found_section = sec
                            break
                            
                    if found_section:
                        notes = parse_notes_from_section(sections[found_section])
                        if notes:
                            xml_root = build_song_xml(info['name'], info['artist'], timed_bpms, tick_to_time, notes, resolution)
                            xml_str = ET.tostring(xml_root, encoding='utf-8')
                            with open(os.path.join(dst_folder, xml_filename), 'wb') as f:
                                f.write(xml_str)
                            print(f"  Generated {xml_filename} from [{found_section}] ({len(notes)} notes)")
        else:
            print(f"  [Warning] No chart file found in {src_folder}!")

    # 3. Audio conversion
    convert_audio(src_folder, dst_folder, info['preview_start'])
    
    # 4. Cover
    cover_candidates = ["album.png", "album.jpg", "cover.png", "cover.jpg", "label.png"]
    for c in cover_candidates:
        cp = os.path.join(src_folder, c)
        if os.path.exists(cp):
            shutil.copy2(cp, os.path.join(dst_folder, "cover.png"))
            break
            
    # 5. Metadata
    with open(os.path.join(dst_folder, "song.ini"), 'w', encoding='utf-8') as f:
        f.write("[Song]\n")
        f.write(f"name = {info['name']}\n")
        f.write(f"artist = {info['artist']}\n")
        f.write(f"charter = {info['charter']}\n")
        
    is_multi = os.path.exists(os.path.join(dst_folder, "song-guitar.mp3"))
    with open(os.path.join(dst_folder, "data.xml"), 'w', encoding='utf-8') as f:
        f.write(f'''<SongLevelData xmlns="http://schemas.datacontract.org/2004/07/">
  <BandName>{info['artist']}</BandName>
  <FileName>{safe_folder_name}</FileName>
  <InitialDelay>{info['delay']}</InitialDelay>
  <IsMultiTrack>{str(is_multi).lower()}</IsMultiTrack>
  <LevelAuthorName>{info['charter']}</LevelAuthorName>
  <LevelID>CUSTOM_{safe_folder_name}</LevelID>
  <SongName>{info['name']}</SongName>
</SongLevelData>''')

    print(f"  [SUCCESS] Created Unplugged custom song at:\n  {dst_folder}")
    return True

if __name__ == "__main__":
    if len(sys.argv) > 1:
        for arg in sys.argv[1:]:
            if os.path.isdir(arg):
                convert_clone_hero_song(arg)
            else:
                print(f"Not a directory: {arg}")
    else:
        print("Usage: python clonehero_to_unplugged.py <path_to_clone_hero_song_folder>")
