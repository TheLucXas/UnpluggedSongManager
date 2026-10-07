using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace USM.Core.Services.Localization
{
    public class LanguageItem
    {
        public string Code { get; set; } = "en";
        public string DisplayName { get; set; } = "English";

        public override string ToString() => DisplayName;
    }

    public class LocalizationService : INotifyPropertyChanged
    {
        private static LocalizationService? _instance;
        public static LocalizationService Instance => _instance ??= new LocalizationService();

        public static readonly List<LanguageItem> SupportedLanguages = new()
        {
            new LanguageItem { Code = "en", DisplayName = "English (Default)" },
            new LanguageItem { Code = "es", DisplayName = "Español" },
            new LanguageItem { Code = "pt", DisplayName = "Português" }
        };

        private string _currentLanguage = "en";
        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    OnPropertyChanged();
                    UpdateApplicationResources();
                    LanguageChanged?.Invoke(value);
                }
            }
        }

        public event Action<string>? LanguageChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        private LocalizationService()
        {
            _currentLanguage = "en";
        }

        public void Initialize(string? savedLanguage)
        {
            if (!string.IsNullOrEmpty(savedLanguage) && (savedLanguage == "es" || savedLanguage == "en" || savedLanguage == "pt"))
            {
                _currentLanguage = savedLanguage;
            }
            else
            {
                _currentLanguage = "en"; // Default is English
            }
            UpdateApplicationResources();
        }

        public static string T(string key, params object[] args)
        {
            return Instance.GetString(key, args);
        }

        public string GetString(string key, params object[] args)
        {
            if (!_translations.TryGetValue(_currentLanguage, out var dict) || !dict.TryGetValue(key, out var text))
            {
                if (!_translations["en"].TryGetValue(key, out text))
                {
                    text = key;
                }
            }

            if (args != null && args.Length > 0)
            {
                try
                {
                    return string.Format(text, args);
                }
                catch
                {
                    return text;
                }
            }
            return text;
        }

        public void UpdateApplicationResources()
        {
            if (Application.Current == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                var enDict = _translations["en"];
                _translations.TryGetValue(_currentLanguage, out var curDict);

                foreach (var kvp in enDict)
                {
                    string resKey = "Loc_" + kvp.Key;
                    string val = (curDict != null && curDict.TryGetValue(kvp.Key, out var localized)) ? localized : kvp.Value;
                    Application.Current.Resources[resKey] = val;
                }
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static readonly Dictionary<string, Dictionary<string, string>> _translations = new()
        {
            ["en"] = new Dictionary<string, string>
            {
                // Top Navbar & Tabs
                ["App_Title"] = "USM • UNPLUGGED SONG MANAGER",
                ["Nav_LaunchGame"] = "🚀 Launch Unplugged",
                ["Nav_LaunchGame_Tooltip"] = "Launch Unplugged VR via Steam",
                ["Nav_GameConnected"] = "Game Connected",
                ["Nav_GameNotDetected"] = "Game Not Detected",
                ["Tab_Download"] = "🔍 Download (Chorus)",
                ["Tab_Converter"] = "⚡ Local Converter",
                ["Tab_Library"] = "📚 Song Library",
                ["Tab_QuestPatcher"] = "🥽 Quest Patcher",
                ["Tab_Settings"] = "⚙ Settings",
                ["Toast_Ready"] = "Ready. Search or drop chart files to add songs to your VR library.",

                // Quest Tab - Headset ADB Connection
                ["Quest_ConnectionTitle"] = "Meta Quest ADB Connection",
                ["Quest_RefreshDevices"] = "🔄 Refresh Devices",
                ["Quest_DetectedHeadset"] = "Detected Headset (USB / Wi-Fi)",
                ["Quest_SelectHeadsetHint"] = "Select a headset from the list:",
                ["Quest_WirelessTitle"] = "Wireless Connection (Wi-Fi ADB)",
                ["Quest_IpLabel"] = "Headset IP Address",
                ["Quest_PortLabel"] = "Port",
                ["Quest_ConnectWifi"] = "📡 Connect Wi-Fi",
                ["Quest_IpTooltip"] = "Meta Quest local IP address (e.g. 192.168.1.15)",
                ["Quest_PortTooltip"] = "ADB Port (Default: 5555)",
                ["Quest_StatusUnauthorized"] = "⚠ Headset unauthorized: Please put on your Quest and click 'Allow USB Debugging'.",
                ["Quest_StatusNoDevice"] = "No Quest device found. Connect via USB or enter your Quest's IP address.",
                ["Quest_StatusChecking"] = "Checking Unplugged packages on Quest...",
                ["Quest_StockFound"] = "✔ Stock Unplugged detected (com.Anotherway.UnpluggedVR)",
                ["Quest_StockNotFound"] = "❌ Stock Unplugged not found",
                ["Quest_ModdedFound"] = "✔ Modded Unplugged installed (com.Anotherway.UnpluggedVR.modded)",
                ["Quest_ModdedNotFound"] = "⚪ Modded Unplugged not installed yet",

                // Quest Tab - Options
                ["Quest_SourceConfigTitle"] = "Source APK & Mod Configuration",
                ["Quest_UseCustomApk"] = "Use custom local APK file (instead of pulling directly from Quest)",
                ["Quest_BrowseApk"] = "📂 Browse APK...",
                ["Quest_SkipTutorialTitle"] = "⚡ Skip Calibration Tutorial & Unlock Campaign (Recommended)",
                ["Quest_SkipTutorialDesc"] = "Starts directly in the studio without having to play the 15-minute hand calibration tutorial. Unlocks all base venues and campaign albums (official DLCs remain untouched).",

                // Quest Tab - Actions
                ["Quest_StandaloneModderTitle"] = "⚡ Quest Standalone Modder",
                ["Quest_PatchButton_Install"] = "🚀 Patch & Deploy 'Unplugged Modded'",
                ["Quest_PatchDesc_Install"] = "Extracts the game from your Quest, clones it into a separate app ('Unplugged Modded') with custom song storage support and tutorial skip, and installs it alongside your stock game.",
                ["Quest_PatchButton_Update"] = "🔄 Update 'Unplugged Modded'",
                ["Quest_PatchDesc_Update"] = "An official game update was detected (v{0} vs Mod v{1}). Re-patch to keep the modded version up to date.",
                ["Quest_PatchButton_Reinstall"] = "🔨 Reinstall / Repair 'Unplugged Modded'",
                ["Quest_PatchDesc_Reinstall"] = "The modded app is already installed on your headset{0}. You can reinstall or repair it to restore files or settings.",
                ["Quest_UninstallButton"] = "🗑 Uninstall 'Unplugged Modded'",

                // Quest Tab - Song Sync
                ["Quest_CustomSongsSyncTitle"] = "🎵 Custom Songs Transfer",
                ["Quest_CustomSongsSyncDesc"] = "Pushes all custom songs from your USM library directly to your Quest storage (/sdcard/Unplugged/Songs) ready to play.",
                ["Quest_SyncSongsButton"] = "📂 Sync Songs to Quest",

                // Quest Tab - Console Log
                ["Quest_ConsoleLogTitle"] = "ADB & Patcher Console Log",
                ["Quest_ClearLogs"] = "Clear Logs",

                // Quest Dialogs & Logs
                ["Quest_Dialog_SelectHeadsetWarning"] = "Please connect a Quest device or select a valid local APK.",
                ["Quest_Dialog_ConfirmPatchTitle"] = "Confirm Quest Patching",
                ["Quest_Dialog_ConfirmPatchMsg"] = "This will {0} Unplugged: Air Guitar into a standalone modded app ('Unplugged Modded') coexisting alongside your stock game, with custom songs support{1}.\n\nDo you want to proceed?",
                ["Quest_Dialog_PatchCompleteTitle"] = "Quest Modding Complete",
                ["Quest_Dialog_PatchCompleteMsg"] = "'Unplugged Modded' has been successfully created and installed on your Meta Quest!\n\n• You will find 'Unplugged Modded' under Unknown Sources in your headset.\n{0}• You can now use 'Sync Songs to Quest' to transfer your custom songs directly.",
                ["Quest_Dialog_PatchCompleteTutorialNote"] = "• The calibration tutorial has been skipped: you will enter directly into the studio with all base venues unlocked.\n",
                ["Quest_Dialog_PatchErrorTitle"] = "Quest Modding Error",
                ["Quest_Dialog_PatchErrorMsg"] = "Failed to patch or deploy APK. Check the console log for details.",
                ["Quest_Dialog_ConfirmUninstallTitle"] = "Confirm Uninstallation",
                ["Quest_Dialog_ConfirmUninstallMsg"] = "Are you sure you want to uninstall 'Unplugged Modded' from your Meta Quest?\n\n• Your stock original game will NOT be affected.\n• Custom songs stored in /sdcard/Unplugged/Songs will NOT be deleted.\n• The cloned modded app and its app data will be removed.",
                ["Quest_Dialog_UninstallCompleteTitle"] = "Uninstallation Complete",
                ["Quest_Dialog_UninstallCompleteMsg"] = "Unplugged Modded has been successfully uninstalled from your Meta Quest.",
                ["Quest_Dialog_UninstallErrorTitle"] = "Uninstallation Error",
                ["Quest_Dialog_UninstallErrorMsg"] = "Failed to uninstall modded app:\n{0}",
                ["Quest_Dialog_ConfirmSyncTitle"] = "Sync Custom Songs",
                ["Quest_Dialog_ConfirmSyncMsg"] = "Sync {0} custom song(s) to Quest (/sdcard/Unplugged/Songs)?",
                ["Quest_Dialog_ConfirmIncrementalSyncMsg"] = "Found {0} new custom song(s) to sync to your Quest ({1} already up to date).\n\nDo you want to proceed?",
                ["Quest_Dialog_AllSongsSyncedTitle"] = "Songs Up to Date",
                ["Quest_Dialog_AllSongsSyncedMsg"] = "All {0} custom songs in your library are already synced to your Quest!\n\nDo you want to force re-sync all songs anyway?",
                ["Quest_Dialog_SyncCompleteTitle"] = "Songs Synced",
                ["Quest_Dialog_SyncCompleteMsg"] = "Successfully synced {0} songs to your Quest!\nThey will appear inside Unplugged Modded.",

                // Quest Log Texts
                ["Log_QuestInit"] = "Quest Patcher Initialized. Searching for Quest headsets via ADB...",
                ["Log_NoDevicesFound"] = "No devices found on ADB.",
                ["Log_FoundDevices"] = "Found {0} device(s). Selected: {1}",
                ["Log_ConnectingWireless"] = "Attempting wireless connection to {0}:{1}...",
                ["Log_WirelessConnected"] = "Wireless ADB connected! ({0})",
                ["Log_WirelessFailed"] = "Failed to connect wirelessly: {0}",
                ["Log_StartingPipeline"] = "=== STARTING QUEST MODDING PIPELINE ===",
                ["Log_VerifyTools"] = "Verifying Java, Apktool, and ADB...",
                ["Log_QueryingPackage"] = "Querying package '{0}' on device {1}...",
                ["Log_CheckingModdedPackage"] = "Original package '{0}' not found. Checking if modded version exists...",
                ["Log_OriginalNotFound"] = "ERROR: Unplugged: Air Guitar was not found installed on this Quest.",
                ["Log_PullingApk"] = "Pulling APK from Quest ({0}) -> {1}...",
                ["Log_ApkPulledSuccess"] = "APK pulled successfully ({0} MB).",
                ["Log_ApkPulledFail"] = "Failed to pull APK: {0}",
                ["Log_DecompilingApk"] = "Decompiling APK structure (this may take up to 1-2 minutes)...",
                ["Log_DecompileSuccess"] = "APK decompiled successfully.",
                ["Log_PatchingManifest"] = "Configuring cloned package identity (com.anotherway.unplugged.modded) and mod permissions...",
                ["Log_ManifestSuccess"] = "AndroidManifest.xml successfully patched.",
                ["Log_RebuildingApk"] = "Rebuilding APK (this may take 1-2 minutes)...",
                ["Log_RebuildSuccess"] = "APK rebuilt: {0}",
                ["Log_InjectingLemonLoader"] = "Injecting LemonLoader mod engine into APK (ARM64 IL2CPP)...",
                ["Log_LemonLoaderSuccess"] = "LemonLoader injected and APK signed successfully!",
                ["Log_DeployingModDll"] = "Deploying UnpluggedCustomSongs mod to Quest Mods directory...",
                ["Log_ModDllSuccess"] = "UnpluggedCustomSongs mod successfully deployed to Quest!",
                ["Log_SigningApk"] = "Signing APK (v2/v3 signatures)...",
                ["Log_SigningSuccess"] = "Signed APK ready: {0}",
                ["Log_InstallingApk"] = "Installing modded APK to Quest ({0})...",
                ["Log_SignatureMismatchUninstall"] = "Existing installation has an incompatible signature. Performing clean reinstall...",
                ["Log_InstallSuccess"] = "APK successfully installed on Quest!",
                ["Log_SettingObb"] = "Setting up game data OBB expansion file on Quest...",
                ["Log_GrantingPermissions"] = "Granting storage and file access permissions on Quest...",
                ["Log_PermissionsReady"] = "Quest storage permissions and directories ready.",
                ["Log_InjectingSave"] = "Injecting unlocked save data (Tutorial Skipped + Campaign Albums 1-5 Unlocked)...",
                ["Log_SaveSuccess"] = "Tutorial skipped and base campaign venues unlocked on Quest!",
                ["Log_PipelineComplete"] = "=== QUEST MODDING PIPELINE FINISHED SUCCESSFULLY ===",
                ["Log_Uninstalling"] = "Uninstalling {0} from Quest headset...",
                ["Log_UninstallSuccess"] = "Modded package uninstalled successfully.",
                ["Log_SyncStarting"] = "Syncing {0} custom songs to Quest (/sdcard/Unplugged/Songs)...",
                ["Log_SyncIncremental"] = "Found {0} songs already on Quest. Syncing {1} new song(s)...",
                ["Log_SyncAllUpToDate"] = "All {0} songs are already installed on Quest. Nothing to transfer.",
                ["Log_SyncingSong"] = "[{0}/{1}] Pushing {2}...",
                ["Log_SyncComplete"] = "Sync finished! {0}/{1} songs transferred to Quest.",

                // Settings Tab
                ["Settings_LanguageCardTitle"] = "Interface Language",
                ["Settings_LanguageDesc"] = "Select your preferred display language for the application and console logs.",
                ["Settings_StatusReady"] = "✓ Ready / Installed",
                ["Settings_StatusMissing"] = "✗ Not Found / Missing",
                ["Settings_GameDirTitle"] = "Game Directory",
                ["Settings_BrowseButton"] = "Browse",
                ["Settings_ModStatusTitle"] = "Mod Installation Status",
                ["Settings_InstallModButton"] = "Install / Update Mod",
                ["Settings_GameExecLabel"] = "Game Executable:",
                ["Settings_MelonLabel"] = "MelonLoader:",
                ["Settings_ModLabel"] = "Custom Songs Mod:",
                ["Settings_CustomSongsFolderLabel"] = "Custom Songs Folder:",
                ["Settings_InGameSettingsTitle"] = "In-Game Mod Settings",
                ["Settings_DiscordRpc"] = "Discord Rich Presence",
                ["Settings_VerboseLogging"] = "Verbose Logging",
                ["Settings_DownloadPrefsTitle"] = "Download Preferences",
                ["Settings_MaxDownloadsLabel"] = "Max Concurrent Downloads:",

                // Updates & GitHub Releases
                ["Settings_UpdatesTitle"] = "Updates & GitHub Releases",
                ["Settings_CurrentAppVersion"] = "Current USM Version:",
                ["Settings_InstalledModVersion"] = "Installed PC Mod Version:",
                ["Settings_LatestRelease"] = "Latest GitHub Release:",
                ["Settings_CheckUpdatesBtn"] = "🔄 Check for Updates",
                ["Settings_CheckingUpdates"] = "Checking GitHub for updates...",
                ["Settings_UpToDate"] = "✔ You are running the latest version.",
                ["Settings_UpdateAvailable"] = "⚡ New version available: {0}!",
                ["Settings_NoReleasesFound"] = "No releases published yet on GitHub.",
                ["Settings_ViewReleaseBtn"] = "🌐 View on GitHub",
                ["Settings_DownloadModBtn"] = "⬇ Download & Update Mod from GitHub",
                ["Settings_ModUpdatedSuccess"] = "Mod plugin updated successfully from GitHub release!",
                ["Settings_ModUpdateFailed"] = "Failed to download mod: {0}",
                ["Settings_UpdateCheckFailed"] = "Could not check for updates: {0}",
                ["Settings_RateLimitExceeded"] = "GitHub API rate limit exceeded. Try again later.",
                ["Settings_UpdateAppBtn"] = "🚀 Update USM App",
                ["Settings_UpdatingApp"] = "Downloading USM update: {0:F0}%...",
                ["Settings_ExtractingApp"] = "Extracting update files and preparing installer...",
                ["Settings_UpdateAppConfirmTitle"] = "Update USM",
                ["Settings_UpdateAppConfirmMsg"] = "A new version of USM ({0}) is available.\n\nUSM will download the update, close, and restart automatically.\n\nDo you want to proceed?",
                ["Settings_UpdateAppFailed"] = "Failed to update USM: {0}",
                ["Settings_NoAppAssetFound"] = "No direct update package was found in the release. Please download it manually from GitHub.",

                // MelonLoader installation
                ["Settings_MelonInstallBtn"] = "Install MelonLoader",
                ["Settings_MelonReinstallBtn"] = "Reinstall MelonLoader",
                ["Settings_MelonUninstallBtn"] = "Uninstall MelonLoader",
                ["Settings_MelonDownloading"] = "Downloading MelonLoader v0.5.7 ({0:F0}%)...",
                ["Settings_MelonExtracting"] = "Extracting MelonLoader files to game folder...",
                ["Settings_MelonSuccess"] = "MelonLoader v0.5.7 installed successfully!",
                ["Settings_MelonFailed"] = "Failed to install MelonLoader: {0}",
                ["Settings_MelonUninstalled"] = "MelonLoader uninstalled successfully.",

                // Quest Patching Tools Caching / Download
                ["Quest_ToolsStatusCached"] = "Patching Tools: Ready (Cached)",
                ["Quest_ToolsStatusMissing"] = "Patching Tools: Will be downloaded on demand",
                ["Quest_DownloadToolsBtn"] = "Download Tools",
                ["Log_QuestToolsDownloading"] = "Downloading Quest patching tools from GitHub ({0:F0}%)...",
                ["Log_QuestToolsExtracting"] = "Extracting Quest tools to local cache...",
                ["Log_QuestToolsCached"] = "Quest patching tools ready in local cache ({0}).",
                ["Log_QuestToolsFailed"] = "Failed to download Quest patching tools: {0}"
            },
            ["es"] = new Dictionary<string, string>
            {
                // Top Navbar & Tabs
                ["App_Title"] = "USM • UNPLUGGED SONG MANAGER",
                ["Nav_LaunchGame"] = "🚀 Iniciar Unplugged",
                ["Nav_LaunchGame_Tooltip"] = "Iniciar Unplugged VR a través de Steam",
                ["Nav_GameConnected"] = "Juego Conectado",
                ["Nav_GameNotDetected"] = "Juego no Detectado",
                ["Tab_Download"] = "🔍 Descargas (Chorus)",
                ["Tab_Converter"] = "⚡ Conversor Local",
                ["Tab_Library"] = "📚 Biblioteca de Canciones",
                ["Tab_QuestPatcher"] = "🥽 Parcheador Quest",
                ["Tab_Settings"] = "⚙ Configuración",
                ["Toast_Ready"] = "Listo. Busca o arrastra archivos chart para agregarlos a tu biblioteca VR.",

                // Quest Tab - Headset ADB Connection
                ["Quest_ConnectionTitle"] = "Conexión ADB de Meta Quest",
                ["Quest_RefreshDevices"] = "🔄 Actualizar Dispositivos",
                ["Quest_DetectedHeadset"] = "Visor Detectado (USB / Wi-Fi)",
                ["Quest_SelectHeadsetHint"] = "Selecciona un visor de la lista:",
                ["Quest_WirelessTitle"] = "Conexión Inalámbrica (Wi-Fi ADB)",
                ["Quest_IpLabel"] = "Dirección IP del Visor",
                ["Quest_PortLabel"] = "Puerto",
                ["Quest_ConnectWifi"] = "📡 Conectar Wi-Fi",
                ["Quest_IpTooltip"] = "Dirección IP local de tu Meta Quest (ej: 192.168.1.15)",
                ["Quest_PortTooltip"] = "Puerto ADB (por defecto: 5555)",
                ["Quest_StatusUnauthorized"] = "⚠ Visor no autorizado: Por favor ponte el Quest y presiona 'Permitir depuración USB'.",
                ["Quest_StatusNoDevice"] = "No se detectó ningún visor Quest. Conéctalo por USB o ingresa su IP.",
                ["Quest_StatusChecking"] = "Comprobando paquetes de Unplugged en el visor...",
                ["Quest_StockFound"] = "✔ Stock Unplugged detectado (com.Anotherway.UnpluggedVR)",
                ["Quest_StockNotFound"] = "❌ Stock Unplugged no encontrado",
                ["Quest_ModdedFound"] = "✔ Modded Unplugged instalado (com.Anotherway.UnpluggedVR.modded)",
                ["Quest_ModdedNotFound"] = "⚪ Modded Unplugged no instalado aún",

                // Quest Tab - Options
                ["Quest_SourceConfigTitle"] = "Configuración del APK y Mod",
                ["Quest_UseCustomApk"] = "Usar archivo APK local personalizado (en lugar de extraerlo del Quest)",
                ["Quest_BrowseApk"] = "📂 Buscar APK...",
                ["Quest_SkipTutorialTitle"] = "⚡ Omitir Tutorial de Calibración y Desbloquear Campaña (Recomendado)",
                ["Quest_SkipTutorialDesc"] = "Inicia directamente en el estudio sin tener que repetir el tutorial de 15 minutos ni calibración de manos. Desbloquea todas las salas y álbumes base de la campaña (los DLCs oficiales se mantienen intactos).",

                // Quest Tab - Actions
                ["Quest_StandaloneModderTitle"] = "⚡ Modificador Standalone de Quest",
                ["Quest_PatchButton_Install"] = "🚀 Parchear e Instalar 'Unplugged Modded'",
                ["Quest_PatchDesc_Install"] = "Extrae el juego de tu Quest, lo clona en una app independiente ('Unplugged Modded') con soporte de custom songs y omisión de tutorial, y lo instala junto a tu juego original.",
                ["Quest_PatchButton_Update"] = "🔄 Actualizar 'Unplugged Modded'",
                ["Quest_PatchDesc_Update"] = "Se detectó una actualización oficial del juego (v{0} vs Mod v{1}). Vuelve a parchear para mantener la versión al día.",
                ["Quest_PatchButton_Reinstall"] = "🔨 Reinstalar / Reparar 'Unplugged Modded'",
                ["Quest_PatchDesc_Reinstall"] = "La app modeada ya está instalada en tu visor{0}. Puedes reinstalarla o repararla para restaurar archivos o ajustes.",
                ["Quest_UninstallButton"] = "🗑 Desinstalar 'Unplugged Modded'",

                // Quest Tab - Song Sync
                ["Quest_CustomSongsSyncTitle"] = "🎵 Transferencia de Canciones Custom",
                ["Quest_CustomSongsSyncDesc"] = "Copia todas las canciones custom de tu biblioteca USM directamente al almacenamiento del Quest (/sdcard/Unplugged/Songs) listas para jugar.",
                ["Quest_SyncSongsButton"] = "📂 Sincronizar Canciones al Quest",

                // Quest Tab - Console Log
                ["Quest_ConsoleLogTitle"] = "Consola de Registros ADB y Parcheador",
                ["Quest_ClearLogs"] = "Limpiar Registro",

                // Quest Dialogs & Logs
                ["Quest_Dialog_SelectHeadsetWarning"] = "Por favor conecta un visor Quest o selecciona un APK local válido.",
                ["Quest_Dialog_ConfirmPatchTitle"] = "Confirmar Parcheo de Quest",
                ["Quest_Dialog_ConfirmPatchMsg"] = "Esto va a {0} Unplugged: Air Guitar en una app independiente ('Unplugged Modded') que coexistirá junto a tu juego original, con soporte de custom songs{1}.\n\n¿Deseas continuar?",
                ["Quest_Dialog_PatchCompleteTitle"] = "Parcheo de Quest Completo",
                ["Quest_Dialog_PatchCompleteMsg"] = "¡'Unplugged Modded' fue creado e instalado exitosamente en tu Meta Quest!\n\n• Encontrarás 'Unplugged Modded' en Fuentes Desconocidas de tu visor.\n{0}• Ahora puedes presionar 'Sincronizar Canciones al Quest' para transferir tu música.",
                ["Quest_Dialog_PatchCompleteTutorialNote"] = "• El tutorial ha sido omitido: entrarás directamente al estudio con todas las salas base disponibles.\n",
                ["Quest_Dialog_PatchErrorTitle"] = "Error de Parcheo en Quest",
                ["Quest_Dialog_PatchErrorMsg"] = "Fallo al parchear o instalar el APK. Revisa la consola de registros para más detalles.",
                ["Quest_Dialog_ConfirmUninstallTitle"] = "Confirmar Desinstalación",
                ["Quest_Dialog_ConfirmUninstallMsg"] = "¿Estás seguro de que deseas desinstalar 'Unplugged Modded' de tu Meta Quest?\n\n• Tu juego original (stock) NO se verá afectado.\n• Las canciones custom guardadas en /sdcard/Unplugged/Songs NO se borrarán.\n• La app modeada clonada y sus datos temporales serán desinstalados.",
                ["Quest_Dialog_UninstallCompleteTitle"] = "Desinstalación Completa",
                ["Quest_Dialog_UninstallCompleteMsg"] = "'Unplugged Modded' ha sido desinstalado correctamente de tu Meta Quest.",
                ["Quest_Dialog_UninstallErrorTitle"] = "Error de Desinstalación",
                ["Quest_Dialog_UninstallErrorMsg"] = "Fallo al desinstalar la app modeada:\n{0}",
                ["Quest_Dialog_ConfirmSyncTitle"] = "Sincronizar Canciones Custom",
                ["Quest_Dialog_ConfirmSyncMsg"] = "¿Sincronizar {0} canción(es) custom a tu Quest (/sdcard/Unplugged/Songs)?",
                ["Quest_Dialog_ConfirmIncrementalSyncMsg"] = "Se encontró {0} canción(es) nueva(s) para sincronizar al Quest ({1} ya al día).\n\n¿Deseas continuar?",
                ["Quest_Dialog_AllSongsSyncedTitle"] = "Canciones al Día",
                ["Quest_Dialog_AllSongsSyncedMsg"] = "¡Todas las {0} canciones de tu biblioteca ya están sincronizadas en tu Quest!\n\n¿Deseas forzar la re-sincronización de todas las canciones de todos modos?",
                ["Quest_Dialog_SyncCompleteTitle"] = "Canciones Sincronizadas",
                ["Quest_Dialog_SyncCompleteMsg"] = "¡Se transfirieron {0} canciones a tu Quest con éxito!\nAparecerán dentro de Unplugged Modded.",

                // Quest Log Texts
                ["Log_QuestInit"] = "Parcheador Quest inicializado. Buscando visores Quest vía ADB...",
                ["Log_NoDevicesFound"] = "No se encontraron dispositivos en ADB.",
                ["Log_FoundDevices"] = "Se encontraron {0} dispositivo(s). Seleccionado: {1}",
                ["Log_ConnectingWireless"] = "Intentando conexión inalámbrica a {0}:{1}...",
                ["Log_WirelessConnected"] = "¡ADB inalámbrico conectado! ({0})",
                ["Log_WirelessFailed"] = "Fallo al conectar inalámbricamente: {0}",
                ["Log_StartingPipeline"] = "=== INICIANDO PROCESSO DE PARCHEO QUEST ===",
                ["Log_VerifyTools"] = "Verificando Java, Apktool y ADB...",
                ["Log_QueryingPackage"] = "Consultando paquete '{0}' en el dispositivo {1}...",
                ["Log_CheckingModdedPackage"] = "Paquete original '{0}' no encontrado. Comprobando versión modeada...",
                ["Log_OriginalNotFound"] = "ERROR: Unplugged: Air Guitar no se encontró instalado en este Quest.",
                ["Log_PullingApk"] = "Extrayendo APK del Quest ({0}) -> {1}...",
                ["Log_ApkPulledSuccess"] = "APK extraído con éxito ({0} MB).",
                ["Log_ApkPulledFail"] = "Fallo al extraer el APK: {0}",
                ["Log_DecompilingApk"] = "Descompilando estructura del APK con Apktool (puede demorar 1-2 minutos)...",
                ["Log_DecompileSuccess"] = "APK descompilado correctamente.",
                ["Log_PatchingManifest"] = "Configurando identidad clonada y permisos de almacenamiento...",
                ["Log_ManifestSuccess"] = "AndroidManifest.xml parcheado con éxito.",
                ["Log_RebuildingApk"] = "Reconstruyendo APK con Apktool (puede demorar 1-2 minutos)...",
                ["Log_RebuildSuccess"] = "APK reconstruido: {0}",
                ["Log_InjectingLemonLoader"] = "Inyectando motor de modding LemonLoader en el APK (ARM64 IL2CPP)...",
                ["Log_LemonLoaderSuccess"] = "¡LemonLoader inyectado y APK firmado correctamente!",
                ["Log_DeployingModDll"] = "Desplegando mod UnpluggedCustomSongs en el directorio Mods de Quest...",
                ["Log_ModDllSuccess"] = "¡Mod UnpluggedCustomSongs desplegado correctamente en Quest!",
                ["Log_SigningApk"] = "Firmando APK con firmas v2/v3...",
                ["Log_SigningSuccess"] = "APK firmado listo: {0}",
                ["Log_InstallingApk"] = "Instalando APK modeado en el Quest ({0})...",
                ["Log_SignatureMismatchUninstall"] = "La versión instalada tiene una firma incompatible. Realizando reinstalación limpia...",
                ["Log_InstallSuccess"] = "¡APK instalado exitosamente en el Quest!",
                ["Log_SettingObb"] = "Configurando archivo de expansión de datos OBB en el Quest...",
                ["Log_GrantingPermissions"] = "Otorgando permisos de almacenamiento y archivos en el Quest...",
                ["Log_PermissionsReady"] = "Permisos de almacenamiento y carpetas de Quest listos.",
                ["Log_InjectingSave"] = "Inyectando partida desbloqueada (Tutorial omitido + Campaña 1-5 desbloqueada)...",
                ["Log_SaveSuccess"] = "¡Tutorial omitido y salas de campaña desbloqueadas en Quest!",
                ["Log_PipelineComplete"] = "=== PROCESO DE PARCHEO QUEST FINALIZADO CON ÉXITO ===",
                ["Log_Uninstalling"] = "Desinstalando {0} del visor Quest...",
                ["Log_UninstallSuccess"] = "Paquete modificado desinstalado con éxito.",
                ["Log_SyncStarting"] = "Sincronizando {0} canciones custom a Quest (/sdcard/Unplugged/Songs)...",
                ["Log_SyncIncremental"] = "Se encontraron {0} canciones ya en el Quest. Sincronizando {1} nueva(s)...",
                ["Log_SyncAllUpToDate"] = "Todas las {0} canciones ya están presentes en el Quest. Nada nuevo para transferir.",
                ["Log_SyncingSong"] = "[{0}/{1}] Transfiriendo {2}...",
                ["Log_SyncComplete"] = "¡Sincronización finalizada! {0}/{1} canciones transferidas al Quest.",

                // Settings Tab
                ["Settings_LanguageCardTitle"] = "Idioma de la Interfaz",
                ["Settings_LanguageDesc"] = "Selecciona el idioma preferido para la interfaz y los registros de la aplicación.",
                ["Settings_StatusReady"] = "✓ Listo / Instalado",
                ["Settings_StatusMissing"] = "✗ No Encontrado / Falta",
                ["Settings_GameDirTitle"] = "Directorio del Juego",
                ["Settings_BrowseButton"] = "Examinar",
                ["Settings_ModStatusTitle"] = "Estado de Instalación del Mod",
                ["Settings_InstallModButton"] = "Instalar / Actualizar Mod",
                ["Settings_GameExecLabel"] = "Ejecutable del Juego:",
                ["Settings_MelonLabel"] = "MelonLoader:",
                ["Settings_ModLabel"] = "Mod de Canciones Custom:",
                ["Settings_CustomSongsFolderLabel"] = "Carpeta de Canciones Custom:",
                ["Settings_InGameSettingsTitle"] = "Ajustes del Mod en el Juego",
                ["Settings_DiscordRpc"] = "Presencia en Discord (Rich Presence)",
                ["Settings_VerboseLogging"] = "Registro Detallado (Verbose Logging)",
                ["Settings_DownloadPrefsTitle"] = "Preferencias de Descarga",
                ["Settings_MaxDownloadsLabel"] = "Máximo de Descargas Simultáneas:",

                // Actualizaciones y Releases de GitHub
                ["Settings_UpdatesTitle"] = "Actualizaciones y Releases de GitHub",
                ["Settings_CurrentAppVersion"] = "Versión Actual de USM:",
                ["Settings_InstalledModVersion"] = "Versión del Mod en PC:",
                ["Settings_LatestRelease"] = "Última Release en GitHub:",
                ["Settings_CheckUpdatesBtn"] = "🔄 Buscar Actualizaciones",
                ["Settings_CheckingUpdates"] = "Consultando GitHub Releases...",
                ["Settings_UpToDate"] = "✔ Tienes la versión más reciente.",
                ["Settings_UpdateAvailable"] = "⚡ ¡Nueva versión disponible: {0}!",
                ["Settings_NoReleasesFound"] = "Aún no hay releases publicadas en GitHub.",
                ["Settings_ViewReleaseBtn"] = "🌐 Ver en GitHub",
                ["Settings_DownloadModBtn"] = "⬇ Descargar y Actualizar Mod de GitHub",
                ["Settings_ModUpdatedSuccess"] = "¡Plugin del mod actualizado con éxito desde GitHub!",
                ["Settings_ModUpdateFailed"] = "Fallo al descargar el mod: {0}",
                ["Settings_UpdateCheckFailed"] = "No se pudo verificar actualizaciones: {0}",
                ["Settings_RateLimitExceeded"] = "Límite de solicitudes de GitHub alcanzado. Intenta más tarde.",
                ["Settings_UpdateAppBtn"] = "🚀 Actualizar USM",
                ["Settings_UpdatingApp"] = "Descargando actualización de USM: {0:F0}%...",
                ["Settings_ExtractingApp"] = "Extrayendo archivos y preparando el instalador...",
                ["Settings_UpdateAppConfirmTitle"] = "Actualizar USM",
                ["Settings_UpdateAppConfirmMsg"] = "Una nueva versión de USM ({0}) está disponible.\n\nUSM descargará la actualización, se cerrará y reiniciará automáticamente.\n\n¿Deseas continuar?",
                ["Settings_UpdateAppFailed"] = "Error al actualizar USM: {0}",
                ["Settings_NoAppAssetFound"] = "No se encontró un paquete de actualización directa en la release. Por favor descárgalo manualmente desde GitHub.",

                // MelonLoader installation
                ["Settings_MelonInstallBtn"] = "Instalar MelonLoader",
                ["Settings_MelonReinstallBtn"] = "Reinstalar MelonLoader",
                ["Settings_MelonUninstallBtn"] = "Desinstalar MelonLoader",
                ["Settings_MelonDownloading"] = "Descargando MelonLoader v0.5.7 ({0:F0}%)...",
                ["Settings_MelonExtracting"] = "Extrayendo archivos de MelonLoader en la carpeta del juego...",
                ["Settings_MelonSuccess"] = "¡MelonLoader v0.5.7 instalado exitosamente!",
                ["Settings_MelonFailed"] = "Error al instalar MelonLoader: {0}",
                ["Settings_MelonUninstalled"] = "MelonLoader desinstalado exitosamente.",

                // Quest Patching Tools Caching / Download
                ["Quest_ToolsStatusCached"] = "Herramientas de Parcheo: Listas (En Caché)",
                ["Quest_ToolsStatusMissing"] = "Herramientas de Parcheo: Se descargarán bajo demanda",
                ["Quest_DownloadToolsBtn"] = "Descargar Herramientas",
                ["Log_QuestToolsDownloading"] = "Descargando herramientas de parcheo de Quest desde GitHub ({0:F0}%)...",
                ["Log_QuestToolsExtracting"] = "Extrayendo herramientas de Quest en la caché local...",
                ["Log_QuestToolsCached"] = "Herramientas de parcheo de Quest listas en caché local ({0}).",
                ["Log_QuestToolsFailed"] = "Error al descargar herramientas de parcheo de Quest: {0}"
            },
            ["pt"] = new Dictionary<string, string>
            {
                // Top Navbar & Tabs
                ["App_Title"] = "USM • UNPLUGGED SONG MANAGER",
                ["Nav_LaunchGame"] = "🚀 Iniciar Unplugged",
                ["Nav_LaunchGame_Tooltip"] = "Iniciar Unplugged VR através da Steam",
                ["Nav_GameConnected"] = "Jogo Conectado",
                ["Nav_GameNotDetected"] = "Jogo Não Detectado",
                ["Tab_Download"] = "🔍 Baixar (Chorus)",
                ["Tab_Converter"] = "⚡ Conversor Local",
                ["Tab_Library"] = "📚 Biblioteca de Músicas",
                ["Tab_QuestPatcher"] = "🥽 Patch Quest",
                ["Tab_Settings"] = "⚙ Configurações",
                ["Toast_Ready"] = "Pronto. Pesquise ou arraste arquivos chart para adicioná-los à sua biblioteca VR.",

                // Quest Tab - Headset ADB Connection
                ["Quest_ConnectionTitle"] = "Conexão ADB Meta Quest",
                ["Quest_RefreshDevices"] = "🔄 Atualizar Dispositivos",
                ["Quest_DetectedHeadset"] = "Headset Detectado (USB / Wi-Fi)",
                ["Quest_SelectHeadsetHint"] = "Selecione um dispositivo da lista:",
                ["Quest_WirelessTitle"] = "Conexão Sem Fio (Wi-Fi ADB)",
                ["Quest_IpLabel"] = "Endereço IP do Headset",
                ["Quest_PortLabel"] = "Porta",
                ["Quest_ConnectWifi"] = "📡 Conectar Wi-Fi",
                ["Quest_IpTooltip"] = "Endereço IP local do Meta Quest (ex: 192.168.1.15)",
                ["Quest_PortTooltip"] = "Porta ADB (Padrão: 5555)",
                ["Quest_StatusUnauthorized"] = "⚠ Headset não autorizado: Coloque o Quest e confirme 'Permitir Depuração USB'.",
                ["Quest_StatusNoDevice"] = "Nenhum Quest encontrado. Conecte via USB ou insira o IP.",
                ["Quest_StatusChecking"] = "Verificando pacotes do Unplugged no Quest...",
                ["Quest_StockFound"] = "✔ Stock Unplugged detectado (com.Anotherway.UnpluggedVR)",
                ["Quest_StockNotFound"] = "❌ Stock Unplugged não encontrado",
                ["Quest_ModdedFound"] = "✔ Modded Unplugged instalado (com.Anotherway.UnpluggedVR.modded)",
                ["Quest_ModdedNotFound"] = "⚪ Modded Unplugged ainda não instalado",

                // Quest Tab - Options
                ["Quest_SourceConfigTitle"] = "Configuração do APK e Mod",
                ["Quest_UseCustomApk"] = "Usar APK local personalizado (em vez de extrair do Quest)",
                ["Quest_BrowseApk"] = "📂 Procurar APK...",
                ["Quest_SkipTutorialTitle"] = "⚡ Pular Tutorial de Calibração e Desbloquear Campanha (Recomendado)",
                ["Quest_SkipTutorialDesc"] = "Inicia direto no estúdio sem precisar repetir o tutorial de 15 minutos nem calibração manual. Desbloqueia todas as salas e álbuns base (DLCs oficiais permanecem intactos).",

                // Quest Tab - Actions
                ["Quest_StandaloneModderTitle"] = "⚡ Modificador Standalone Quest",
                ["Quest_PatchButton_Install"] = "🚀 Criar e Instalar 'Unplugged Modded'",
                ["Quest_PatchDesc_Install"] = "Extrai o jogo do Quest, clona em um app independente ('Unplugged Modded') com suporte a custom songs e tutorial pulado, e instala ao lado do jogo original.",
                ["Quest_PatchButton_Update"] = "🔄 Atualizar 'Unplugged Modded'",
                ["Quest_PatchDesc_Update"] = "Atualização oficial detectada (v{0} vs Mod v{1}). Crie o patch novamente para atualizar.",
                ["Quest_PatchButton_Reinstall"] = "🔨 Reinstalar / Reparar 'Unplugged Modded'",
                ["Quest_PatchDesc_Reinstall"] = "O app modificado já está instalado{0}. Você pode reinstalar ou reparar para restaurar arquivos.",
                ["Quest_UninstallButton"] = "🗑 Desinstalar 'Unplugged Modded'",

                // Quest Tab - Song Sync
                ["Quest_CustomSongsSyncTitle"] = "🎵 Transferência de Custom Songs",
                ["Quest_CustomSongsSyncDesc"] = "Envia todas as músicas da biblioteca USM diretamente para o armazenamento do Quest (/sdcard/Unplugged/Songs) prontas para jogar.",
                ["Quest_SyncSongsButton"] = "📂 Sincronizar Músicas para o Quest",

                // Quest Tab - Console Log
                ["Quest_ConsoleLogTitle"] = "Console de Logs ADB e Patcher",
                ["Quest_ClearLogs"] = "Limpar Logs",

                // Quest Dialogs & Logs
                ["Quest_Dialog_SelectHeadsetWarning"] = "Por favor conecte um dispositivo Quest ou selecione um APK local válido.",
                ["Quest_Dialog_ConfirmPatchTitle"] = "Confirmar Modificação Quest",
                ["Quest_Dialog_ConfirmPatchMsg"] = "Isso irá {0} Unplugged: Air Guitar em um app independente ('Unplugged Modded') que coexistirá com seu jogo original, com suporte a músicas customizadas{1}.\n\nDeseja continuar?",
                ["Quest_Dialog_PatchCompleteTitle"] = "Modificação Quest Concluída",
                ["Quest_Dialog_PatchCompleteMsg"] = "'Unplugged Modded' foi criado e instalado com sucesso no seu Meta Quest!\n\n• Você encontrará o 'Unplugged Modded' em Fontes Desconhecidas no seu visor.\n{0}• Agora você pode clicar em 'Sincronizar Músicas para o Quest' para transferir suas músicas.",
                ["Quest_Dialog_PatchCompleteTutorialNote"] = "• O tutorial foi pulado: você entrará direto no estúdio com todos os locais base desbloqueados.\n",
                ["Quest_Dialog_PatchErrorTitle"] = "Erro ao Modificar Quest",
                ["Quest_Dialog_PatchErrorMsg"] = "Falha ao aplicar patch ou instalar o APK. Verifique o console de logs para detalhes.",
                ["Quest_Dialog_ConfirmUninstallTitle"] = "Confirmar Desinstalação",
                ["Quest_Dialog_ConfirmUninstallMsg"] = "Tem certeza de que deseja desinstalar 'Unplugged Modded' do seu Meta Quest?\n\n• Seu jogo original NÃO será afetado.\n• As músicas customizadas em /sdcard/Unplugged/Songs NÃO serão excluídas.\n• O app modificado e seus dados temporários serão removidos.",
                ["Quest_Dialog_UninstallCompleteTitle"] = "Desinstalação Concluída",
                ["Quest_Dialog_UninstallCompleteMsg"] = "'Unplugged Modded' foi desinstalado com sucesso do seu Meta Quest.",
                ["Quest_Dialog_UninstallErrorTitle"] = "Erro de Desinstalação",
                ["Quest_Dialog_UninstallErrorMsg"] = "Falha ao desinstalar app modificado:\n{0}",
                ["Quest_Dialog_ConfirmSyncTitle"] = "Sincronizar Músicas Custom",
                ["Quest_Dialog_ConfirmSyncMsg"] = "Sincronizar {0} música(s) customizada(s) para o Quest (/sdcard/Unplugged/Songs)?",
                ["Quest_Dialog_ConfirmIncrementalSyncMsg"] = "Encontrada(s) {0} nova(s) música(s) para sincronizar no Quest ({1} já atualizada(s)).\n\nDeseja continuar?",
                ["Quest_Dialog_AllSongsSyncedTitle"] = "Músicas Atualizadas",
                ["Quest_Dialog_AllSongsSyncedMsg"] = "Todas as {0} músicas da sua biblioteca já estão instaladas no seu Quest!\n\nDeseja forçar a sincronização de todas as músicas mesmo assim?",
                ["Quest_Dialog_SyncCompleteTitle"] = "Músicas Sincronizadas",
                ["Quest_Dialog_SyncCompleteMsg"] = "Transferência de {0} música(s) concluída com sucesso!\nElas aparecerão no Unplugged Modded.",

                // Quest Log Texts
                ["Log_QuestInit"] = "Patcher Quest inicializado. Procurando headsets Quest via ADB...",
                ["Log_NoDevicesFound"] = "Nenhum dispositivo encontrado no ADB.",
                ["Log_FoundDevices"] = "Encontrado(s) {0} dispositivo(s). Selecionado: {1}",
                ["Log_ConnectingWireless"] = "Tentando conexão sem fio para {0}:{1}...",
                ["Log_WirelessConnected"] = "ADB sem fio conectado! ({0})",
                ["Log_WirelessFailed"] = "Falha ao conectar sem fio: {0}",
                ["Log_StartingPipeline"] = "=== INICIANDO PROCESSO DE PATCH QUEST ===",
                ["Log_VerifyTools"] = "Verificando Java, Apktool e ADB...",
                ["Log_QueryingPackage"] = "Consultando pacote '{0}' no dispositivo {1}...",
                ["Log_CheckingModdedPackage"] = "Pacote original '{0}' não encontrado. Verificando versão modificada...",
                ["Log_OriginalNotFound"] = "ERRO: Unplugged: Air Guitar não foi encontrado instalado neste Quest.",
                ["Log_PullingApk"] = "Extraindo APK do Quest ({0}) -> {1}...",
                ["Log_ApkPulledSuccess"] = "APK extraído com sucesso ({0} MB).",
                ["Log_ApkPulledFail"] = "Falha ao extrair APK: {0}",
                ["Log_DecompilingApk"] = "Descompilando estrutura do APK com Apktool (pode levar 1-2 minutos)...",
                ["Log_DecompileSuccess"] = "APK descompilado com sucesso.",
                ["Log_PatchingManifest"] = "Configurando identidade clonada e permissões de armazenamento...",
                ["Log_ManifestSuccess"] = "AndroidManifest.xml modificado com sucesso.",
                ["Log_RebuildingApk"] = "Reconstruindo APK com Apktool (pode levar 1-2 minutos)...",
                ["Log_RebuildSuccess"] = "APK reconstruído: {0}",
                ["Log_InjectingLemonLoader"] = "Injetando mecanismo de modding LemonLoader no APK (ARM64 IL2CPP)...",
                ["Log_LemonLoaderSuccess"] = "LemonLoader injetado e APK assinado com sucesso!",
                ["Log_DeployingModDll"] = "Implantando mod UnpluggedCustomSongs no diretório Mods do Quest...",
                ["Log_ModDllSuccess"] = "Mod UnpluggedCustomSongs implantado com sucesso no Quest!",
                ["Log_SigningApk"] = "Assinando APK com assinaturas v2/v3...",
                ["Log_SigningSuccess"] = "APK assinado pronto: {0}",
                ["Log_InstallingApk"] = "Instalando APK modificado no Quest ({0})...",
                ["Log_SignatureMismatchUninstall"] = "A instalação existente possui uma assinatura incompatível. Realizando reinstalação limpa...",
                ["Log_InstallSuccess"] = "APK instalado com sucesso no Quest!",
                ["Log_SettingObb"] = "Configurando arquivo de dados de expansão OBB no Quest...",
                ["Log_GrantingPermissions"] = "Concedendo permissões de armazenamento e arquivos no Quest...",
                ["Log_PermissionsReady"] = "Permissões de armazenamento e diretórios do Quest prontos.",
                ["Log_InjectingSave"] = "Injetando savegame desbloqueado (Tutorial pulado + Campanha 1-5 desbloqueada)...",
                ["Log_SaveSuccess"] = "Tutorial pulado e locais de campanha desbloqueados no Quest!",
                ["Log_PipelineComplete"] = "=== PROCESSO DE PATCH QUEST FINALIZADO COM SUCESSO ===",
                ["Log_Uninstalling"] = "Desinstalando {0} do headset Quest...",
                ["Log_UninstallSuccess"] = "Pacote modificado desinstalado com sucesso.",
                ["Log_SyncStarting"] = "Sincronizando {0} músicas customizadas para o Quest (/sdcard/Unplugged/Songs)...",
                ["Log_SyncIncremental"] = "Encontradas {0} músicas já no Quest. Sincronizando {1} nova(s)...",
                ["Log_SyncAllUpToDate"] = "Todas as {0} músicas já estão presentes no Quest (/sdcard/Unplugged/Songs). Nada a transferir.",
                ["Log_SyncingSong"] = "[{0}/{1}] Enviando {2}...",
                ["Log_SyncComplete"] = "Sincronização concluída! {0}/{1} músicas transferidas para o Quest.",

                // Settings Tab
                ["Settings_LanguageCardTitle"] = "Idioma da Interface",
                ["Settings_LanguageDesc"] = "Escolha o idioma preferido para a interface e os logs do aplicativo.",
                ["Settings_StatusReady"] = "✓ Pronto / Instalado",
                ["Settings_StatusMissing"] = "✗ Não Encontrado",
                ["Settings_GameDirTitle"] = "Diretório do Jogo",
                ["Settings_BrowseButton"] = "Procurar",
                ["Settings_ModStatusTitle"] = "Status de Instalação do Mod",
                ["Settings_InstallModButton"] = "Instalar / Atualizar Mod",
                ["Settings_GameExecLabel"] = "Executável do Jogo:",
                ["Settings_MelonLabel"] = "MelonLoader:",
                ["Settings_ModLabel"] = "Mod Custom Songs:",
                ["Settings_CustomSongsFolderLabel"] = "Pasta de Músicas:",
                ["Settings_InGameSettingsTitle"] = "Configurações do Mod no Jogo",
                ["Settings_DiscordRpc"] = "Discord Rich Presence",
                ["Settings_VerboseLogging"] = "Logs Detalhados (Verbose)",
                ["Settings_DownloadPrefsTitle"] = "Preferências de Download",
                ["Settings_MaxDownloadsLabel"] = "Downloads Simultâneos Máximos:",

                // Atualizações e Releases do GitHub
                ["Settings_UpdatesTitle"] = "Atualizações e Releases do GitHub",
                ["Settings_CurrentAppVersion"] = "Versão Atual do USM:",
                ["Settings_InstalledModVersion"] = "Versão do Mod no PC:",
                ["Settings_LatestRelease"] = "Última Release no GitHub:",
                ["Settings_CheckUpdatesBtn"] = "🔄 Verificar Atualizações",
                ["Settings_CheckingUpdates"] = "Consultando GitHub Releases...",
                ["Settings_UpToDate"] = "✔ Você está usando a versão mais recente.",
                ["Settings_UpdateAvailable"] = "⚡ Nova versão disponível: {0}!",
                ["Settings_NoReleasesFound"] = "Nenhuma release publicada ainda no GitHub.",
                ["Settings_ViewReleaseBtn"] = "🌐 Ver no GitHub",
                ["Settings_DownloadModBtn"] = "⬇ Baixar e Atualizar Mod do GitHub",
                ["Settings_ModUpdatedSuccess"] = "Plugin do mod atualizado com sucesso do GitHub!",
                ["Settings_ModUpdateFailed"] = "Falha ao baixar o mod: {0}",
                ["Settings_UpdateCheckFailed"] = "Não foi possível verificar atualizações: {0}",
                ["Settings_RateLimitExceeded"] = "Limite de requisições do GitHub atingido. Tente mais tarde.",
                ["Settings_UpdateAppBtn"] = "🚀 Atualizar USM",
                ["Settings_UpdatingApp"] = "Baixando atualização do USM: {0:F0}%...",
                ["Settings_ExtractingApp"] = "Extraindo arquivos e preparando o instalador...",
                ["Settings_UpdateAppConfirmTitle"] = "Atualizar USM",
                ["Settings_UpdateAppConfirmMsg"] = "Uma nova versão do USM ({0}) está disponível.\n\nO USM baixará a atualização, fechará e reiniciará automaticamente.\n\nDeseja continuar?",
                ["Settings_UpdateAppFailed"] = "Falha ao atualizar o USM: {0}",
                ["Settings_NoAppAssetFound"] = "Nenhum pacote de atualização direta foi encontrado na release. Baixe manualmente pelo GitHub.",

                // MelonLoader installation
                ["Settings_MelonInstallBtn"] = "Instalar MelonLoader",
                ["Settings_MelonReinstallBtn"] = "Reinstalar MelonLoader",
                ["Settings_MelonUninstallBtn"] = "Desinstalar MelonLoader",
                ["Settings_MelonDownloading"] = "Baixando MelonLoader v0.5.7 ({0:F0}%)...",
                ["Settings_MelonExtracting"] = "Extraindo arquivos do MelonLoader na pasta do jogo...",
                ["Settings_MelonSuccess"] = "MelonLoader v0.5.7 instalado com sucesso!",
                ["Settings_MelonFailed"] = "Falha ao instalar o MelonLoader: {0}",
                ["Settings_MelonUninstalled"] = "MelonLoader desinstalado com sucesso.",

                // Quest Patching Tools Caching / Download
                ["Quest_ToolsStatusCached"] = "Ferramentas de Patch: Prontas (Em Cache)",
                ["Quest_ToolsStatusMissing"] = "Ferramentas de Patch: Serão baixadas sob demanda",
                ["Quest_DownloadToolsBtn"] = "Baixar Ferramentas",
                ["Log_QuestToolsDownloading"] = "Baixando ferramentas de patch do Quest do GitHub ({0:F0}%)...",
                ["Log_QuestToolsExtracting"] = "Extraindo ferramentas do Quest para o cache local...",
                ["Log_QuestToolsCached"] = "Ferramentas de patch do Quest prontas no cache local ({0}).",
                ["Log_QuestToolsFailed"] = "Falha ao baixar as ferramentas de patch do Quest: {0}"
            }
        };
    }
}
