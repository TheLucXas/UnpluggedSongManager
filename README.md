# 🎸 USM · Unplugged Song Manager

**Custom songs for _Unplugged: Air Guitar_ on Windows PC and Meta Quest.** Search [Chorus Encore](https://www.enchor.us/), convert compatible charts into compact `.usm` archives, manage your library, and install the companion mod from one Windows app.

> [!IMPORTANT]
> USM is a community project, independent of the game and the services it uses. You need your own copy of _Unplugged_ and the right to use any charts or audio you import. The repository and releases do not include the game, commercial songs, or Quest APKs.
>
> This project was developed with generative AI and may contain bugs. Please report problems in [GitHub Issues](https://github.com/TheLucXas/UnpluggedSongManager/issues).

## ✨ What's included?

| Component | Purpose |
| --- | --- |
| **USM** | Windows manager for discovery, conversion, libraries, mod installation, Quest patching, and updates. |
| **PC mod** | Adds a Custom Songs album to the PC game through MelonLoader. |
| **Quest mod** | Adds Custom Songs to a separate, LemonLoader-patched Quest installation. |
| **`.usm` format** | Portable song archive with compressed OGG audio, JPG cover, preview, metadata, and difficulty charts for hands and controllers. |

### 🔎 Discover and convert

- Search or browse Chorus Encore with sorting, difficulty and status filters, paginated results, and progressively displayed matches.
- Filter by **Single Track**, **Half Track**, **Multi Track**, and chart quality. Audio type is checked from the song package: instrument badges alone do not prove that separate audio stems exist.
- Highlight **Ideal for Unplugged** charts when guitar chart coverage, difficulties, notes, and stems meet USM's criteria. This is a compatibility hint, not a guarantee that every chart was authored perfectly.
- Download compatible `.sng` charts, or import local `.sng`, `.zip`, and chart folders with `notes.mid` or `notes.chart`.
- Keep OGG audio compact, process covers and previews, and omit video backgrounds from `.usm` output.
- Package separate difficulty data for hand tracking and controllers. Use authored difficulties when present and adapt missing ones.
- Warn about incomplete charts before keeping a download. Store Chorus chart IDs so **Resync Charts** can refresh chart data after parser changes without needlessly reprocessing audio.

![USM Chorus search screen](docs/images/chorus-search.png)

### 📚 Manage your library

- Browse `.usm` songs with covers, previews, search, selection, bulk removal, audio type, and **Ideal** labels.
- See charts that need a parser resync and refresh them from their source chart.
- When a Quest is connected, list and remove its installed songs in a separate **Quest Songs** tab, including select all.
- Sync changed `.usm` files to Quest through ADB. Quest songs live at `/sdcard/ModData/com.Anotherway.UnpluggedVR.modded/Songs`.

![USM song library](docs/images/song-library.png)

### 🎮 Play on PC and Quest

- Custom Songs album with covers, song previews, difficulty selection, and audio type / **Ideal** labels.
- Hand tracking and controller charts, prepared in the archive rather than converted during play.
- **Single Track** = one mixed audio track; **Half Track** = backing/instrumental plus vocals; **Multi Track** = multiple separate stems. The mod uses available stems for its in-game audio behavior.
- Skip a long silent introduction to shortly before the first note: **A + X** with controllers or the in-game button with hands.
- Navigate songs with controller sticks, or use the hand-tracked carousel gesture and side buttons.
- Filter installed songs in the Custom Songs album by title or artist with an in-game keyboard, then show only matching songs in the carousel. The PC build also accepts a physical keyboard.
- On the `develop` branch, open **CHORUS** from the Custom Songs album to search and page through charts, download one, parse it with USM's shared chart logic, and add its `.usm` to the album without opening the Windows app. PC imports use the normal FFmpeg audio pipeline. Quest imports currently accept charts with directly playable OGG Vorbis or MP3 audio and no stem mixing; unsupported audio is reported before installation.
- Optional PC Discord Rich Presence and verbose diagnostics.

![Custom Songs album with a converted song selected](docs/images/custom-songs-album.png)

![In-game song selection with difficulty and track information](docs/images/song-selection.png)

### 🛠️ Install and update

- Install, update, reinstall, or remove the PC mod from USM. USM can also install or remove MelonLoader for the PC game.
- Patch a **separate Quest app** named `Unplugged Modded`, preserving the original installation. USM backs up existing modded-app saves while patching and can optionally import compatible PC progress.
- Once the Quest app is patched, update or reinstall **just the Quest mod DLL** without rebuilding the APK or retransferring songs.
- Connect through USB ADB or wireless ADB; check connected devices and installed mod status in the app.
- Get update notices for USM and both mods from GitHub Releases when releases are available.
- Choose among 16 interface languages. FFmpeg is downloaded when needed instead of being bundled inside `USM.exe`.

## ✅ Requirements

| Use | What you need |
| --- | --- |
| **USM on Windows** | A 64-bit Windows PC and the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Internet access is needed for Chorus searches, updates, and the first FFmpeg download if you convert songs. |
| **Play on PC** | Your own [Unplugged for PC](https://store.steampowered.com/app/1623390/Unplugged/), a VR setup supported by the game ([SteamVR or OpenXR](https://store.steampowered.com/app/1623390/Unplugged/)), and [MelonLoader 0.5.7](https://github.com/LavaGang/MelonLoader/releases/tag/v0.5.7). USM can install this MelonLoader version for you. |
| **Play on Quest** | Your own original _Unplugged_ installed on the headset, [Quest developer mode](https://developers.meta.com/vr/documentation/android-apps/enable-developer-mode/), and an authorized ADB connection via a data-capable USB cable or configured Wi-Fi. Install [Android SDK Platform Tools (ADB)](https://developer.android.com/tools/releases/platform-tools) and a [Java JDK](https://adoptium.net/temurin/releases/?version=17) on the Windows PC used for patching. USM obtains the other patching tools when needed. |

## 🚀 Install from a release

Download the ready-to-run USM app and separate PC/Quest mod DLLs from [GitHub Releases](https://github.com/TheLucXas/UnpluggedSongManager/releases). Each release provides the three builds; the Windows app still requires the .NET 8 Desktop Runtime listed above.

### Windows PC · through USM

1. Install your own copy of _Unplugged_ on PC and run USM on Windows with the **.NET 8 Desktop Runtime**.
2. In **Settings**, select the game folder if it was not detected automatically.
3. Install **MelonLoader**, then install the **PC mod** from USM.
4. Use **Download (Chorus)** or **Local Converter** to add songs. USM stores them in the game's `CustomSongs` folder.
5. Start the game and select **Custom Songs**.

### Meta Quest · through USM

1. Install your own original _Unplugged_ game on the headset. Enable developer mode and connect it through ADB.
2. Open **Quest Patcher** in USM. Select the headset and patch the cloned **Unplugged Modded** app. The first patch requires Quest tools and Java.
3. In **Song Library** or **Quest Patcher**, sync your `.usm` songs to the connected headset.
4. Launch **Unplugged Modded**. For later mod changes, use **Update/Reinstall Quest Mod**; a full APK patch is unnecessary.

> [!NOTE]
> Keep the signing key in USM's local Quest signing folder when updating the cloned app. A different APK signature can prevent an in-place update. The Quest mod, songs, and the original game are separate.

## 🧰 Manual installation

USM is a convenience tool; the loaders and mod DLLs can also be installed manually.

| Platform | Manual steps |
| --- | --- |
| **PC** | Install a compatible [MelonLoader](https://github.com/LavaGang/MelonLoader) release in the PC game folder. Place the PC build as `UnpluggedCustomSongs.dll` in `<Unplugged>/Mods/` and `.usm` songs in `<Unplugged>/CustomSongs/`. The current project builds against MelonLoader 0.5.7. |
| **Quest** | Patch a separate `com.Anotherway.UnpluggedVR.modded` installation with a compatible [LemonLoader installer](https://github.com/LemonLoader/MelonLoaderInstaller). Put `UnpluggedCustomSongs.Quest.dll` in the **Mods directory actually reported by LemonLoader** (commonly `/sdcard/MelonLoader/com.Anotherway.UnpluggedVR.modded/Mods/`). Put `.usm` songs in `/sdcard/ModData/com.Anotherway.UnpluggedVR.modded/Songs/`. Keep the same APK signing key for future updates. |

Manual Quest setup also needs a legally obtained APK, its matching game data, ADB, Java, and appropriate patching/signing tools. USM handles those steps for the cloned app. Do not put `.usm` songs in `/sdcard/Unplugged/Songs`.

## 🏗️ Build from source

### Requirements

- **Windows**, the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and PowerShell.
- A locally installed PC copy of _Unplugged_ with **MelonLoader 0.5.7** for PC mod references.
- Quest IL2CPP interop assemblies generated from your own Quest installation, plus matching Quest MelonLoader reference assemblies. These proprietary/generated DLLs are **not** in Git.
- For Quest patching at runtime: **Java**, **ADB**, Apktool, uber-apk-signer, and LemonLoader patching assets. The two JAR tools are under `tools/quest/`; USM can obtain a compatible Quest tool bundle into its local cache. FFmpeg is acquired separately on first use.

Place Quest references under `tools/quest/Il2CppAssemblies/` and `tools/quest/MelonNet/net8/`, or pass another parent folder with `-QuestReferencesDir`. Then run from the repository root:

```powershell
./tools/Build-USM.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Unplugged'
```

For references stored elsewhere:

```powershell
./tools/Build-USM.ps1 -GameDir 'D:\Games\Unplugged' -QuestReferencesDir 'D:\QuestBuildReferences'
```

`-GameDir` can be omitted if `UNPLUGGED_GAME_DIR` is set, USM has saved the game location, or the script finds a standard Steam library. The script builds both mod variants and publishes `dist/USM-CSharp/USM.exe`, replacing the previous build in that folder. It also produces `UnpluggedCustomSongs.PC.dll` and `UnpluggedCustomSongs.Quest.dll` separately. Both mods are embedded in the app for installation through USM.

```text
src/USM/                    Windows app, conversion, library, Quest patcher
mod/UnpluggedCustomSongs/  PC and Quest mod source
tools/Build-USM.ps1         Local build entry point
tools/quest/               Quest patching tools and local references
tests/                     Optional development checks
```

Local game files, Quest references, saves, captures, APKs, credentials, and device logs must stay outside Git. Never add private tokens or account data to the repository.

## 🗺️ Roadmap

- Complete the Quest in-game audio conversion path for Chorus charts with Opus, FLAC, WAV, M4A, or stems that need mixing, and refine the VR search interface after device testing.
- Create and edit charts specifically for _Unplugged_, including hand and controller difficulties.
- Edit song titles, artists, covers, and other `.usm` metadata without reconverting audio.
- Create playlists and mark favorite songs in the library and game.
- Compare PC and Quest libraries and sync only missing or changed songs.
- Add a practice mode with selectable song sections and playback speed.

## 📜 License and third-party credits

**USM's original source code is released under [The Unlicense](LICENSE).** You may use, modify, distribute, or sell that original code without attributing USM. This does **not** change the licenses of third-party code, fonts, tools, the game, or user-provided music and charts; their own notices and terms still apply. See [third-party notices](THIRD_PARTY_NOTICES.md) for redistribution details.

| Project | Role / credit |
| --- | --- |
| [Unplugged: Air Guitar](https://www.vertigo-games.com/games/unplugged-vr/) · Anotherway / Vertigo Games | Original game and its assets; not included here. |
| [Chorus Encore](https://www.enchor.us/) and the individual chart authors | Chart discovery and community-created charts. A listing does not grant rights to redistribute its audio. |
| [Clone Hero](https://clonehero.net/) | Supported chart ecosystem and file formats. |
| [MelonLoader](https://github.com/LavaGang/MelonLoader), [LemonLoader installer](https://github.com/LemonLoader/MelonLoaderInstaller), [HarmonyX](https://github.com/BepInEx/HarmonyX), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | PC/Quest loader and runtime patching ecosystem. Their separate licenses apply; the LemonLoader installer includes GPL-licensed components. |
| [Apktool](https://github.com/iBotPeaches/Apktool), [uber-apk-signer](https://github.com/patrickfav/uber-apk-signer), [Android Platform Tools](https://developer.android.com/tools/releases/platform-tools) | Quest APK processing, signing, and ADB. The bundled JARs remain under their own Apache-2.0 licenses. |
| [FFmpeg](https://ffmpeg.org/) and [Gyan's Windows builds](https://www.gyan.dev/ffmpeg/builds/) | Audio conversion; downloaded separately. FFmpeg's applicable license depends on its build configuration. |
| [SimpleJSON](https://github.com/Bunny83/SimpleJSON) · Markus Göbel | JSON parser source in the mod, under MIT; its copyright and permission notice remain in the source file. |
| [Glory font](https://fonts.google.com/specimen/Glory) | UI font under SIL Open Font License 1.1; its `OFL.txt` is included beside the font files. |
| [PyQt-Fluent-Widgets](https://github.com/zhiyiYo/PyQt-Fluent-Widgets) · zhiyiYo | Visual design inspiration. USM's active interface is WPF and does not bundle the PyQt widget library. |

USM is an independent modding project. All product names and trademarks belong to their respective owners.
