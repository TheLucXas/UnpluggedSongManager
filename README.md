# USM · Unplugged Song Manager

USM converts Clone Hero/Chorus charts into `.usm` songs, manages the PC and Quest libraries, and installs the companion mods for Unplugged. The current desktop application is the C# WPF project in `src/USM/`. The Python application in `app.py`, `core/`, and `ui/` is legacy source.

## Build the complete Windows application

You need Windows, the .NET 8 SDK, an installed copy of Unplugged for PC, and MelonLoader 0.5.7 installed in that game folder. The PC mod compiles against assemblies from your own game installation; they are intentionally not included in Git.

The Quest mod also needs the locally generated IL2CPP interop assemblies from your own Quest installation and the matching MelonLoader reference assemblies. Place them under `tools/quest/Il2CppAssemblies/` and `tools/quest/MelonNet/net8/`, respectively, or pass their parent folder with `-QuestReferencesDir`. These generated DLLs are ignored by Git.

From the repository root, run:

```powershell
./tools/Build-USM.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Unplugged'
```

`-GameDir` may be omitted when `UNPLUGGED_GAME_DIR` is set, USM has saved the game location, or the script finds a standard Steam library. The script checks the required references before compiling both mods and publishing the WPF application to `dist/USM-CSharp/USM.exe`. It replaces the previous output in that same folder.

For Quest references stored elsewhere:

```powershell
./tools/Build-USM.ps1 -GameDir 'D:\Games\Unplugged' -QuestReferencesDir 'D:\QuestBuildReferences'
```

The `apktool.jar` and `uber-apk-signer.jar` files in `tools/quest/` are **runtime Quest patching tools**. They are not compiler references. The patcher can use them from the source checkout, or download its tool bundle into USM's local cache when a compatible bundle is available. FFmpeg is downloaded on first use and is not embedded in the application.

## Source layout

- `src/USM/` — WPF desktop application, song converter, library, and Quest patcher.
- `mod/UnpluggedCustomSongs/` — PC and Quest mod projects.
- `tools/` — build helper and Quest patching utilities.
- `tests/` — optional development checks using local chart fixtures.

Local game files, generated Quest assemblies, saves, device logs, APKs, and credentials must stay outside Git. `.gitignore` excludes them. Never add an API key or personal account data to the repository.
