# USG (Unplugged Song Manager) & Custom Songs Mod

**USG** is an all-in-one desktop companion and modding suite for **Unplugged VR** (by Anotherway / Vertigo Games). It allows players to effortlessly convert, manage, and download custom songs from Chorus / Clone Hero directly into Unplugged VR with full hand tracking and controller support.

---

## 🎸 Features

- **In-Game Custom Song Loader (`UnpluggedCustomSongs.dll`):**
  - Native integration with Unplugged's vinyl carousel UI.
  - Multi-track stem audio mixing (Guitar, Bass, Drums, Vocals, Other/Backing).
  - Both **Hand Tracking** (gesture recognition) and **VR Controllers** (grip/trigger mapping).
  - Real-time vinyl record cover updates matching each song's artwork.
  - FileSystemWatcher hot-reload: drop new songs into `CustomSongs` and they appear in-game automatically.
  - Discord Rich Presence integration showing current song, artist, and difficulty.

- **Desktop Manager (`USG.exe`):**
  - **Local Converter:** Drag and drop `.zip`, `.sng`, `.chart`, or `.mid` files for instant automatic conversion.
  - **Song Library:** View installed songs, play 25s audio previews, filter/search by title or artist, and batch delete.
  - **Chorus Integration:** Search and download thousands of community charts with one click.
  - **Multi-threaded Audio & Image Pipeline:** Parallel FFmpeg stem transcoding and in-memory cover processing.
  - Embedded FFmpeg: No external terminal windows or messy manual installations required.

---

## 🚀 Quick Start

1. Install [MelonLoader](https://melonwiki.xyz/#/) (v0.5.7) for `Unplugged.exe`.
2. Place `UnpluggedCustomSongs.dll` inside your `Unplugged/Mods/` folder.
3. Launch `USG.exe`, drag & drop your charts or download from the Chorus tab.
4. Put on your VR headset and enjoy your custom tracks!

---

## 🛠️ Project Structure

- `core/` - Conversion engine, Chorus API client, audio stem mixing, and game detection.
- `ui/` - Modern CustomTkinter desktop interface.
- `mod/` - C# MelonLoader mod source code with Harmony runtime patches.
- `assets/` - Theme configurations and default vinyl artwork.

---

## 📜 License

MIT License. Developed for the Unplugged VR community by Luc & Antigravity.
