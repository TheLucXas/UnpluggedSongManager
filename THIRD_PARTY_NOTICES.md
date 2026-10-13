# Third-party notices

USM's `LICENSE` applies only to code and assets created for this project. The following components retain their own licenses and notices. Check the exact versions included in any release bundle before redistribution.

## Files in this source tree

| File or component | Upstream | License / notice |
| --- | --- | --- |
| `tools/quest/apktool.jar` | [Apktool](https://github.com/iBotPeaches/Apktool) | Apache-2.0; see [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt). |
| `tools/quest/uber-apk-signer.jar` | [uber-apk-signer](https://github.com/patrickfav/uber-apk-signer) | Apache-2.0; see [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt). |
| `mod/UnpluggedCustomSongs/SimpleJSON.cs` | [SimpleJSON](https://github.com/Bunny83/SimpleJSON), Markus Göbel | MIT; see [`LICENSES/SimpleJSON-MIT.txt`](LICENSES/SimpleJSON-MIT.txt). The complete notice is also preserved at the top of the source file. |
| `src/USM/assets/fonts/Glory-*.ttf` | [Glory](https://fonts.google.com/specimen/Glory), The Glory Project Authors | SIL Open Font License 1.1; see [`src/USM/assets/fonts/OFL.txt`](src/USM/assets/fonts/OFL.txt). |

## External or optional components

- [MelonLoader](https://github.com/LavaGang/MelonLoader) provides the PC mod loader. It is licensed under Apache-2.0 and includes components under other licenses; consult its own `LICENSE` and `NOTICE`.
- [LemonLoader's installer](https://github.com/LemonLoader/MelonLoaderInstaller) provides the Quest patching engine. Its installer repository is GPL-3.0. `tools/lemon_patcher_src/` is a small wrapper around the installer core. If an installer build is distributed with USM, its GPL obligations, corresponding source, and upstream notices must accompany that release.
- [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop), [HarmonyX](https://github.com/BepInEx/HarmonyX), Unity/game assemblies, Java, and Android Platform Tools are external build or runtime dependencies with their own terms. Game and generated Quest assemblies are not committed here.
- [FFmpeg](https://ffmpeg.org/) is downloaded separately for audio conversion. Its license depends on the downloaded build and configuration; review that build's notices before bundling it in a release.
- [PyQt-Fluent-Widgets](https://github.com/zhiyiYo/PyQt-Fluent-Widgets) informed the visual design. Its GPL-licensed library is not included in the active WPF application.

_Unplugged: Air Guitar_ and its assets belong to Anotherway / Vertigo Games. Charts and song recordings belong to their respective authors and rights holders. USM does not grant redistribution rights for them.
