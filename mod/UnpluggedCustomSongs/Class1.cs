using System;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Globalization;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Linq;
using UnityEngine;
using HarmonyLib;
using MelonLoader;
using SimpleJSON;
#if !QUEST_IL2CPP
using TMPro;
using Unplugged.Model;
using CultureInfo = System.Globalization.CultureInfo;
#endif
#if QUEST_IL2CPP
using Il2Cpp;
using Il2CppUnplugged.Model;
using Il2CppTMPro;
using CultureInfo = Il2CppSystem.Globalization.CultureInfo;
#endif

[assembly: MelonInfo(typeof(UnpluggedCustomSongs.ModMain), "Unplugged Custom Songs", "1.0", "Luc & Antigravity")]
[assembly: MelonGame("Anotherway", "Unplugged")]

namespace UnpluggedCustomSongs
{
    // Mono exposes the game's storage as fields. IL2CPP wrappers expose the
    // same native storage as managed properties; Traverse.Field silently misses
    // those properties. Use the actual member representation on each platform.
    internal static class GameFieldAccess
    {
        private static readonly Dictionary<(Type, string), MemberInfo> Members = new Dictionary<(Type, string), MemberInfo>();

        private static MemberInfo Resolve(object owner, string name)
        {
            Type ownerType = owner as Type ?? owner.GetType();
            lock (Members)
            {
                if (Members.TryGetValue((ownerType, name), out MemberInfo member)) return member;
                for (Type type = ownerType; type != null; type = type.BaseType)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    member = (MemberInfo)type.GetField(name, flags) ?? type.GetProperty(name, flags);
                    if (member == null) continue;
                    Members.Add((ownerType, name), member);
                    return member;
                }
                throw new MissingMemberException(ownerType.FullName, name);
            }
        }

        public static T Get<T>(object owner, string name)
        {
            MemberInfo member = Resolve(owner, name);
            object target = owner is Type ? null : owner;
            return (T)(member is FieldInfo field ? field.GetValue(target) : ((PropertyInfo)member).GetValue(target));
        }

        public static void Set(object owner, string name, object value)
        {
            MemberInfo member = Resolve(owner, name);
            object target = owner is Type ? null : owner;
            if (member is FieldInfo field) field.SetValue(target, value);
            else ((PropertyInfo)member).SetValue(target, value);
        }
    }

    public static class DiscordRpcManager
    {
        private static NamedPipeClientStream pipe;
        private static bool isConnected = false;
        private static readonly object lockObj = new object();

        public static void Init()
        {
            if (Application.platform == RuntimePlatform.Android) return;
            if (!ModMain.ConfigDiscordRPC) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Connect());
        }

        public static void EnsureConnected()
        {
            if (Application.platform == RuntimePlatform.Android) return;
            if (!ModMain.ConfigDiscordRPC) return;
            if (isConnected && pipe != null && pipe.IsConnected) return;
            Connect();
        }

        private static void Connect()
        {
            lock (lockObj)
            {
                if (isConnected && pipe != null && pipe.IsConnected) return;
                try
                {
                    if (pipe != null)
                    {
                        try { pipe.Close(); } catch { }
                        pipe = null;
                        isConnected = false;
                    }

                    string clientId = ModMain.ConfigDiscordClientId;
                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            var testPipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut);
                            testPipe.Connect(350);
                            if (testPipe.IsConnected)
                            {
                                // Send Handshake
                                string hsJson = $"{{\"v\":1,\"client_id\":\"{clientId}\"}}";
                                byte[] hsBytes = System.Text.Encoding.UTF8.GetBytes(hsJson);
                                byte[] hsHeader = new byte[8];
                                BitConverter.GetBytes(0).CopyTo(hsHeader, 0); // opcode 0 = HANDSHAKE
                                BitConverter.GetBytes(hsBytes.Length).CopyTo(hsHeader, 4);
                                testPipe.Write(hsHeader, 0, 8);
                                testPipe.Write(hsBytes, 0, hsBytes.Length);
                                testPipe.Flush();

                                // Read Handshake response
                                byte[] respHeader = new byte[8];
                                int read = testPipe.Read(respHeader, 0, 8);
                                if (read == 8)
                                {
                                    int respOpcode = BitConverter.ToInt32(respHeader, 0);
                                    int respLen = BitConverter.ToInt32(respHeader, 4);
                                    byte[] respData = new byte[respLen];
                                    testPipe.Read(respData, 0, respLen);

                                    if (respOpcode == 1) // DISPATCH / READY
                                    {
                                        pipe = testPipe;
                                        isConnected = true;
                                        MelonLogger.Msg($"[DiscordRPC] Connected to Discord! (Client ID: {clientId})");
                                        UpdateMenu();
                                        return;
                                    }
                                    else
                                    {
                                        string err = System.Text.Encoding.UTF8.GetString(respData);
                                        MelonLogger.Warning($"[DiscordRPC] Discord rejected handshake: {err}");
                                        try { testPipe.Close(); } catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    if (ModMain.ConfigVerbose) MelonLogger.Warning($"[DiscordRPC] Connection error: {ex.Message}");
                }
            }
        }

        private static void SendPacket(int opcode, string json)
        {
            try
            {
                if (pipe == null || !pipe.IsConnected)
                {
                    isConnected = false;
                    return;
                }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                byte[] header = new byte[8];
                BitConverter.GetBytes(opcode).CopyTo(header, 0);
                BitConverter.GetBytes(bytes.Length).CopyTo(header, 4);
                pipe.Write(header, 0, 8);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
            catch
            {
                isConnected = false;
            }
        }

        public static void UpdatePlaying(string songName, string artistName, string difficulty)
        {
            if (!ModMain.ConfigDiscordRPC) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureConnected();
                    if (!isConnected) return;

                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string details = $"{songName} - {artistName}".Replace("\"", "\\\"");
                    string state = $"Difficulty: {difficulty}".Replace("\"", "\\\"");
                    string payload = $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Process.GetCurrentProcess().Id},\"activity\":{{\"details\":\"{details}\",\"state\":\"{state}\",\"timestamps\":{{\"start\":{now}}},\"assets\":{{\"large_text\":\"Unplugged VR\"}}}}}},\"nonce\":\"{Guid.NewGuid()}\"}}";
                    SendPacket(1, payload);
                }
                catch { }
            });
        }

        public static void UpdateMenu()
        {
            if (!ModMain.ConfigDiscordRPC) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureConnected();
                    if (!isConnected) return;

                    string payload = $"{{\"cmd\":\"SET_ACTIVITY\",\"args\":{{\"pid\":{Process.GetCurrentProcess().Id},\"activity\":{{\"details\":\"In Hub / Browsing Songs\",\"state\":\"Custom Songs Collection\",\"assets\":{{\"large_text\":\"Unplugged VR\"}}}}}},\"nonce\":\"{Guid.NewGuid()}\"}}";
                    SendPacket(1, payload);
                }
                catch { }
            });
        }

        public static void Shutdown()
        {
            try
            {
                if (pipe != null && pipe.IsConnected)
                {
                    SendPacket(2, "{}");
                    pipe.Close();
                }
            }
            catch { }
        }
    }

    public class ModMain : MelonMod
    {
        public static MelonPreferences_Category PrefsCategory;
        public static MelonPreferences_Entry<bool> PrefDiscordRPC;
        public static MelonPreferences_Entry<bool> PrefVerbose;
        public static MelonPreferences_Entry<bool> PrefChartDiagnostics;
        public static MelonPreferences_Entry<string> PrefDiscordClientId;
        public static MelonPreferences_Entry<bool> PrefDebugMode;

        public static bool ConfigDiscordRPC => PrefDiscordRPC?.Value ?? true;
        public static bool ConfigVerbose => PrefVerbose?.Value ?? false;
        public static bool ConfigChartDiagnostics => PrefChartDiagnostics?.Value ?? true;
        public static string ConfigDiscordClientId => !string.IsNullOrEmpty(PrefDiscordClientId?.Value) ? PrefDiscordClientId.Value : "1441987286022619299";

        public static bool DebugMode = false;
        public static bool AutoPlay = false;
        public static float yaw = 0f;
        public static float pitch = 0f;
        private static bool initializedAngles = false;
        private static float menuLoadedTime;

        public override void OnInitializeMelon()
        {
            menuLoadedTime = Time.unscaledTime;
            LoggerInstance.Msg("=== Unplugged Custom Songs Mod Loaded! ===");
            PrefsCategory = MelonPreferences.CreateCategory("UnpluggedCustomSongs", "Unplugged Custom Songs");
            PrefDiscordRPC = PrefsCategory.CreateEntry("EnableDiscordRPC", true, "Enable Discord Rich Presence");
            PrefVerbose = PrefsCategory.CreateEntry("VerboseLogging", false, "Enable Verbose Debug Logging");
            PrefChartDiagnostics = PrefsCategory.CreateEntry("ChartDiagnostics", true, "Log custom chart loading and note results");
            PrefDiscordClientId = PrefsCategory.CreateEntry("DiscordClientId", "1441987286022619299", "Discord Application Client ID");
#if !QUEST_IL2CPP
            PrefDebugMode = PrefsCategory.CreateEntry("EnableDebugMode", false, "Enable Desktop WASD/Mouse Look & Testing Controls");
            DebugMode = PrefDebugMode?.Value ?? false;
#else
            // The desktop camera, mouse and simulated note controls can interfere with
            // Quest's tracked hands and controller input. Never activate that path on IL2CPP.
            DebugMode = false;
#endif
            DiscordRpcManager.Init();

            try
            {
                Application.runInBackground = true;
            }
            catch { }

            try
            {
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.Full);
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.Full);
            }
            catch { }

            AppDomain.CurrentDomain.FirstChanceException += (sender, eventArgs) =>
            {
                if (eventArgs.Exception is ArgumentOutOfRangeException || eventArgs.Exception is NullReferenceException)
                {
                    string st = eventArgs.Exception.StackTrace ?? "";
                    if (!st.Contains("MelonLoader") && !st.Contains("HarmonyLib.Public.Patching"))
                    {
                        LoggerInstance.Error($"[FIRST_CHANCE] {eventArgs.Exception.GetType().Name}: {eventArgs.Exception.Message}\n{st}");
                    }
                }
            };

#if !QUEST_IL2CPP
            Application.logMessageReceived += (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    LoggerInstance.Error($"[UNITY_{type}] {condition}\nSTACK TRACE:\n{stackTrace}");
                }
            };
#endif

            CustomSongLoader.Init();

            try
            {
                if (Params.Global != null)
                {
                    Params.Global.DisableControllerMode = false;
                    LoggerInstance.Msg("[CustomSongs] Set Params.Global.DisableControllerMode = false");
                }
            }
            catch { }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            try
            {
                if (Params.Global != null)
                {
                    Params.Global.DisableControllerMode = false;
                }
            }
            catch { }
        }
        // ===================== DESKTOP TEST CONTROLS =====================
        public static bool ShowButtonPanel = false; // Closed by default
        public static bool ShowMarkers = false;     // Off by default
        public static float ForcedGuitarVolume = -1f; // < 0 = no override
        private const float StrumWindow = 0.25f;      // seconds, manual strum hit window

#if !QUEST_IL2CPP
        private class ClickTarget
        {
            public PressableButton Button;
            public string Label;
            public Vector2 GuiPos;
            public bool OnScreen;
            public float Distance;
        }

        private static readonly List<ClickTarget> targets = new List<ClickTarget>();
        private static float nextRefresh = 0f;
        private static Rect panelRect = new Rect(0, 0, 0, 0);
        private static Vector2 panelScroll = Vector2.zero;
        private static readonly MethodInfo WaitToRaisePressedMI = AccessTools.Method(typeof(PressableButton), "WaitToRaisePressed");
#endif
        public static bool PendingReload = false;
        public static float ReloadTime = 0f;

        public static void ScheduleReload()
        {
            // Ignore reload requests during initial startup (first 6 seconds)
            if (Time.unscaledTime < 6.0f) return;
            PendingReload = true;
            ReloadTime = Time.unscaledTime + 2.0f; // 2 seconds debounce for file writing to finish
        }

        public static void PerformSongRefresh()
        {
            if (CustomSongLoader.CustomAlbumInstance == null) return;
            MelonLogger.Msg("[CustomSongs] Refreshing custom songs in real time...");
            CustomSongLoader.PopulateCustomSongs(CustomSongLoader.CustomAlbumInstance);
            int newCount = CustomSongLoader.CustomAlbumInstance.SongFiles != null ? CustomSongLoader.CustomAlbumInstance.SongFiles.Count : 0;

            if (StartSpace.I != null && StartSpace.I.gameObject.activeInHierarchy && StartSpace.I.songMenu != null)
            {
                if (AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                {
                    if (AlbumsManager.I != null)
                    {
                        AlbumsManager.I.CurrentAlbumSongs = CustomSongLoader.CustomAlbumInstance.SongsMetadata;
                    }

                    int targetIndex = CustomSongLoader.GetLatestSongIndex();
                    if (targetIndex >= 0)
                    {
                        CustomSongLoader.QueueDirectSelectionIndex(targetIndex);
                        StartSpace.I.songMenu.InitialOffset = 0;
                        string sName = targetIndex < CustomSongLoader.CustomAlbumInstance.builtInSongsMetadata.Count
                            ? CustomSongLoader.CustomAlbumInstance.builtInSongsMetadata[targetIndex].SongName
                            : $"Index {targetIndex}";
                        MelonLogger.Msg($"[CustomSongs] Auto-selecting newest song: '{sName}' (Index {targetIndex})");
                    }

                    StartSpace.I.songMenu.ChangeAlbum(CustomSongLoader.CustomAlbumInstance.SongsMetadata);
                    MelonLogger.Msg($"[CustomSongs] Turntable updated with {newCount} songs in real time!");
                }
            }
        }

        public override void OnUpdate()
        {
            CustomSongLoader.ProcessDeferredAlbumRegistration();

            // Real-time custom song import / reload
            if (Input.GetKeyDown(KeyCode.F5))
            {
                LoggerInstance.Msg("[CustomSongs] Key [F5]: Manual refresh requested!");
                PerformSongRefresh();
            }

            if (PendingReload && Time.unscaledTime >= ReloadTime)
            {
                PendingReload = false;
                PerformSongRefresh();
            }

            // SongMenu does not consistently request a preview when an album is opened
            // with a song already selected. Polling the selected custom song also covers
            // restoring the last album/song on startup, on both Mono and IL2CPP.
            UpdateCustomSongPreview();

            // VR Thumbstick & Keyboard song carousel navigation (with hold-acceleration)
            HandleSongMenuNavigation();

#if !QUEST_IL2CPP
            if (Input.GetKeyDown(KeyCode.F1))
            {
                DebugMode = !DebugMode;
                LoggerInstance.Msg($"[CustomSongs] Debug Mode is now {(DebugMode ? "ENABLED" : "DISABLED")}");
            }
#endif

            IntroSkipManager.Update();

#if !QUEST_IL2CPP
            if (!DebugMode) return;

            HandleCameraAndMovement();
            RefreshClickTargets();
            HandleActionKeybinds();
            HandleGuitarPlaying();
#endif
        }

        public override void OnLateUpdate()
        {
            Patch_SongMenuItem_SetSong.SyncTitleVisibility();
        }

        public override void OnApplicationQuit()
        {
            DiscordRpcManager.Shutdown();
            try
            {
                foreach (var p in Process.GetProcessesByName("UnityCrashHandler64"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        private static int _navHoldDir = 0;
        private static float _navHoldStartTime = 0f;
        private static float _navNextStepTime = 0f;
        private static string _lastMenuPreviewSongKey = null;
        private static string _pendingMenuPreviewSongKey = null;
        private static float _pendingMenuPreviewSince;

        private void UpdateCustomSongPreview()
        {
            try
            {
                var startSpace = StartSpace.I;
                var album = AlbumsManager.SelectedAlbum;
                if (startSpace == null || !startSpace.gameObject.activeInHierarchy ||
                    startSpace.songMenu == null || !startSpace.songMenu.gameObject.activeInHierarchy ||
                    album == null || album.AlbumID != "CustomSongsAlbum" || GameManager.IsPlaying)
                {
                    CustomSongLoader.StopCustomAudioPreview();
                    _lastMenuPreviewSongKey = null;
                    _pendingMenuPreviewSongKey = null;
                    return;
                }

                var selectedSong = startSpace.songMenu.SelectedSong;
                if (selectedSong == null || selectedSong.SongID == null ||
                    !selectedSong.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(selectedSong.FileName))
                {
                    _pendingMenuPreviewSongKey = null;
                    return;
                }

                string selectionKey = (selectedSong.SongID ?? "") + "|" + selectedSong.FileName;
                if (string.Equals(selectionKey, _lastMenuPreviewSongKey, StringComparison.Ordinal)) return;

                // Match the native carousel's settling delay. Starting a decoder
                // for every intermediate selection can stall the menu and play
                // snippets of songs that the user never stopped on.
                if (!string.Equals(selectionKey, _pendingMenuPreviewSongKey, StringComparison.Ordinal))
                {
                    _pendingMenuPreviewSongKey = selectionKey;
                    _pendingMenuPreviewSince = Time.unscaledTime;
                    return;
                }
                float settleDelay = Mathf.Max(startSpace.songMenu.SamplePlayDelay,
                    startSpace.songMenu.CarouselSpinningDelay);
                if (Time.unscaledTime - _pendingMenuPreviewSince < Mathf.Max(0f, settleDelay)) return;

                var player = GameManager.I != null ? GameManager.I.musicPlayer : null;
                if (player == null) player = UnityEngine.Object.FindObjectOfType<Mp3Player>();
                if (player == null || player.MusicAudioSource == null ||
                    CustomSongLoader.ResolveUsmPath(selectedSong.FileName) == null) return;

                _lastMenuPreviewSongKey = selectionKey;
                CustomSongLoader.PlayCustomAudioSample(player, selectedSong.FileName);
            }
            catch (Exception ex)
            {
                _lastMenuPreviewSongKey = null;
                _pendingMenuPreviewSongKey = null;
                if (ConfigVerbose) MelonLogger.Warning($"[CustomSongs] Could not update selected-song preview: {ex.Message}");
            }
        }

        private void HandleSongMenuNavigation()
        {
            if (StartSpace.I == null || !StartSpace.I.gameObject.activeInHierarchy)
            {
                _navHoldDir = 0;
                return;
            }

            var sm = StartSpace.I.songMenu;
            if (sm == null || !sm.gameObject.activeInHierarchy)
            {
                _navHoldDir = 0;
                return;
            }

            if (GameManager.IsPlaying)
            {
                _navHoldDir = 0;
                return;
            }

            // Do not cast SongMenu.songList to List<T>: Quest exposes an
            // Il2CppSystem.Collections.Generic.List<T> and that cast silently
            // disabled controller navigation on the IL2CPP build.
            try
            {
                if (sm.SelectedSong == null)
                {
                    _navHoldDir = 0;
                    return;
                }
            }
            catch
            {
                _navHoldDir = 0;
                return;
            }

            // Gather horizontal stick / key input
            float stickX = 0f;

            // 1. OVRInput (Oculus / Quest Touch Controllers thumbsticks)
            try
            {
                Vector2 ovrL = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.All);
                if (Mathf.Abs(ovrL.x) > Mathf.Abs(stickX)) stickX = ovrL.x;

                Vector2 ovrR = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Controller.All);
                if (Mathf.Abs(ovrR.x) > Mathf.Abs(stickX)) stickX = ovrR.x;
            }
            catch { }

            // 2. Keyboard keys: Arrow keys (Left/Right) and Z/X
            if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.X))
            {
                stickX = 1f;
            }
            else if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.Z))
            {
                stickX = -1f;
            }

            const float deadZone = 0.45f;
            int currentDir = 0;
            if (stickX > deadZone) currentDir = 1;      // Next song (Right)
            else if (stickX < -deadZone) currentDir = -1; // Prev song (Left)

            if (currentDir != 0)
            {
                if (_navHoldDir != currentDir)
                {
                    // New direction triggered: immediate single step
                    _navHoldDir = currentDir;
                    _navHoldStartTime = Time.unscaledTime;
                    _navNextStepTime = Time.unscaledTime + 0.38f; // Initial delay to prevent accidental multi-steps

                    TriggerSongStep(sm, currentDir);
                    if (ConfigVerbose)
                    {
                        MelonLogger.Msg($"[CustomSongs][Nav] Stick/key x={stickX:F2}, direction={(currentDir > 0 ? "next" : "previous")}, selected='{sm.SelectedSong?.SongName}'.");
                    }
                }
                else
                {
                    // Continued hold in the same direction: check repeat timer with acceleration
                    if (Time.unscaledTime >= _navNextStepTime)
                    {
                        TriggerSongStep(sm, currentDir);

                        float holdDuration = Time.unscaledTime - _navHoldStartTime;
                        // Smoothly accelerate repeat rate from 0.22s down to 0.045s (~22 songs/sec)
                        float factor = Mathf.Clamp01((holdDuration - 0.38f) / 2.0f);
                        float interval = Mathf.Lerp(0.22f, 0.045f, factor);
                        _navNextStepTime = Time.unscaledTime + interval;
                    }
                }
            }
            else
            {
                _navHoldDir = 0;
                _navHoldStartTime = 0f;
                _navNextStepTime = 0f;
            }
        }

        private static void TriggerSongStep(SongMenu sm, int dir)
        {
            try
            {
                if (dir > 0)
                {
                    Traverse.Create(sm).Method("Prev_Pressed").GetValue();
                }
                else
                {
                    Traverse.Create(sm).Method("Next_Pressed").GetValue();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Error navigating song: {ex.Message}");
            }
        }

        public static SongMappingMetaData GetSafeSelectedSong()
        {
            try
            {
                if (StartSpace.I != null && StartSpace.I.songMenu != null && StartSpace.I.gameObject.activeInHierarchy)
                {
#if QUEST_IL2CPP
                    var list = GameFieldAccess.Get<Il2CppSystem.Collections.Generic.List<SongMappingMetaData>>(StartSpace.I.songMenu, "songList");
#else
                    var list = GameFieldAccess.Get<List<SongMappingMetaData>>(StartSpace.I.songMenu, "songList");
#endif
                    if (list != null && list.Count > 0)
                    {
                        return StartSpace.I.songMenu.SelectedSong;
                    }
                }
            }
            catch { }
            return null;
        }

        private static Camera GetCam()
        {
            return Camera.main != null ? Camera.main : CameraManager.Main;
        }

        // Calculates the true letterboxed viewport of the VR left eye within the desktop mirror window.
        public static Rect GetEyeViewportRect(Camera cam)
        {
            float eyeAspect = 1f;
            if (cam != null && cam.stereoEnabled)
            {
                Matrix4x4 p = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                if (Mathf.Abs(p[0, 0]) > 0.0001f)
                {
                    eyeAspect = Mathf.Abs(p[1, 1] / p[0, 0]);
                }
            }
            else if (cam != null)
            {
                eyeAspect = cam.aspect;
            }

            float winAspect = (float)Screen.width / (float)Screen.height;
            if (winAspect > eyeAspect)
            {
                float vpWidth = Screen.height * eyeAspect;
                float barW = (Screen.width - vpWidth) * 0.5f;
                return new Rect(barW, 0f, vpWidth, Screen.height);
            }
            else
            {
                float vpHeight = Screen.width / eyeAspect;
                float barH = (Screen.height - vpHeight) * 0.5f;
                return new Rect(0f, barH, Screen.width, vpHeight);
            }
        }

        // Projects a world point onto the desktop mirror window accurately at all camera positions and rotations.
        private static bool WorldToGui(Camera cam, Vector3 world, out Vector2 gui)
        {
            gui = Vector2.zero;
            if (cam == null) return false;
            Vector3 vp = cam.WorldToViewportPoint(world);
            if (vp.z <= 0.05f) return false; // Behind camera

            Rect eyeRect = GetEyeViewportRect(cam);
            gui = new Vector2(eyeRect.x + vp.x * eyeRect.width, eyeRect.y + (1f - vp.y) * eyeRect.height);
            return vp.x >= -0.1f && vp.x <= 1.1f && vp.y >= -0.1f && vp.y <= 1.1f;
        }

#if !QUEST_IL2CPP
        private static void RefreshClickTargets()
        {
            Camera cam = GetCam();
            if (cam == null) { targets.Clear(); return; }

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 0.5f;
                targets.Clear();
                foreach (var b in UnityEngine.Object.FindObjectsOfType<PressableButton>())
                {
                    if (b == null || !b.isActiveAndEnabled || !b.gameObject.activeInHierarchy) continue;
                    string label = !string.IsNullOrEmpty(b.Text) ? b.Text : b.gameObject.name;
                    // Filter out DLC shop background items to eliminate clutter
                    if (label.Equals("open", StringComparison.OrdinalIgnoreCase) || label.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)) continue;
                    targets.Add(new ClickTarget { Button = b, Label = label });
                }
            }

            Vector3 camPos = cam.transform.position;
            foreach (var t in targets)
            {
                if (t.Button == null) { t.OnScreen = false; continue; }
                var col = t.Button.GetComponentInChildren<Collider>();
                Vector3 pos = col != null ? col.bounds.center : t.Button.transform.position;
                t.Distance = Vector3.Distance(camPos, pos);
                t.OnScreen = WorldToGui(cam, pos, out t.GuiPos);
            }
        }

        public static void PressButton(PressableButton b, string label)
        {
            if (b == null) return;
            if (b.IsDisabled)
            {
                MelonLogger.Msg($"[CustomSongs] Button '{label}' is disabled, ignoring.");
                return;
            }
            MelonLogger.Msg($"[CustomSongs] Pressing button: {label}");
            try
            {
                // Same path the game uses when a hand presses the button (raises Pressed + OnPressed)
                b.StartCoroutine((IEnumerator)WaitToRaisePressedMI.Invoke(b, null));
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] PressButton failed for '{label}': {ex.Message}");
            }
        }

                public override void OnGUI()
        {
            if (menuLoadedTime > 0f && Time.unscaledTime - menuLoadedTime < 8f)
            {
                GUI.color = new Color(0.2f, 1f, 0.2f, 1f);
                GUI.Label(new Rect(20, Screen.height - 80, 800, 60), "<size=40><b>Custom Song Loader loaded!</b></size>");
                GUI.color = Color.white;
            }

            if (!DebugMode) return;

            Event e = Event.current;

            // ---- HUD help ----
            GUI.color = Color.white;
            string vol = ForcedGuitarVolume >= 0f ? $"<color=yellow>FORZADO {ForcedGuitarVolume:P0}</color>" : "normal";
            SongMappingMetaData sel = GameManager.CurrentSongData != null ? GameManager.CurrentSongData : GetSafeSelectedSong();
            string currentSongText = "";
            if (sel != null)
            {
                string trackType = sel.IsMultiTrack ? "<color=#00FF66>[MultiTrack: YES]</color>" : "<color=#FFAA00>[MultiTrack: NO]</color>";
                currentSongText = $" | Song: <color=cyan><b>{sel.BandName} - {sel.SongName}</b></color> {trackType}";
            }

            GUI.Label(new Rect(15, 12, Screen.width - 30, 22), "<b>[WASD/QE]</b> Move | <b>[Right Click + Mouse]</b> Look | <b>[Left Click]</b> Press Button | <b>[Z/X or ←/→]</b> Change Song" + currentSongText);
            string modeLabel = GamePreferences.Load(UnpluggedInfo.IndexHandsMode, defaultValue: false) ? "<color=#FFD700>HANDS</color>" : "<color=#00E5FF>CONTROLLER</color>";
            GUI.Label(new Rect(15, 34, Screen.width - 30, 22), $"<b>[1 or Enter]</b> Start | <b>[2]</b> AutoPlay: {(AutoPlay ? "<color=green>ON</color>" : "<color=yellow>MANUAL</color>")} | <b>[H]</b> Mode: {modeLabel} | <b>[Space]</b> Strum | <b>[3]</b> Skip Intro | <b>[End/N]</b> Finish | <b>[Tab]</b> Buttons | <b>[F1]</b> Debug");

            if (IntroSkipManager.CanSkip && !IntroSkipManager.HasSkipped)
            {
                float bw = 560f;
                float bh = 34f;
                Rect skipRect = new Rect((Screen.width - bw) / 2f, Screen.height - 70f, bw, bh);
                GUI.Box(skipRect, "");
                var oldC = GUI.color;
                GUI.color = new Color(1f, 0.88f, 0.2f);
                GUI.Label(skipRect, "<b>PRESS A + X OR [3] TO SKIP INTRO SILENCE</b>");
                GUI.color = oldC;
            }

            // ---- Button list panel (opened with Tab) ----
            panelRect = new Rect(0, 0, 0, 0);
            if (ShowButtonPanel)
            {
                var visible = targets.Where(t => t.Button != null && t.Distance < 6f).OrderBy(t => t.Distance).Take(20).ToList();
                float w = 260f;
                float h = Mathf.Min(40f + visible.Count * 26f, Screen.height - 110f);
                panelRect = new Rect(Screen.width - w - 10f, 70f, w, h);
                GUI.Box(panelRect, "Detected Buttons (click)");
                Rect view = new Rect(0, 0, w - 20f, visible.Count * 26f);
                panelScroll = GUI.BeginScrollView(new Rect(panelRect.x + 5f, panelRect.y + 25f, w - 10f, h - 30f), panelScroll, view);
                for (int i = 0; i < visible.Count; i++)
                {
                    var t = visible[i];
                    string txt = $"{t.Label}  ({t.Distance:F1}m){(t.Button.IsDisabled ? " [off]" : "")}";
                    if (GUI.Button(new Rect(0, i * 26f, w - 25f, 24f), txt))
                    {
                        PressButton(t.Button, t.Label);
                    }
                }
                GUI.EndScrollView();
            }

            // ---- On-screen markers where each button projects (toggle with [6]) ----
            if (ShowMarkers)
            {
                foreach (var t in targets)
                {
                    if (t.Button == null || !t.OnScreen || t.Distance > 6f) continue;
                    GUI.color = t.Button.IsDisabled ? Color.gray : Color.yellow;
                    GUI.DrawTexture(new Rect(t.GuiPos.x - 4f, t.GuiPos.y - 4f, 8f, 8f), Texture2D.whiteTexture);
                    GUI.Label(new Rect(t.GuiPos.x + 6f, t.GuiPos.y - 10f, 220f, 20f), t.Label);
                }
                GUI.color = Color.white;
            }

            // ---- Mouse click: accurate 2D screen picking + 3D raycast ----
            if (e.type == EventType.MouseDown && e.button == 0 && (!ShowButtonPanel || !panelRect.Contains(e.mousePosition)))
            {
                Camera cam = GetCam();
                ClickTarget best = null;
                float bestDist = 65f; // pixels in desktop window
                foreach (var t in targets)
                {
                    if (t.Button == null || !t.OnScreen) continue;
                    float d = Vector2.Distance(t.GuiPos, e.mousePosition);
                    if (d < bestDist) { bestDist = d; best = t; }
                }

                if (best != null)
                {
                    PressButton(best.Button, best.Label);
                    e.Use();
                }
                else if (cam != null)
                {
                    // Direct camera raycast through mouse position
                    Rect eyeRect = GetEyeViewportRect(cam);
                    if (eyeRect.Contains(e.mousePosition))
                    {
                        float vx = (e.mousePosition.x - eyeRect.x) / eyeRect.width;
                        float vy = (e.mousePosition.y - eyeRect.y) / eyeRect.height;
                        Ray ray = cam.ViewportPointToRay(new Vector3(vx, 1f - vy, 0f));
                        RaycastHit[] hits = Physics.RaycastAll(ray, 25f);
                        foreach (var hit in hits.OrderBy(h => h.distance))
                        {
                            var btn = hit.collider.GetComponentInParent<PressableButton>() ?? hit.collider.GetComponentInChildren<PressableButton>();
                            if (btn != null && !btn.IsDisabled)
                            {
                                string label = !string.IsNullOrEmpty(btn.Text) ? btn.Text : btn.gameObject.name;
                                PressButton(btn, label);
                                e.Use();
                                break;
                            }
                        }
                    }

                    // Also check floating pick in PreparationSpace
                    var prep = UnityEngine.Object.FindObjectOfType<PreparationSpace>();
                    if (prep != null && prep.gameObject.activeInHierarchy && prep.PickGrab != null)
                    {
                        if (WorldToGui(cam, prep.PickGrab.transform.position, out Vector2 pg) && Vector2.Distance(pg, e.mousePosition) < 120f)
                        {
                            ConfirmAndStartPreparation(prep);
                            e.Use();
                        }
                    }
                }
            }
        }
#endif

        private void HandleCameraAndMovement()
        {
            Transform camT = Camera.main != null ? Camera.main.transform : (CameraManager.Main != null ? CameraManager.Main.transform : null);
            Transform rig = InteractionSystem.I != null ? InteractionSystem.I.AnchorPlayer : null;

            if (camT == null) return;

            if (!initializedAngles)
            {
                yaw = camT.eulerAngles.y;
                pitch = camT.eulerAngles.x;
                initializedAngles = true;
            }

            // Mouse Look (Hold Right Mouse Button)
            if (Input.GetMouseButton(1))
            {
                float mouseX = Input.GetAxis("Mouse X") * 2.5f;
                float mouseY = -Input.GetAxis("Mouse Y") * 2.5f;

                yaw += mouseX;
                pitch = Mathf.Clamp(pitch + mouseY, -85f, 85f);

                if (rig != null)
                {
                    rig.rotation = Quaternion.Euler(pitch, yaw, 0f);
                }
                else
                {
                    camT.rotation = Quaternion.Euler(pitch, yaw, 0f);
                }
            }

            // WASD Movement
            float speed = Input.GetKey(KeyCode.LeftShift) ? 6f : 3f;
            Vector3 forward = camT.forward;
            forward.y = 0;
            forward.Normalize();
            Vector3 right = camT.right;
            right.y = 0;
            right.Normalize();

            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += forward;
            if (Input.GetKey(KeyCode.S)) move -= forward;
            if (Input.GetKey(KeyCode.D)) move += right;
            if (Input.GetKey(KeyCode.A)) move -= right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) move -= Vector3.up;

            if (move.sqrMagnitude > 0.001f)
            {
                Vector3 delta = move.normalized * speed * Time.deltaTime;
                if (rig != null)
                {
                    rig.position += delta;
                }
                else
                {
                    camT.position += delta;
                }
            }
        }

        // =========================================================================
        // CENTRALIZED DEBUG & CONTROLLER KEYBINDS REFERENCE (NON-CONFLICTING)
        // =========================================================================
        // Camera & Movement:
        //   - [W] [A] [S] [D]  : Fly camera Forward / Left / Backward / Right
        //   - [E] / [Q] [Ctrl] : Fly camera Up / Down
        //   - [Mouse Right]    : Hold and drag to rotate camera view
        //   - [Mouse Left]     : Click on 3D in-game buttons
        //
        // Song Carousel Navigation (Turntable):
        //   - [Z] / [LeftArrow]  : Previous Song
        //   - [X] / [RightArrow] : Next Song
        //   - [C]                : Select Custom Songs Album
        //
        // Gameplay & Song Flow:
        //   - [1] / [Enter]    : Confirm floating pick / Start Song
        //   - [2]              : Toggle AutoPlay Bot (Green = ON, Yellow = MANUAL)
        //   - [3]              : Skip Intro Silence (fast-forward to 2.5s before 1st note)
        //   - [H]              : Toggle Gameplay Mode (Hands Mode vs Controller Mode)
        //   - [Space] / [Down] : Strum guitar (test hit)
        //   - [End] / [N]      : Finish / Skip current song to score screen
        //   - [Esc] / [P]      : Pause / Resume song
        //
        // Tools & Diagnostics:
        //   - [4]              : Reset Audio Settings to Windows Default
        //   - [5]              : Print Debug Status to MelonLoader console
        //   - [6]              : Toggle projected 2D button markers
        //   - [Tab]            : Toggle clickable button list panel
        //   - [F1]             : Toggle Debug Mode & HUD on/off
        //   - [F5]             : Refresh and reload custom songs from disk
        // =========================================================================
        private void HandleActionKeybinds()
        {
            // [1] or [Enter]: Confirm Preparation / Start Song
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1) || Input.GetKeyDown(KeyCode.Return))
            {
                var prep = UnityEngine.Object.FindObjectOfType<PreparationSpace>();
                if (prep != null && prep.gameObject.activeInHierarchy)
                {
                    ConfirmAndStartPreparation(prep);
                    return;
                }

                if (StartSpace.I != null && StartSpace.I.songMenu != null && StartSpace.I.gameObject.activeInHierarchy)
                {
                    var selected = GetSafeSelectedSong();
                    if (selected != null)
                    {
                        GameManager.CurrentSongData = selected;
                        LoggerInstance.Msg($"[CustomSongs] Key [1]/Enter: Starting selected song '{selected.BandName} - {selected.SongName}'...");
                        Traverse.Create(StartSpace.I).Method("StartButton_Pressed").GetValue();
                    }
                }
            }

            // [2]: Toggle AutoPlay Bot
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                AutoPlay = !AutoPlay;
                LoggerInstance.Msg($"[CustomSongs] Key [2]: AutoPlay Bot is now {(AutoPlay ? "ENABLED" : "DISABLED (Manual Strumming)")}");
            }

            // [3]: Skip Intro Silence (fast-forwards to 2.5s before first note)
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                if (IntroSkipManager.CanSkip && !IntroSkipManager.HasSkipped)
                {
                    LoggerInstance.Msg("[CustomSongs] Key [3]: Skip Intro requested!");
                    IntroSkipManager.PerformSkip();
                }
                else
                {
                    LoggerInstance.Msg("[CustomSongs] Key [3]: Skip Intro not available (not in intro or already skipped).");
                }
            }

            // [H]: Toggle Hands Mode vs Controller Mode (Index Hands vs Mandos)
#if !QUEST_IL2CPP
            if (Input.GetKeyDown(KeyCode.H))
            {
                bool currentMode = GamePreferences.Load(UnpluggedInfo.IndexHandsMode, defaultValue: false);
                bool newMode = !currentMode;
                GamePreferences.Save(UnpluggedInfo.IndexHandsMode, (object)newMode, true);
                string modeStr = newMode ? "HANDS / INDEX TRACKING MODE" : "CONTROLLER / MANDOS MODE";
                LoggerInstance.Msg($"[CustomSongs] Key [H]: Switched gameplay mode to: {modeStr}. (Select a song or restart track to apply)");
            }
#endif

            // [4]: Reset Audio Settings to Windows Default
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
            {
                try
                {
                    AudioSettings.Reset(AudioSettings.GetConfiguration());
                    LoggerInstance.Msg("[CustomSongs] Key [4]: AudioSettings Reset to current Windows default audio device!");
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"Error resetting audio: {ex}");
                }
            }

            // [5]: Debug Status Print
            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5))
            {
                PrintDebugStatus();
            }

            // [6]: Toggle projected button markers
            if (Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6))
            {
                ShowMarkers = !ShowMarkers;
                LoggerInstance.Msg($"[CustomSongs] Key [6]: Marcadores en pantalla = {ShowMarkers}");
            }

            // [Tab]: Toggle clickable button list
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                ShowButtonPanel = !ShowButtonPanel;
            }


            // [C]: Select Custom Songs in StartSpace
            if (Input.GetKeyDown(KeyCode.C))
            {
                if (StartSpace.I != null && CustomSongLoader.CustomAlbumInstance != null)
                {
                    LoggerInstance.Msg("[CustomSongs] Key [C]: Selecting Custom Songs Album...");
                    Traverse.Create(StartSpace.I).Method("AlbumSelected", new object[] { CustomSongLoader.CustomAlbumInstance }).GetValue();
                }
            }

            // [End] or [N]: Skip current song / finish immediately
            if (Input.GetKeyDown(KeyCode.End) || Input.GetKeyDown(KeyCode.N))
            {
                if (GameManager.I != null && GameManager.IsPlaying)
                {
                    LoggerInstance.Msg("[CustomSongs] Key [End/N]: Skipping current song to end / Finish!");
                    try
                    {
                        var mp = GameManager.I.musicPlayer;
                        float songLen = 180f;
                        if (mp != null && mp.MusicAudioSource != null && mp.MusicAudioSource.clip != null)
                        {
                            songLen = mp.MusicAudioSource.clip.length;
                        }
                        else if (mp != null && mp.AudioSources != null)
                        {
                            foreach (var s in mp.AudioSources)
                            {
                                if (s != null && s.clip != null && s.clip.length > 5f)
                                {
                                    songLen = s.clip.length;
                                    break;
                                }
                            }
                        }

                        float target = Mathf.Max(2.5f, songLen - 0.5f);
                        if (mp != null)
                        {
                            if (mp.MusicAudioSource != null && mp.MusicAudioSource.clip != null)
                            {
                                mp.MusicAudioSource.time = Mathf.Clamp(target, 0f, mp.MusicAudioSource.clip.length - 0.1f);
                            }
                            mp.CurrentTime = target;
                            mp.SongTimeDelayCounter = GameManager.I.StartDelay + target;
                            if (mp.AudioSources != null)
                            {
                                foreach (var s in mp.AudioSources)
                                {
                                    if (s != null && s.clip != null && s.clip.length > target)
                                        s.time = target;
                                }
                            }
                        }
                        if (GameManager.I.TimelineManager != null)
                        {
                            GameManager.I.TimelineManager.SetTime(target);
                        }
                        Traverse.Create(GameManager.I).Method("Finish").GetValue();
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"[CustomSongs] Error skipping song to end: {ex.Message}");
                    }
                }
            }

            // [Escape] or [P]: Unpause if in pause menu
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.P))
            {
                var pause = UnityEngine.Object.FindObjectOfType<PauseSpace>();
                if (pause != null && pause.gameObject.activeInHierarchy)
                {
                    LoggerInstance.Msg("[CustomSongs] Key [Escape/P]: Resuming game via PauseSpace.BeginContinue()...");
                    Traverse.Create(pause).Method("BeginContinue").GetValue();
                }
            }
        }

        public static void ConfirmAndStartPreparation(PreparationSpace prep)
        {
            MelonLogger.Msg("[CustomSongs] Confirming Preparation: Closing guitar rack and starting song!");
            try
            {
                if (prep.audioReady != null) prep.audioReady.Play();
                Traverse.Create(prep).Method("PickGrab_Grabbed", new object[] { null }).GetValue();
                Traverse.Create(prep).Method("SendPick").GetValue();
            }
            catch { }

            // Hide the preparation rack and floating UI
            if (prep.InstrumentMenu != null) prep.InstrumentMenu.gameObject.SetActive(false);
            if (prep.PickGrabUI != null) prep.PickGrabUI.SetActive(false);
            if (prep.PickGrab != null) prep.PickGrab.gameObject.SetActive(false);
            prep.gameObject.SetActive(false);

            if (prep.PlayerReady != null)
            {
                prep.PlayerReady.Invoke();
            }
            else if (GameManager.I != null)
            {
                GameManager.I.StartSong();
            }
        }

        private static void HitNote(GuitarGameManager guitarMgr, TierNoteBase note)
        {
            note.RaiseSuccessed(guitarMgr.gameTimeLine.CurrentTime);
            Traverse.Create(guitarMgr).Method("NotePlayedHandlerInvoke", new object[] { note, FeedbackType.Awesome }).GetValue();
            if (ForcedGuitarVolume < 0f)
            {
                guitarMgr.InstrumentVolumeTarget = 1.0f;
                guitarMgr.InstrumentVolumeSmooth = 1.0f;
            }
            if (note.Tier < guitarMgr.Frets.Count && guitarMgr.Frets[note.Tier] != null)
            {
                guitarMgr.SelectedFret = guitarMgr.Frets[note.Tier];
                guitarMgr.SelectedFret?.SetFeedback(FeedbackType.Awesome);
            }
        }

        private void HandleGuitarPlaying()
        {
            if (!GameManager.IsPlaying) return;

            var guitarMgr = UnityEngine.Object.FindObjectOfType<GuitarGameManager>();
            if (guitarMgr == null || guitarMgr.gameTimeLine == null) return;

            if (AutoPlay)
            {
                var ready = guitarMgr.gameTimeLine.NotesReadyToInteract;
                if (ready != null)
                {
                    for (int i = 0; i < ready.Count; i++)
                    {
                        if (ready[i] is TierNoteBase tierNote && !tierNote.Played)
                        {
                            HitNote(guitarMgr, tierNote);
                        }
                    }
                }
                return;
            }

            // Manual strumming
            bool strumPressed = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.DownArrow);
            if (!strumPressed) return;

            float now = guitarMgr.gameTimeLine.CurrentTime;
            var closer = guitarMgr.gameTimeLine.CloserInteractiveNote as TierNoteBase;
            if (closer != null && !closer.Played && Mathf.Abs(closer.Time - now) <= StrumWindow)
            {
                HitNote(guitarMgr, closer);
                LoggerInstance.Msg($"[CustomSongs] Manual Strum: HIT tier {closer.Tier} (offset {(now - closer.Time) * 1000f:F0} ms)");
            }
            else
            {
                // Strum with no note in the window = gentle miss drop (0.4f instead of 0.1f so sound doesn't cut out completely)
                if (ForcedGuitarVolume < 0f)
                {
                    guitarMgr.InstrumentVolumeTarget = 0.4f;
                    guitarMgr.InstrumentVolumeSmooth = 0.4f;
                }
                LoggerInstance.Msg("[CustomSongs] Manual Strum: MISS (no note within window)");
            }
        }

        private void PrintDebugStatus()
        {
            LoggerInstance.Msg("=== [DEBUG STATUS] ===");
            try
            {
                LoggerInstance.Msg($"GameState: {GameStateManager.I?.CurrentGameState}, AutoPlay: {AutoPlay}");
                if (StartSpace.I != null)
                {
                    LoggerInstance.Msg($"StartSpace: Active, CurrentAlbum={AlbumsManager.SelectedAlbum?.Name}");
                    if (StartSpace.I.songMenu != null)
                    {
                        var sm = StartSpace.I.songMenu;
                        LoggerInstance.Msg($"SongMenu: SelectedIndex={sm.SelectedIndex}, SelectedSong={sm.SelectedSong?.SongName} ({sm.SelectedSong?.BandName})");
                    }
                }
                var prep = UnityEngine.Object.FindObjectOfType<PreparationSpace>();
                if (prep != null && prep.gameObject.activeInHierarchy)
                {
                    LoggerInstance.Msg("PreparationSpace: ACTIVE (floating pick waiting for [1])");
                }
                if (GameManager.I != null && GameManager.I.musicPlayer != null)
                {
                    var mp = GameManager.I.musicPlayer;
                    var guitarMgr = UnityEngine.Object.FindObjectOfType<GuitarGameManager>();
                    LoggerInstance.Msg($"MusicPlayer: IsPlaying={GameManager.IsPlaying}, IsMultiTrack={mp.IsMultiTrack}, CurrentTime={mp.CurrentTime:F2}, GuitarTargetVol={guitarMgr?.InstrumentVolumeTarget:F2}");
                    for (int i = 0; i < mp.AudioSources.Count; i++)
                    {
                        var src = mp.AudioSources[i];
                        if (src != null)
                        {
                            string mixer = src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : "none";
                            LoggerInstance.Msg($"  Track [{(Mp3Player.SongTrackNames)i}]: isPlaying={src.isPlaying}, vol={src.volume:F2}, 2D={src.spatialBlend == 0f}, mixer={mixer}, time={src.time:F2}, clip={src.clip?.name ?? "null"}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Error in PrintDebugStatus: {ex}");
            }
            LoggerInstance.Msg("======================");
        }
    }

    public static class CustomSongLoader
    {
        private static int ChartOutcomeLogCount;

        public static string CustomSongsFolder
        {
            get
            {
                if (Application.platform == RuntimePlatform.Android)
                {
                    string packageName = string.IsNullOrWhiteSpace(Application.identifier)
                        ? "com.Anotherway.UnpluggedVR.modded"
                        : Application.identifier;
                    string modData = $"/sdcard/ModData/{packageName}/Songs";
                    if (!Directory.Exists(modData))
                    {
                        try { Directory.CreateDirectory(modData); }
                        catch (Exception ex) { MelonLogger.Error($"[CustomSongs] Could not create songs folder '{modData}': {ex}"); }
                    }
                    return modData;
                }
                return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CustomSongs");
            }
        }
        public static DLCAlbumData CustomAlbumInstance;

        private static FileSystemWatcher watcher;
        private static readonly Dictionary<string, Texture2D> CoverTextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> TrackDisplayLabelCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private sealed class SongCoverRequest
        {
            public SongMenuItem Item;
            public SongMappingMetaData Song;
            public string Key;
        }
        private static readonly Queue<SongCoverRequest> PendingSongCoverLoads = new Queue<SongCoverRequest>();
        private static readonly HashSet<string> PendingSongCoverKeys = new HashSet<string>(StringComparer.Ordinal);
        private static bool _songCoverLoaderRunning;
        private static Task _startupMetadataScanTask;
        private static AlbumsManager _pendingAlbumManager;
        private static bool _pendingSavedAlbumRestore;
        private sealed class ArchiveSongMetadata
        {
            public string FileVersion;
            public string SongName;
            public string BandName;
            public string AudioLayout;
            public string TrackDisplayLabel;
            public float InitialDelay = 4f;
            public bool IsMultiTrack;
        }
        private sealed class ParsedUsmMetadata
        {
            public string SongName = string.Empty;
            public string BandName = string.Empty;
            public string AudioLayout = string.Empty;
            public float InitialDelay = 4f;
            public bool? IsMultiTrack;
            public bool IsIdealForUnplugged;
            public int ChartParserVersion;
            public float ChartCoverageRatio;
        }
        private static readonly Dictionary<string, ArchiveSongMetadata> ArchiveSongMetadataCache = new Dictionary<string, ArchiveSongMetadata>(StringComparer.OrdinalIgnoreCase);
        public const string LastCustomSongFilePreference = "USM.LastCustomSongFile";
        private static bool _hasPendingDirectSelection;
        private static int _pendingDirectSelectionIndex;
        private static AlbumData _lastPopulatedAlbum;
        private static string _lastPopulatedSongsFingerprint;
        private static string ActivePreviewArchivePath;
        private static string PendingPreviewArchivePath;
        private static int PreviewLoadGeneration;

        public static string ResolveUsmPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            try
            {
                if (File.Exists(fileName)) return Path.GetFullPath(fileName);

                string candidate = Path.Combine(CustomSongsFolder, Path.GetFileName(fileName));
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not resolve .usm path '{fileName}': {ex.Message}");
            }

            return null;
        }

        private static System.IO.Compression.ZipArchiveEntry FindArchiveEntry(System.IO.Compression.ZipArchive archive, params string[] names)
        {
            if (archive == null || names == null) return null;

            // Honor the caller's requested difficulty / preferred format. Searching
            // entries first made ZIP insertion order win, so an Easy request could
            // silently load Normal or Hard when those entries appeared earlier.
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var match = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(Path.GetFileName(entry.FullName), Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return null;
        }

        private static Texture2D LoadArchiveCover(string archivePath)
        {
            string cacheKey = GetArchiveFileVersion(archivePath);

            if (CoverTextureCache.TryGetValue(cacheKey, out Texture2D cached) && cached != null)
                return cached;

            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                {
                    var entry = FindArchiveEntry(archive, "cover.jpg", "cover.jpeg", "cover.png", "album.jpg", "album.jpeg", "album.png");
                    if (entry == null)
                    {
                        MelonLogger.Warning($"[CustomSongs] No cover image found inside '{Path.GetFileName(archivePath)}'.");
                        return null;
                    }

                    using (var stream = entry.Open())
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(texture, memory.ToArray()))
                        {
                            UnityEngine.Object.Destroy(texture);
                            MelonLogger.Warning($"[CustomSongs] Could not decode '{entry.FullName}' inside '{Path.GetFileName(archivePath)}'.");
                            return null;
                        }

                        texture.name = Path.GetFileNameWithoutExtension(archivePath);
                        CoverTextureCache[cacheKey] = texture;
                        MelonLogger.Msg($"[CustomSongs] Cover loaded from archive: {Path.GetFileName(archivePath)}!{entry.FullName} ({texture.width}x{texture.height})");
                        return texture;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not load cover from '{Path.GetFileName(archivePath)}': {ex.Message}");
                return null;
            }
        }

        private static bool ArchiveHasAudioStems(string archivePath)
        {
            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                {
                    return FindArchiveEntry(archive, "song-guitar.ogg", "song-guitar.mp3", "song-bass.ogg", "song-bass.mp3",
                        "song-drums.ogg", "song-drums.mp3", "song-vocals.ogg", "song-vocals.mp3",
                        "song-other.ogg", "song-other.mp3") != null;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not inspect audio stems in '{Path.GetFileName(archivePath)}': {ex.Message}");
                return false;
            }
        }

        private static string ReadAudioLayout(string archivePath)
        {
            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                {
                    var entry = FindArchiveEntry(archive, "meta.json");
                    if (entry == null) return string.Empty;

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        return ParseUsmMetadata(reader.ReadToEnd()).AudioLayout;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not read audio layout from '{Path.GetFileName(archivePath)}': {ex.Message}");
                return string.Empty;
            }
        }

        private static string ReadTrackDisplayLabel(string archivePath)
        {
            return ReadArchiveSongMetadata(archivePath).TrackDisplayLabel;
        }

        private static string GetArchiveFileVersion(string archivePath)
        {
            try
            {
                var file = new FileInfo(archivePath);
                return Path.GetFullPath(archivePath) + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return archivePath ?? string.Empty;
            }
        }

        private static ArchiveSongMetadata ReadArchiveSongMetadata(string archivePath)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(archivePath); }
            catch { fullPath = archivePath ?? string.Empty; }
            string version = GetArchiveFileVersion(archivePath);
            if (ArchiveSongMetadataCache.TryGetValue(fullPath, out var cached) && cached != null && cached.FileVersion == version)
                return cached;

            var metadata = new ArchiveSongMetadata
            {
                FileVersion = version,
                SongName = Path.GetFileNameWithoutExtension(archivePath),
                BandName = "Custom Artist"
            };
            int audioStemCount = 0;
            bool hasGuitar = false;
            bool hasVocals = false;

            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string name = Path.GetFileName(entry.FullName);
                        string extension = Path.GetExtension(name);
                        if (!extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) &&
                            !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!name.StartsWith("song-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        audioStemCount++;
                        if (name.StartsWith("song-guitar.", StringComparison.OrdinalIgnoreCase)) hasGuitar = true;
                        if (name.StartsWith("song-vocals.", StringComparison.OrdinalIgnoreCase)) hasVocals = true;
                    }

                    var entryMeta = FindArchiveEntry(archive, "meta.json");
                    if (entryMeta != null)
                    {
                        using (var reader = new StreamReader(entryMeta.Open()))
                        {
                            ParsedUsmMetadata parsed = ParseUsmMetadata(reader.ReadToEnd());
                            if (!string.IsNullOrWhiteSpace(parsed.SongName))
                                metadata.SongName = parsed.SongName;
                            if (!string.IsNullOrWhiteSpace(parsed.BandName))
                                metadata.BandName = parsed.BandName;
                            metadata.InitialDelay = parsed.InitialDelay;
                            metadata.AudioLayout = parsed.AudioLayout;
                            metadata.IsMultiTrack = parsed.IsMultiTrack ?? false;

                            string layout = metadata.AudioLayout;
                            if (string.IsNullOrEmpty(layout))
                            {
                                layout = audioStemCount <= 1 ? "single-track"
                                    : hasGuitar && hasVocals && audioStemCount == 2 ? "two-track-instrumental-vocals"
                                    : "full-multitrack";
                                metadata.AudioLayout = layout;
                            }
                            if (!parsed.IsMultiTrack.HasValue)
                                metadata.IsMultiTrack = !string.Equals(layout, "single-track", StringComparison.OrdinalIgnoreCase);

                            metadata.TrackDisplayLabel = BuildTrackDisplayLabel(layout, parsed.IsIdealForUnplugged,
                                parsed.ChartParserVersion, parsed.ChartCoverageRatio);
                        }
                    }
                    else
                    {
                        metadata.IsMultiTrack = audioStemCount > 1;
                        metadata.AudioLayout = audioStemCount <= 1 ? "single-track"
                            : hasGuitar && hasVocals && audioStemCount == 2 ? "two-track-instrumental-vocals"
                            : "full-multitrack";
                        metadata.TrackDisplayLabel = BuildTrackDisplayLabel(metadata.AudioLayout, false, 0, 0f);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not read metadata from '{Path.GetFileName(archivePath)}': {ex.Message}");
                metadata.TrackDisplayLabel = BuildTrackDisplayLabel(metadata.AudioLayout, false, 0, 0f);
            }

            ArchiveSongMetadataCache[fullPath] = metadata;
            return metadata;
        }

        private static ParsedUsmMetadata ParseUsmMetadata(string json)
        {
            var parsed = new ParsedUsmMetadata();
#if QUEST_IL2CPP
            // Startup metadata scanning runs on a worker so dozens of .usm ZIPs
            // do not block Unity. SimpleJSON here is an IL2CPP game type and
            // must not be invoked from that worker; System.Text.Json is managed
            // and safe to use off the Unity thread.
            using (var document = System.Text.Json.JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return parsed;
                if (root.TryGetProperty("SongName", out var songName) && songName.ValueKind == System.Text.Json.JsonValueKind.String)
                    parsed.SongName = songName.GetString() ?? string.Empty;
                if (root.TryGetProperty("BandName", out var bandName) && bandName.ValueKind == System.Text.Json.JsonValueKind.String)
                    parsed.BandName = bandName.GetString() ?? string.Empty;
                if (root.TryGetProperty("AudioLayout", out var audioLayout) && audioLayout.ValueKind == System.Text.Json.JsonValueKind.String)
                    parsed.AudioLayout = audioLayout.GetString() ?? string.Empty;
                if (root.TryGetProperty("InitialDelay", out var initialDelay) && initialDelay.TryGetSingle(out float delay))
                    parsed.InitialDelay = delay;
                if (root.TryGetProperty("IsMultiTrack", out var isMultiTrack) &&
                    (isMultiTrack.ValueKind == System.Text.Json.JsonValueKind.True || isMultiTrack.ValueKind == System.Text.Json.JsonValueKind.False))
                    parsed.IsMultiTrack = isMultiTrack.GetBoolean();
                if (root.TryGetProperty("IsIdealForUnplugged", out var isIdeal) &&
                    (isIdeal.ValueKind == System.Text.Json.JsonValueKind.True || isIdeal.ValueKind == System.Text.Json.JsonValueKind.False))
                    parsed.IsIdealForUnplugged = isIdeal.GetBoolean();
                if (root.TryGetProperty("ChartParserVersion", out var parserVersion) && parserVersion.TryGetInt32(out int version))
                    parsed.ChartParserVersion = version;
                if (root.TryGetProperty("ChartCoverageRatio", out var coverage) && coverage.TryGetSingle(out float ratio))
                    parsed.ChartCoverageRatio = ratio;
            }
#else
            var root = JSON.Parse(json);
            if (root == null) return parsed;
            parsed.SongName = root["SongName"]?.Value ?? string.Empty;
            parsed.BandName = root["BandName"]?.Value ?? string.Empty;
            parsed.AudioLayout = root["AudioLayout"]?.Value ?? string.Empty;
            if (root["InitialDelay"] != null) parsed.InitialDelay = root["InitialDelay"].AsFloat;
            if (root["IsMultiTrack"] != null) parsed.IsMultiTrack = root["IsMultiTrack"].AsBool;
            if (root["IsIdealForUnplugged"] != null) parsed.IsIdealForUnplugged = root["IsIdealForUnplugged"].AsBool;
            if (root["ChartParserVersion"] != null) parsed.ChartParserVersion = root["ChartParserVersion"].AsInt;
            if (root["ChartCoverageRatio"] != null) parsed.ChartCoverageRatio = root["ChartCoverageRatio"].AsFloat;
#endif
            return parsed;
        }

        private static string BuildTrackDisplayLabel(string layout, bool ideal, int chartParserVersion, float chartCoverage)
        {
            string label = layout switch
            {
                "single-track" => "<color=#69B9F2>Single Track</color>",
                "two-track-instrumental-vocals" => "<color=#E8B65C>Half Track</color>",
                "full-multitrack" => "<color=#71D59B>Multi Track</color>",
                _ => string.Empty
            };

            if (ideal)
                label += (label.Length > 0 ? "  ·  " : string.Empty) + "<color=#A7E87A>Ideal</color>";
            if (chartParserVersion >= 6 && chartCoverage < 0.50f)
            {
                int coveragePercent = (int)Math.Round(Math.Min(1d, Math.Max(0d, chartCoverage)) * 100d);
                label += (label.Length > 0 ? "  ·  " : string.Empty)
                    + $"<color=#FFC36B>Incomplete {coveragePercent}%</color>";
            }
            return label;
        }

        public static string GetTrackDisplayLabel(SongMappingMetaData songData)
        {
            if (songData == null || string.IsNullOrEmpty(songData.SongID) ||
                !songData.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal))
                return string.Empty;

            string archiveName = Path.GetFileName(songData.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(archiveName)) return string.Empty;
            if (TrackDisplayLabelCache.TryGetValue(archiveName, out string cached)) return cached;

            string archivePath = ResolveUsmPath(archiveName);
            string label = string.IsNullOrEmpty(archivePath) ? string.Empty : ReadTrackDisplayLabel(archivePath);
            TrackDisplayLabelCache[archiveName] = label;
            return label;
        }

        public static Texture2D EnsureSongCover(SongMappingMetaData songData)
        {
            if (songData == null || string.IsNullOrEmpty(songData.SongID) ||
                !songData.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal))
                return songData?.Thumbnail;

            string archivePath = ResolveUsmPath(songData.FileName);
            if (string.IsNullOrEmpty(archivePath)) return songData.Thumbnail;
            var cover = LoadArchiveCover(archivePath);
            if (cover != null) songData.Thumbnail = cover;
            return cover ?? songData.Thumbnail;
        }

        public static void QueueSongCoverLoad(SongMenuItem item, SongMappingMetaData songData)
        {
            if (item == null || songData == null || string.IsNullOrEmpty(songData.SongID) ||
                !songData.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal))
                return;

            string key = item.GetInstanceID() + "|" + songData.SongID;
            if (!PendingSongCoverKeys.Add(key)) return;

            PendingSongCoverLoads.Enqueue(new SongCoverRequest { Item = item, Song = songData, Key = key });
            if (_songCoverLoaderRunning) return;

            _songCoverLoaderRunning = true;
            try
            {
                MelonCoroutines.Start(LoadQueuedSongCovers());
            }
            catch
            {
                _songCoverLoaderRunning = false;
                PendingSongCoverKeys.Remove(key);
            }
        }

        private static bool IsSameSong(SongMappingMetaData current, SongMappingMetaData expected)
        {
            // IL2CPP can return a new managed wrapper for the same native metadata.
            // Reference equality would discard every deferred cover on Quest.
            return current != null && expected != null && !string.IsNullOrEmpty(expected.SongID)
                && string.Equals(current.SongID, expected.SongID, StringComparison.Ordinal);
        }

        private static IEnumerator LoadQueuedSongCovers()
        {
            while (PendingSongCoverLoads.Count > 0)
            {
                var request = PendingSongCoverLoads.Dequeue();
                PendingSongCoverKeys.Remove(request.Key);
                try
                {
                    var currentSong = request.Item != null ? request.Item.SongData : null;
                    if (IsSameSong(currentSong, request.Song))
                    {
                        var cover = EnsureSongCover(currentSong);
                        if (cover != null)
                        {
                            if (request.Item.CoverRenderer != null && request.Item.CoverRenderer.sharedMaterial != null)
                                request.Item.CoverRenderer.sharedMaterial.mainTexture = cover;
                            if (request.Item.FramedCoverRenderer != null && request.Item.FramedCoverRenderer.sharedMaterial != null)
                                request.Item.FramedCoverRenderer.sharedMaterial.mainTexture = cover;

                            var menu = StartSpace.I?.songMenu;
                            if (menu != null && AlbumsManager.SelectedAlbum?.AlbumID == "CustomSongsAlbum"
                                && IsSameSong(menu.SelectedSong, currentSong))
                                VinylCoverHelper.UpdateTurntableVinyl(currentSong, force: true);

                            MelonLogger.Msg($"[CoverDiag] Applied visible cover for '{currentSong.FileName}' without requiring gameplay.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[CustomSongs] Could not apply a deferred cover: {ex.Message}");
                }

                // Decode at most one archive cover per frame instead of blocking
                // the first menu frame while every carousel card is populated.
                yield return null;
            }

            _songCoverLoaderRunning = false;
        }

        public static void Init()
        {
            MelonLogger.Msg($"[UnpluggedQuestMod] CustomSongLoader.Init called! Platform: {Application.platform}");
            MelonLogger.Msg($"[UnpluggedQuestMod] Target CustomSongsFolder: {CustomSongsFolder}");
#if QUEST_IL2CPP
            if (Application.platform == RuntimePlatform.Android)
            {
                try
                {
                    var environment = new AndroidJavaClass("android.os.Environment");
                    bool hasAllFilesAccess = environment.CallStatic<bool>("isExternalStorageManager");
                    environment.Dispose();
                    MelonLogger.Msg($"[CustomSongs] Android all-files access: {hasAllFilesAccess}");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[CustomSongs] Could not check Android all-files access: {ex.Message}");
                }
            }
#endif
            if (!Directory.Exists(CustomSongsFolder))
            {
                try { Directory.CreateDirectory(CustomSongsFolder); } catch { }
            }
            BeginStartupMetadataScan();
            if (Application.platform != RuntimePlatform.Android)
            {
                StartWatcher();
            }
        }

        private static void BeginStartupMetadataScan()
        {
            if (_startupMetadataScanTask != null) return;
            string folder = CustomSongsFolder;
            _startupMetadataScanTask = Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                string[] files = Directory.Exists(folder)
                    ? Directory.GetFiles(folder)
                        .Where(path => string.Equals(Path.GetExtension(path), ".usm", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();

                foreach (string path in files)
                    ReadArchiveSongMetadata(path);

                timer.Stop();
                MelonLogger.Msg($"[CustomSongs] Background metadata scan finished for {files.Length} .usm archive(s) in {timer.ElapsedMilliseconds} ms.");
            });
        }

        public static void QueueCustomAlbumRegistration(AlbumsManager manager)
        {
            if (manager == null) return;
            _pendingAlbumManager = manager;
            if (_startupMetadataScanTask == null) BeginStartupMetadataScan();
            ProcessDeferredAlbumRegistration();
        }

        public static void ProcessDeferredAlbumRegistration()
        {
            TryRestoreSavedCustomAlbum();

            var manager = _pendingAlbumManager;
            var scan = _startupMetadataScanTask;
            if (manager == null || scan == null || !scan.IsCompleted) return;

            _pendingAlbumManager = null;
            if (scan.IsFaulted)
                MelonLogger.Warning($"[CustomSongs] Background metadata scan failed; reading metadata during registration: {scan.Exception?.GetBaseException().Message}");

            RegisterCustomAlbum(manager);

            _pendingSavedAlbumRestore = string.Equals(
                GamePreferences.Load(UnpluggedInfo.LastSongPlayedAlbum, ""),
                "CustomSongsAlbum",
                StringComparison.Ordinal);
            TryRestoreSavedCustomAlbum();
        }

        private static void TryRestoreSavedCustomAlbum()
        {
            if (!_pendingSavedAlbumRestore || CustomAlbumInstance?.builtInSongsMetadata == null ||
                CustomAlbumInstance.builtInSongsMetadata.Count == 0)
                return;

            try
            {
                int targetIndex = GetLastPlayedCustomSongIndex();
                if (targetIndex < 0) targetIndex = 0;
                QueueDirectSelectionIndex(targetIndex);

                var manager = AlbumsManager.I;
                if (manager == null) return;
                // The property setter raises AlbumLoaded, which tells StartSpace to
                // replay ChangeAlbum even while its initial disc animation is running.
                // Set the initial album as vanilla Awake does, before the menu enters.
                if (AlbumsManager.SelectedAlbum?.AlbumID != "CustomSongsAlbum")
                {
                    manager.SetSelectedAlbum(CustomAlbumInstance);
                    MelonLogger.Msg("[StartupDiag] Prepared the saved custom album without queuing a second disc animation.");
                }
                manager.CurrentAlbumSongs = CustomAlbumInstance.SongsMetadata;

                var startSpace = StartSpace.I;
                if (startSpace == null || !startSpace.gameObject.activeInHierarchy || startSpace.songMenu == null)
                    return;

                startSpace.songMenu.InitialOffset = 0;
#if QUEST_IL2CPP
                var existingSongs = GameFieldAccess.Get<Il2CppSystem.Collections.Generic.List<SongMappingMetaData>>(startSpace.songMenu, "songList");
#else
                var existingSongs = GameFieldAccess.Get<List<SongMappingMetaData>>(startSpace.songMenu, "songList");
#endif
                bool needsLateMenuRefresh = existingSongs != null && existingSongs.Count > 0
                    && (existingSongs[0]?.SongID == null || !existingSongs[0].SongID.StartsWith("CUSTOM_", StringComparison.Ordinal));
                if (needsLateMenuRefresh)
                    startSpace.songMenu.ChangeAlbum(manager.CurrentAlbumSongs);

                Traverse.Create(startSpace).Method("SetChallengeSetup", CustomAlbumInstance).GetValue();
                Traverse.Create(startSpace).Method("SongMenu_SelectedSongChanged", CustomAlbumInstance.builtInSongsMetadata[targetIndex]).GetValue();
                _pendingSavedAlbumRestore = false;
                MelonLogger.Msg($"[StartupDiag] Restored saved custom song index={targetIndex}, lateMenuRefresh={needsLateMenuRefresh}; the initial disc animation remains responsible for entering the menu.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Could not restore the custom album after background scanning: {ex}");
            }
        }

        public static bool ContainsSong(AlbumData album, string folderName)
        {
            if (album == null || album.SongFiles == null) return false;
            foreach (var s in album.SongFiles)
            {
                if (s != null && (s.OriginalFileName == folderName || s.Path == folderName)) return true;
            }
            return false;
        }

        public static void StartWatcher()
        {
            if (watcher != null) return;
            try
            {
                if (!Directory.Exists(CustomSongsFolder)) Directory.CreateDirectory(CustomSongsFolder);
                watcher = new FileSystemWatcher(CustomSongsFolder);
                watcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite;
                watcher.IncludeSubdirectories = true;
                watcher.InternalBufferSize = 65536;
                watcher.Created += (s, e) => ModMain.ScheduleReload();
                watcher.Changed += (s, e) => ModMain.ScheduleReload();
                watcher.Deleted += (s, e) => ModMain.ScheduleReload();
                watcher.Renamed += (s, e) => ModMain.ScheduleReload();
                watcher.EnableRaisingEvents = true;
                MelonLogger.Msg("[CustomSongs] FileSystemWatcher active: real-time in-game custom song importing enabled!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error starting FileSystemWatcher: {ex}");
            }
        }

        public static void RegisterCustomAlbum(AlbumsManager manager)
        {
            try
            {
                MelonLogger.Msg($"[UnpluggedQuestMod] RegisterCustomAlbum called. Manager null: {manager == null}");
                if (manager == null) return;

                if (CustomAlbumInstance == null)
                {
                    if (manager.DlcAlbums != null && manager.DlcAlbums.Count > 0)
                    {
                        CustomAlbumInstance = UnityEngine.Object.Instantiate(manager.DlcAlbums[0]);
                    }
                    else if (manager.CampaignAlbums != null && manager.CampaignAlbums.Count > 0)
                    {
                        CustomAlbumInstance = ScriptableObject.CreateInstance<DLCAlbumData>();
                        CustomAlbumInstance.Cover = manager.CampaignAlbums[0].Cover;
                        CustomAlbumInstance.VinylTexture = manager.CampaignAlbums[0].VinylTexture;
                    }
                    else
                    {
                        CustomAlbumInstance = ScriptableObject.CreateInstance<DLCAlbumData>();
                    }

                    CustomAlbumInstance.AlbumID = "CustomSongsAlbum";
                    CustomAlbumInstance.Name = "CUSTOM SONGS";
                    CustomAlbumInstance.VenueID = "Venue1";
                    CustomAlbumInstance.IsDLC = false;
                    CustomAlbumInstance.IsFree = true;
                    CustomAlbumInstance.IsPurchased = true;
                    CustomAlbumInstance.CampaignAlbumTargetForFreeUnlocking = null;
                }

                PopulateCustomSongs(CustomAlbumInstance);
                MelonLogger.Msg($"[UnpluggedQuestMod] Custom songs populated: {CustomAlbumInstance.SongFiles?.Count ?? 0} song(s)");

                if (manager.DlcAlbums != null && !manager.DlcAlbums.Contains(CustomAlbumInstance))
                {
                    manager.DlcAlbums.Add(CustomAlbumInstance);
                    MelonLogger.Msg($"[UnpluggedQuestMod] Added Custom Songs Album to DLC shelf with {CustomAlbumInstance.SongFiles?.Count ?? 0} song(s)!");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error registering custom album: {ex}");
            }
        }

        public static void PopulateCustomSongs(AlbumData album)
        {
            if (album == null) return;
            var scanTimer = Stopwatch.StartNew();
            string[] usmFiles;
            try
            {
                bool folderExists = Directory.Exists(CustomSongsFolder);
                MelonLogger.Msg($"[CustomSongs] Songs folder exists/readable: {folderExists} ({CustomSongsFolder})");
                if (!folderExists) return;

                usmFiles = Directory.GetFiles(CustomSongsFolder)
                    .Where(path => string.Equals(Path.GetExtension(path), ".usm", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                MelonLogger.Msg($"[CustomSongs] Enumerated {usmFiles.Length} .usm archive(s).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Could not enumerate songs in '{CustomSongsFolder}': {ex}");
                return;
            }

            string fingerprint = string.Join("\n", usmFiles.Select(GetArchiveFileVersion));
            if (ReferenceEquals(album, _lastPopulatedAlbum) &&
                string.Equals(fingerprint, _lastPopulatedSongsFingerprint, StringComparison.Ordinal) &&
                album.SongFiles != null && album.builtInSongsMetadata != null &&
                album.SongFiles.Count == usmFiles.Length && album.builtInSongsMetadata.Count == usmFiles.Length)
            {
                scanTimer.Stop();
                MelonLogger.Msg($"[CustomSongs] Reused unchanged album metadata for {usmFiles.Length} .usm archive(s) ({scanTimer.ElapsedMilliseconds} ms); skipped the repeated ZIP/image scan.");
                if (AlbumsManager.I != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                    AlbumsManager.I.CurrentAlbumSongs = album.SongsMetadata;
                UpdateAlbumCover(album as DLCAlbumData ?? CustomAlbumInstance);
                return;
            }

            TrackDisplayLabelCache.Clear();
#if QUEST_IL2CPP
            album.SongFiles = new Il2CppSystem.Collections.Generic.List<SongAsset>();
            album.builtInSongsMetadata = new Il2CppSystem.Collections.Generic.List<SongMappingMetaData>();
#else
            album.SongFiles = new List<SongAsset>();
            album.builtInSongsMetadata = new List<SongMappingMetaData>();
#endif

            foreach (var usmPath in usmFiles)
            {
                try
                {
                    string folderName = Path.GetFileName(usmPath);
                    var archiveMetadata = ReadArchiveSongMetadata(usmPath);
                    string songName = archiveMetadata.SongName;
                    string artistName = archiveMetadata.BandName;
                    float initialDelay = archiveMetadata.InitialDelay;
                    TrackDisplayLabelCache[folderName] = archiveMetadata.TrackDisplayLabel ?? string.Empty;

                    var asset = new SongAsset
                    {
                        ID = "CUSTOM_" + folderName,
                        OriginalFileName = folderName,
                        Path = folderName,
                        AlbumData = album,
                        Active = true,
                        IsPurchased = true,
                        LockedAtStart = false
                    };
                    album.SongFiles.Add(asset);

                    bool isMulti = archiveMetadata.IsMultiTrack;

                    var meta = new SongMappingMetaData
                    {
                        SongID = asset.ID,
                        SongName = songName,
                        BandName = artistName,
                        FileName = folderName,
                        LevelAuthorName = "Custom Charter",
                        InitialDelay = initialDelay,
                        IsBuiltIn = false,
                        IsMultiTrack = isMulti,
                        SongAsset = asset,
                        // Decode covers only for the selected album and visible
                        // carousel cards; the rest remain lazy until scrolled to.
                        Thumbnail = album.Cover
                    };
                    album.builtInSongsMetadata.Add(meta);

                    MelonLogger.Msg($"[CustomSongs] Found song: {artistName} - {songName} (cover deferred until visible, MultiTrack={isMulti})");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[CustomSongs] Error reading song archive {usmPath}: {ex}");
                }
            }

            scanTimer.Stop();
            MelonLogger.Msg($"[CustomSongs] Album metadata ready: {album.builtInSongsMetadata?.Count ?? 0} song(s), {scanTimer.ElapsedMilliseconds} ms. Titles, track layout and compatibility were read from each .usm meta.json.");
            _lastPopulatedAlbum = album;
            _lastPopulatedSongsFingerprint = fingerprint;

            // Synchronize private songsMetadata field so AlbumData.SongsMetadata getter returns fresh list!
            try
            {
#if QUEST_IL2CPP
                var freshList = new Il2CppSystem.Collections.Generic.List<SongMappingMetaData>();
                foreach (var metadata in album.builtInSongsMetadata) freshList.Add(metadata);
#else
                var freshList = new List<SongMappingMetaData>(album.builtInSongsMetadata);
#endif
                GameFieldAccess.Set(album, "songsMetadata", freshList);

                if (CustomAlbumInstance != null && CustomAlbumInstance != album)
                {
                    CustomAlbumInstance.SongFiles = album.SongFiles;
                    CustomAlbumInstance.builtInSongsMetadata = album.builtInSongsMetadata;
#if QUEST_IL2CPP
                    var copy = new Il2CppSystem.Collections.Generic.List<SongMappingMetaData>();
                    foreach (var metadata in freshList) copy.Add(metadata);
#else
                    var copy = new List<SongMappingMetaData>(freshList);
#endif
                    GameFieldAccess.Set(CustomAlbumInstance, "songsMetadata", copy);
                }

                if (AlbumsManager.I != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                {
                    AlbumsManager.I.CurrentAlbumSongs = freshList;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error syncing songsMetadata: {ex}");
            }

            UpdateAlbumCover(album as DLCAlbumData ?? CustomAlbumInstance);
        }

        public static void UpdateAlbumCover(DLCAlbumData album)
        {
            try
            {
                if (album == null) return;
                Texture2D chosenCover = null;

                int lastSongIndex = GetLastPlayedCustomSongIndex();
                if (lastSongIndex < 0) lastSongIndex = 0;
                if (album.builtInSongsMetadata != null && album.builtInSongsMetadata.Count > 0)
                {
                    if (lastSongIndex < album.builtInSongsMetadata.Count)
                    {
                        var lastSong = album.builtInSongsMetadata[lastSongIndex];
                        if (lastSong != null) chosenCover = EnsureSongCover(lastSong);
                    }
                }

                if (chosenCover == null)
                {
                    string presetPath = Path.Combine(CustomSongsFolder, "preset_cover.jpg");
                    if (File.Exists(presetPath))
                    {
                        chosenCover = new Texture2D(2, 2);
                        if (ImageConversion.LoadImage(chosenCover, File.ReadAllBytes(presetPath)))
                        {
                            chosenCover.name = "preset_cover";
                        }
                        else
                        {
                            chosenCover = null;
                        }
                    }
                }

                if (chosenCover != null)
                {
                    album.Cover = chosenCover;
                    album.VinylTexture = chosenCover;
                    if (CustomAlbumInstance != null && CustomAlbumInstance != album)
                    {
                        CustomAlbumInstance.Cover = chosenCover;
                        CustomAlbumInstance.VinylTexture = chosenCover;
                    }
                    MelonLogger.Msg($"[CustomSongs] Vinyl cover updated to: {chosenCover.name} ({chosenCover.width}x{chosenCover.height})");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in UpdateAlbumCover: {ex}");
            }
        }

        public static SongMapping LoadSongMapping(SongMappingMetaData song, bool isHandTracking)
        {
            ChartOutcomeLogCount = 0;
            string usmPath = ResolveUsmPath(song?.FileName);
            if (string.IsNullOrEmpty(usmPath))
                throw new FileNotFoundException($"Custom song archive was not found for '{song?.FileName}' in '{CustomSongsFolder}'.");

            string diff = GameStateManager.I != null
                ? SongMapping.GetSongDataPathByDificulty(GameStateManager.I.CurrentDifficulty)
                : "songdata-Normal.xml";
            string jsonContent = null;
            string loadedJson = null;
            string xmlContent = "";
            string loadedDiff = diff;

            using (var archive = System.IO.Compression.ZipFile.OpenRead(usmPath))
            {
#if QUEST_IL2CPP
                if (isHandTracking)
                {
                    string nativeHandName = Path.GetFileNameWithoutExtension(diff) + ".hand.xml";
                    var nativeHandEntry = FindArchiveEntry(archive, GetChartEntryCandidates(nativeHandName));
                    if (nativeHandEntry == null)
                        throw new InvalidDataException($"'{Path.GetFileName(usmPath)}' has no Quest hand mapping for {diff}. Sync this song to Quest again from the updated USM.");
                    loadedDiff = nativeHandEntry.Name;
                    using (var sr = new StreamReader(nativeHandEntry.Open()))
                        xmlContent = sr.ReadToEnd();
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[ChartDiag] Selected prebuilt Quest hand mapping '{nativeHandEntry.FullName}'.");
                }
                else
#endif
                {
                string jsonDiff = Path.ChangeExtension(Path.GetFileName(diff), ".json");
                var jsonEntry = FindArchiveEntry(archive, GetChartEntryCandidates(jsonDiff));
                if (jsonEntry != null)
                {
                    loadedJson = jsonEntry.Name;
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[ChartDiag] Requested difficulty {jsonDiff}; selected .usm entry '{jsonEntry.FullName}'.");
                    using (var sr = new StreamReader(jsonEntry.Open()))
                    {
                        jsonContent = sr.ReadToEnd();
                    }
                }
                else
                {
                    var xmlEntry = FindArchiveEntry(archive, GetChartEntryCandidates(diff));
                    if (xmlEntry != null)
                    {
                        loadedDiff = xmlEntry.Name;
                        if (ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg($"[ChartDiag] Requested difficulty {diff}; selected legacy .usm entry '{xmlEntry.FullName}'.");
                        using (var sr = new StreamReader(xmlEntry.Open()))
                        {
                            xmlContent = sr.ReadToEnd();
                        }
                    }
                }
                }
            }

            if (loadedJson != null)
                return LoadSongMappingJson(jsonContent, loadedJson, usmPath, isHandTracking);

            if (string.IsNullOrWhiteSpace(xmlContent))
                throw new InvalidDataException($"No chart JSON or legacy XML was found in '{Path.GetFileName(usmPath)}' for difficulty '{diff}'.");

            MelonLogger.Msg($"[CustomSongs] Loading chart XML from .usm: {Path.GetFileName(usmPath)}!{loadedDiff} ({xmlContent.Length} characters)");
#if QUEST_IL2CPP
            var serializer = new Il2CppSystem.Runtime.Serialization.DataContractSerializer(Il2CppInterop.Runtime.Il2CppType.Of<SongMapping>());
            var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xmlContent));
            var stream = new Il2CppSystem.IO.MemoryStream(ms.ToArray());
            var settings = new Il2CppSystem.Xml.XmlReaderSettings();
            settings.ConformanceLevel = Il2CppSystem.Xml.ConformanceLevel.Fragment;
            var reader = Il2CppSystem.Xml.XmlReader.Create(stream.Cast<Il2CppSystem.IO.Stream>(), settings);
            reader.Read();
            var obj = serializer.ReadObject(reader, false);
            var mapping = obj.Cast<SongMapping>();
#else
            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xmlContent));
            var serializer = new DataContractSerializer(typeof(SongMapping));
            using var reader = XmlReader.Create(ms, new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            reader.Read();
            var mapping = (SongMapping)serializer.ReadObject(reader, false);
#endif
            if (mapping == null)
                throw new InvalidDataException($"Chart XML '{loadedDiff}' in '{Path.GetFileName(usmPath)}' did not deserialize to a song mapping.");

            mapping.IsHandTrackingMapping = isHandTracking;
            MelonLogger.Msg($"[CustomSongs] Chart mapping loaded: {mapping.Keys?.Count ?? 0} track/key group(s), handTracking={isHandTracking}");

#if QUEST_IL2CPP
            // The prebuilt hand XML already contains every finger and the exact
            // requested difficulty. Do not enter the legacy repair/conversion
            // loops: reading List<TierFinger> through the interop bridge crashes.
            if (isHandTracking)
                return mapping;
#endif

            // DYNAMIC FINGERING POPULATION FOR ALL NOTES (Fixes missing scoring without needing Harmony patches)
            if (mapping.Keys != null)
            {
                for (int ki = 0; ki < mapping.Keys.Count; ki++)
                {
                    var k = mapping.Keys[ki];
                    if (k is GuitarGame gg && gg.Notes != null)
                    {
                        for (int ni = 0; ni < gg.Notes.Count; ni++)
                        {
                            if (gg.Notes[ni] is HandGuitarNote hn)
                            {
                                if (hn.Fingers == null || hn.Fingers.Count == 0)
                                {
#if QUEST_IL2CPP
                                    var fList = new Il2CppSystem.Collections.Generic.List<TierFinger>();
                                    fList.Add(hn.Tier switch {
                                        0 => TierFinger.Index,
                                        1 => TierFinger.Middle,
                                        2 => TierFinger.Ring,
                                        _ => TierFinger.Pinky
                                    });
                                    hn.Fingers = fList;
#else
                                    hn.Fingers = hn.Tier switch {
                                        0 => new List<TierFinger> { TierFinger.Index },
                                        1 => new List<TierFinger> { TierFinger.Middle },
                                        2 => new List<TierFinger> { TierFinger.Ring },
                                        _ => new List<TierFinger> { TierFinger.Pinky }
                                    };
#endif
                                }
                            }
                        }
                    }
                    else if (k is GuitarControllerGame cg && cg.Notes != null)
                    {
                        for (int ni = 0; ni < cg.Notes.Count; ni++)
                        {
                            if (cg.Notes[ni] is ControllerGuitarNote cn)
                            {
                                if (cn.Buttons == null || cn.Buttons.Count < 2 || (!cn.Buttons[0] && !cn.Buttons[1]))
                                {
#if QUEST_IL2CPP
                                    var bList = new Il2CppSystem.Collections.Generic.List<bool>();
                                    bList.Add(cn.Tier < 2);
                                    bList.Add(cn.Tier >= 2);
                                    cn.Buttons = bList;
#else
                                    cn.Buttons = new List<bool> { cn.Tier < 2, cn.Tier >= 2 };
#endif
                                }
                            }
                        }
                    }
                }
            }

            // DUAL-TRACK GUARANTEE: Ensure GuitarControllerGame exists so controllers work!
            if (mapping.Keys != null)
            {
                bool hasHand = false;
                bool hasController = false;
                GuitarGame handGame = null;
                for (int ki = 0; ki < mapping.Keys.Count; ki++)
                {
                    var k = mapping.Keys[ki];
                    if (k is GuitarGame gg)
                    {
                        hasHand = true;
                        if (handGame == null) handGame = gg;
                    }
                    if (k is GuitarControllerGame) hasController = true;
                }

                if (hasHand && !hasController && handGame != null)
                {
                    var ctrlGame = new GuitarControllerGame
                    {
                        Duration = handGame.Duration,
                        Time = handGame.Time,
                        PrewarmTime = handGame.PrewarmTime,
#if QUEST_IL2CPP
                        Notes = new Il2CppSystem.Collections.Generic.List<ControllerGuitarNote>()
#else
                        Notes = new List<ControllerGuitarNote>()
#endif
                    };
                    var notes = handGame.Notes;
                    if (notes != null)
                    {
                        for (int ni = 0; ni < notes.Count; ni++)
                        {
                            var k = notes[ni];
                            if (k is GuitarNote n)
                            {
                                bool hasTrigger = n.Tier < 2;
                                bool hasGrip = n.Tier >= 2;
                                if (n is HandGuitarNote hn && hn.Fingers != null && hn.Fingers.Count > 0)
                                {
                                    bool hasLow = hn.Fingers.Contains(TierFinger.Index) || hn.Fingers.Contains(TierFinger.Middle);
                                    bool hasHigh = hn.Fingers.Contains(TierFinger.Ring) || hn.Fingers.Contains(TierFinger.Pinky);
                                    if (hasLow && hasHigh) { hasTrigger = true; hasGrip = true; }
                                    else if (hasHigh) { hasTrigger = false; hasGrip = true; }
                                    else { hasTrigger = true; hasGrip = false; }
                                }

                                var cNote = new ControllerGuitarNote
                                {
                                    Time = n.Time,
                                    Duration = n.Duration,
                                    CursableProbability = n.CursableProbability,
                                    IsBlind = n.IsBlind,
#if QUEST_IL2CPP
                                    Buttons = new Il2CppSystem.Collections.Generic.List<bool>(),
#else
                                    Buttons = new List<bool> { hasTrigger, hasGrip },
#endif
                                    ChordRotation = 0,
                                    Intensity = 0
                                };
                                SetGameplayStringLane(cNote, n.Tier);
#if QUEST_IL2CPP
                                cNote.Buttons.Add(hasTrigger);
                                cNote.Buttons.Add(hasGrip);
#endif
                                ctrlGame.Notes.Add(cNote);
                            }
                        }
                    }
                    mapping.Keys.Add(ctrlGame);
                    MelonLogger.Msg($"[CustomSongs] Converted {ctrlGame.Notes.Count} notes to GuitarControllerGame!");
                }
            }

            return mapping;
        }

        private static string[] GetChartEntryCandidates(string requestedEntry)
        {
            string fileName = Path.GetFileName(requestedEntry ?? string.Empty);
            string extension = Path.GetExtension(fileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string[] preferredStems;

            switch (stem.ToLowerInvariant())
            {
                case "songdata-supereasy":
                    preferredStems = new[] { "songdata-SuperEasy", "songdata-Easy", "songdata-Normal", "songdata-Hard" };
                    break;
                case "songdata-easy":
                    preferredStems = new[] { "songdata-Easy", "songdata-SuperEasy", "songdata-Normal", "songdata-Hard" };
                    break;
                case "songdata-normal":
                    preferredStems = new[] { "songdata-Normal", "songdata-Easy", "songdata-Hard", "songdata-SuperEasy" };
                    break;
                case "songdata-hard":
                    preferredStems = new[] { "songdata-Hard", "songdata-Normal", "songdata-Easy", "songdata-SuperEasy" };
                    break;
                default:
                    return new[] { fileName, "songdata-Normal" + extension, "songdata-Hard" + extension,
                        "songdata-Easy" + extension, "songdata-SuperEasy" + extension };
            }

            return preferredStems.Select(candidate => candidate + extension).ToArray();
        }

        private static JSONArray RequireJsonArray(JSONNode root, string propertyName, string archiveName)
        {
            JSONNode value = root[propertyName];
            if (value == null || !value.IsArray)
                throw new InvalidDataException($"Chart JSON '{archiveName}' is missing the '{propertyName}' array.");
            return value.AsArray;
        }

        private static TierFinger ParseTierFinger(string fingerName, string archiveName)
        {
            switch (fingerName)
            {
                case "Index": return TierFinger.Index;
                case "Middle": return TierFinger.Middle;
                case "Ring": return TierFinger.Ring;
                case "Pinky": return TierFinger.Pinky;
                default: throw new InvalidDataException($"Chart JSON '{archiveName}' has an unknown finger '{fingerName}'.");
            }
        }

        private static SongMapping LoadSongMappingJson(string jsonContent, string chartName, string usmPath, bool isHandTracking)
        {
#if QUEST_IL2CPP
            return LoadSongMappingJsonQuest(jsonContent, chartName, usmPath, isHandTracking);
#else
            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new InvalidDataException($"Chart JSON '{chartName}' in '{Path.GetFileName(usmPath)}' is empty.");

            var loadTimer = Stopwatch.StartNew();
            JSONNode root;
            try
            {
                root = JSONNode.Parse(jsonContent);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Chart JSON '{chartName}' in '{Path.GetFileName(usmPath)}' is invalid: {ex.Message}", ex);
            }

            long jsonParseMs = loadTimer.ElapsedMilliseconds;
            int schemaVersion = root != null ? root["SchemaVersion"].AsInt : 0;
            if (root == null || !root.IsObject || (schemaVersion != 2 && schemaVersion != 3))
                throw new InvalidDataException($"Chart JSON '{chartName}' uses unsupported schema version {schemaVersion}; expected version 2 or 3.");

            JSONArray handNotes = RequireJsonArray(root, "HandNotes", chartName);
            JSONArray controllerNotes = RequireJsonArray(root, "ControllerNotes", chartName);
            JSONArray bpmChanges = RequireJsonArray(root, "BPMGrid", chartName);
            bool useHandTrack = isHandTracking;
            JSONArray selectedNotes = useHandTrack ? handNotes : controllerNotes;
            float chartDuration = root["Duration"].AsFloat;
            int initialBpm = root["BPM"].AsInt;

            var mapping = new SongMapping();
#if QUEST_IL2CPP
            mapping.BPMGrid = new Il2CppSystem.Collections.Generic.List<BPMKey>();
            mapping.Keys = new Il2CppSystem.Collections.Generic.List<KeyTime>();
#else
            mapping.BPMGrid = new List<BPMKey>();
            mapping.Keys = new List<KeyTime>();
#endif
            mapping.BPM = initialBpm > 0 ? initialBpm : 120;
            mapping.IsHandTrackingMapping = useHandTrack;

            for (int i = 0; i < bpmChanges.Count; i++)
            {
                JSONNode bpmNode = bpmChanges[i];
                var bpmKey = new BPMKey
                {
                    Time = bpmNode["Time"].AsFloat,
                    Duration = 0f,
                    BPM = bpmNode["BPM"].AsFloat
                };
                mapping.BPMGrid.Add(bpmKey);
            }

            if (mapping.BPMGrid.Count == 0)
            {
                mapping.BPMGrid.Add(new BPMKey { Time = 0f, Duration = 0f, BPM = mapping.BPM });
            }

            int sampleLimit = ModMain.ConfigChartDiagnostics ? Math.Min(3, selectedNotes.Count) : 0;
            string trackLabel = useHandTrack ? "hand" : "controller";
            var tierCounts = new SortedDictionary<int, int>();
            int loadedSelectedNotes = 0;
            int ignoredEmptyControllerNotes = 0;
            if (useHandTrack)
            {
#if QUEST_IL2CPP
                var handGame = new GuitarGame { Time = 0f, Duration = chartDuration, PrewarmTime = 4f, Notes = new Il2CppSystem.Collections.Generic.List<GuitarNote>() };
#else
                var handGame = new GuitarGame { Time = 0f, Duration = chartDuration, PrewarmTime = 4f, Notes = new List<GuitarNote>() };
#endif
                for (int i = 0; i < handNotes.Count; i++)
                {
                    JSONNode noteNode = handNotes[i];
                    if (i == 0 && ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg("[ChartDiag] Building first hand note (PC).");
                    var handNote = noteNode["IsPullOff"].AsBool
                        ? (HandGuitarNote)new PullOffHandGuitarNote()
                        : new HandGuitarNote();
                    handNote.Time = noteNode["Time"].AsFloat;
                    handNote.Duration = noteNode["Duration"].AsFloat;
                    SetGameplayStringLane(handNote, ReadStringLane(noteNode, chartName));
                    tierCounts[handNote.Tier] = tierCounts.TryGetValue(handNote.Tier, out int handTierCount) ? handTierCount + 1 : 1;
                    handNote.CursableProbability = 0f;
                    handNote.IsBlind = false;
                    JSONArray fingers = RequireJsonArray(noteNode, "Fingers", chartName);
                    // The game constructor initializes Fingers. Replacing its list is
                    // unnecessary and can cross an unsafe IL2CPP generic marshaling path.
                    var noteFingers = handNote.Fingers;
                    if (noteFingers == null)
                        throw new InvalidDataException($"Hand note {i} in '{chartName}' has no Fingers list.");
                    for (int fingerIndex = 0; fingerIndex < fingers.Count; fingerIndex++)
                    {
                        noteFingers.Add(ParseTierFinger(fingers[fingerIndex].Value, chartName));
                    }
                    if (noteFingers.Count == 0)
                        throw new InvalidDataException($"Chart JSON '{chartName}' has a hand note without fingers (note {i}).");
                    handGame.Notes.Add(handNote);

                    if (i < sampleLimit)
                    {
                        var fingerNames = new List<string>();
                        for (int fingerIndex = 0; fingerIndex < handNote.Fingers.Count; fingerIndex++)
                            fingerNames.Add(handNote.Fingers[fingerIndex].ToString());
                        MelonLogger.Msg($"[ChartDiag] hand sample {i}: t={handNote.Time:F3}s d={handNote.Duration:F3}s stringLane={handNote.Tier} fingers={string.Join("+", fingerNames)} pullOff={noteNode["IsPullOff"].AsBool}");
                    }
                }
                loadedSelectedNotes = handGame.Notes.Count;
                mapping.Keys.Add(handGame);
            }
            else
            {
#if QUEST_IL2CPP
                var controllerGame = new GuitarControllerGame { Time = 0f, Duration = chartDuration, PrewarmTime = 4f, Notes = new Il2CppSystem.Collections.Generic.List<ControllerGuitarNote>() };
#else
                var controllerGame = new GuitarControllerGame { Time = 0f, Duration = chartDuration, PrewarmTime = 4f, Notes = new List<ControllerGuitarNote>() };
#endif
                for (int i = 0; i < controllerNotes.Count; i++)
                {
                    JSONNode noteNode = controllerNotes[i];
                    JSONArray buttons = RequireJsonArray(noteNode, "Buttons", chartName);
                    if (buttons.Count != 2)
                        throw new InvalidDataException($"Chart JSON '{chartName}' controller note {i} must contain exactly two button values.");

                    bool hasTrigger = buttons[0].AsBool;
                    bool hasGrip = buttons[1].AsBool;
                    if (!hasTrigger && !hasGrip)
                    {
                        // Vanilla controller charts always require at least one fret
                        // button. Empty entries are parser artifacts (typically CH
                        // HOPO/tap markers), so ignore them rather than making an
                        // unplayable note in old .usm files.
                        ignoredEmptyControllerNotes++;
                        if (ModMain.ConfigChartDiagnostics && ignoredEmptyControllerNotes <= 5)
                            MelonLogger.Warning($"[ChartDiag] ignored empty controller note {i}: t={noteNode["Time"].AsFloat:F3}s stringLane={ReadStringLane(noteNode, chartName)}; both fret buttons were false.");
                        continue;
                    }

                    int stringLane = ReadStringLane(noteNode, chartName);
                    var controllerNote = new ControllerGuitarNote
                    {
                        Time = noteNode["Time"].AsFloat,
                        Duration = noteNode["Duration"].AsFloat,
                        CursableProbability = 0f,
                        IsBlind = false,
                        ChordRotation = noteNode["ChordRotation"].AsInt,
                        Intensity = noteNode["Intensity"].AsInt
                    };
                    SetGameplayStringLane(controllerNote, stringLane);
#if QUEST_IL2CPP
                    controllerNote.Buttons = new Il2CppSystem.Collections.Generic.List<bool>();
#else
                    controllerNote.Buttons = new List<bool>();
#endif
                    controllerNote.Buttons.Add(hasTrigger);
                    controllerNote.Buttons.Add(hasGrip);
                    tierCounts[controllerNote.Tier] = tierCounts.TryGetValue(controllerNote.Tier, out int controllerTierCount) ? controllerTierCount + 1 : 1;
                    controllerGame.Notes.Add(controllerNote);

                    if (i < sampleLimit)
                    {
                        MelonLogger.Msg($"[ChartDiag] controller sample {i}: t={controllerNote.Time:F3}s d={controllerNote.Duration:F3}s stringLane={controllerNote.Tier} trigger={controllerNote.Buttons[0]} grip={controllerNote.Buttons[1]} rotation={controllerNote.ChordRotation}");
                    }
                }
                loadedSelectedNotes = controllerGame.Notes.Count;
                mapping.Keys.Add(controllerGame);
            }

            string tierSummary = string.Join(",", tierCounts.Select(pair => $"{pair.Key}:{pair.Value}"));
            MelonLogger.Msg($"[ChartDiag] Loaded {Path.GetFileName(usmPath)}!{chartName}: schema={schemaVersion}, mode={trackLabel}, selectedNotes={selectedNotes.Count}, loadedNotes={loadedSelectedNotes}, handNotes={handNotes.Count}, controllerNotes={controllerNotes.Count}, ignoredEmptyControllerNotes={ignoredEmptyControllerNotes}, stringLanes=[{tierSummary}], bpmChanges={mapping.BPMGrid.Count}, duration={chartDuration:F2}s, jsonParseMs={jsonParseMs}, gameObjectsMs={loadTimer.ElapsedMilliseconds - jsonParseMs}, totalMs={loadTimer.ElapsedMilliseconds}");
            if (loadedSelectedNotes == 0)
                MelonLogger.Warning($"[ChartDiag] The selected {trackLabel} track in {chartName} has no notes.");

            return mapping;
#endif
        }

#if QUEST_IL2CPP
        private static SongMapping LoadSongMappingJsonQuest(string jsonContent, string chartName, string usmPath, bool isHandTracking)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new InvalidDataException($"Chart JSON '{chartName}' in '{Path.GetFileName(usmPath)}' is empty.");

            var loadTimer = Stopwatch.StartNew();
            System.Text.Json.JsonDocument document;
            try
            {
                document = System.Text.Json.JsonDocument.Parse(jsonContent, new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Chart JSON '{chartName}' in '{Path.GetFileName(usmPath)}' is invalid: {ex.Message}", ex);
            }

            using (document)
            {
                long jsonParseMs = loadTimer.ElapsedMilliseconds;
                var root = document.RootElement;
                int schemaVersion = ReadJsonInt(root, "SchemaVersion");
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || (schemaVersion != 2 && schemaVersion != 3))
                    throw new InvalidDataException($"Chart JSON '{chartName}' uses unsupported schema version {schemaVersion}; expected version 2 or 3.");

                var handNotes = RequireJsonArray(root, "HandNotes", chartName);
                var controllerNotes = RequireJsonArray(root, "ControllerNotes", chartName);
                var bpmChanges = RequireJsonArray(root, "BPMGrid", chartName);
                var selectedNotes = isHandTracking ? handNotes : controllerNotes;
                float chartDuration = ReadJsonFloat(root, "Duration");
                int initialBpm = ReadJsonInt(root, "BPM");

                var mapping = new SongMapping
                {
                    BPM = initialBpm > 0 ? initialBpm : 120,
                    IsHandTrackingMapping = isHandTracking,
                    BPMGrid = new Il2CppSystem.Collections.Generic.List<BPMKey>(),
                    Keys = new Il2CppSystem.Collections.Generic.List<KeyTime>()
                };

                foreach (var bpmNode in bpmChanges.EnumerateArray())
                {
                    mapping.BPMGrid.Add(new BPMKey
                    {
                        Time = ReadJsonFloat(bpmNode, "Time"),
                        Duration = 0f,
                        BPM = ReadJsonFloat(bpmNode, "BPM")
                    });
                }
                if (mapping.BPMGrid.Count == 0)
                    mapping.BPMGrid.Add(new BPMKey { Time = 0f, Duration = 0f, BPM = mapping.BPM });

                int sampleLimit = ModMain.ConfigChartDiagnostics ? Math.Min(3, selectedNotes.GetArrayLength()) : 0;
                string trackLabel = isHandTracking ? "hand" : "controller";
                var tierCounts = new SortedDictionary<int, int>();
                int loadedSelectedNotes = 0;
                int ignoredEmptyControllerNotes = 0;

                if (isHandTracking)
                {
                    var handGame = new GuitarGame
                    {
                        Time = 0f,
                        Duration = chartDuration,
                        PrewarmTime = 4f,
                        Notes = new Il2CppSystem.Collections.Generic.List<GuitarNote>()
                    };

                    int index = 0;
                    foreach (var noteNode in handNotes.EnumerateArray())
                    {
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] Building first hand note (Quest).");
                        HandGuitarNote handNote = ReadJsonBool(noteNode, "IsPullOff")
                            ? (HandGuitarNote)new PullOffHandGuitarNote()
                            : new HandGuitarNote();
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] First hand note constructed.");
                        handNote.Time = ReadJsonFloat(noteNode, "Time");
                        handNote.Duration = ReadJsonFloat(noteNode, "Duration");
                        int stringLane = ReadJsonStringLane(noteNode, chartName);
                        SetGameplayStringLane(handNote, stringLane);
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] First hand note lane set.");
                        tierCounts[stringLane] = tierCounts.TryGetValue(stringLane, out int handTierCount) ? handTierCount + 1 : 1;
                        // Both fields are already zero/false on a new game note.
                        // Avoid the inherited setter bridge on IL2CPP hand notes.

                        var fingers = RequireJsonArray(noteNode, "Fingers", chartName);
                        var fingerValues = new TierFinger[fingers.GetArrayLength()];
                        int fingerIndex = 0;
                        foreach (var finger in fingers.EnumerateArray())
                        {
                            string fingerName = finger.ValueKind == System.Text.Json.JsonValueKind.String ? finger.GetString() : string.Empty;
                            fingerValues[fingerIndex++] = ParseTierFinger(fingerName, chartName);
                        }
                        if (fingerValues.Length == 0)
                            throw new InvalidDataException($"Chart JSON '{chartName}' has a hand note without fingers (note {index}).");
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] First hand note fingers parsed.");

                        // The game's constructor owns this list. Populate its native
                        // array directly to avoid the generic enum Add bridge that
                        // appears to abort Unity on Quest.
                        var noteFingers = handNote._Fingers_k__BackingField;
                        if (noteFingers == null)
                            throw new InvalidDataException($"Hand note {index} in '{chartName}' has no Fingers list.");
                        noteFingers._items = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<TierFinger>(fingerValues);
                        noteFingers._size = fingerValues.Length;
                        noteFingers._version++;
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] First hand note fingers set.");
                        handGame.Notes.Add(handNote);
                        if (index == 0 && ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg("[ChartDiag] First hand note added to game.");

                        if (index < sampleLimit)
                        {
                            MelonLogger.Msg($"[ChartDiag] hand sample {index}: t={ReadJsonFloat(noteNode, "Time"):F3}s d={ReadJsonFloat(noteNode, "Duration"):F3}s stringLane={stringLane} fingers={string.Join("+", fingerValues.Select(finger => finger.ToString()))} pullOff={ReadJsonBool(noteNode, "IsPullOff")}");
                        }
                        index++;
                    }
                    loadedSelectedNotes = handGame.Notes.Count;
                    mapping.Keys.Add(handGame);
                }
                else
                {
                    var controllerGame = new GuitarControllerGame
                    {
                        Time = 0f,
                        Duration = chartDuration,
                        PrewarmTime = 4f,
                        Notes = new Il2CppSystem.Collections.Generic.List<ControllerGuitarNote>()
                    };

                    int index = 0;
                    foreach (var noteNode in controllerNotes.EnumerateArray())
                    {
                        var buttons = RequireJsonArray(noteNode, "Buttons", chartName);
                        if (buttons.GetArrayLength() != 2)
                            throw new InvalidDataException($"Chart JSON '{chartName}' controller note {index} must contain exactly two button values.");

                        var buttonEnumerator = buttons.EnumerateArray();
                        buttonEnumerator.MoveNext();
                        bool hasTrigger = buttonEnumerator.Current.ValueKind == System.Text.Json.JsonValueKind.True;
                        buttonEnumerator.MoveNext();
                        bool hasGrip = buttonEnumerator.Current.ValueKind == System.Text.Json.JsonValueKind.True;
                        int stringLane = ReadJsonStringLane(noteNode, chartName);
                        if (!hasTrigger && !hasGrip)
                        {
                            ignoredEmptyControllerNotes++;
                            if (ModMain.ConfigChartDiagnostics && ignoredEmptyControllerNotes <= 5)
                                MelonLogger.Warning($"[ChartDiag] ignored empty controller note {index}: t={ReadJsonFloat(noteNode, "Time"):F3}s stringLane={stringLane}; both fret buttons were false.");
                            index++;
                            continue;
                        }

                        var controllerNote = new ControllerGuitarNote
                        {
                            Time = ReadJsonFloat(noteNode, "Time"),
                            Duration = ReadJsonFloat(noteNode, "Duration"),
                            CursableProbability = 0f,
                            IsBlind = false,
                            ChordRotation = ReadJsonInt(noteNode, "ChordRotation"),
                            Intensity = ReadJsonInt(noteNode, "Intensity"),
                            Buttons = new Il2CppSystem.Collections.Generic.List<bool>()
                        };
                        SetGameplayStringLane(controllerNote, stringLane);
                        controllerNote.Buttons.Add(hasTrigger);
                        controllerNote.Buttons.Add(hasGrip);
                        tierCounts[controllerNote.Tier] = tierCounts.TryGetValue(controllerNote.Tier, out int controllerTierCount) ? controllerTierCount + 1 : 1;
                        controllerGame.Notes.Add(controllerNote);

                        if (index < sampleLimit)
                            MelonLogger.Msg($"[ChartDiag] controller sample {index}: t={controllerNote.Time:F3}s d={controllerNote.Duration:F3}s stringLane={controllerNote.Tier} trigger={hasTrigger} grip={hasGrip} rotation={controllerNote.ChordRotation}");
                        index++;
                    }
                    loadedSelectedNotes = controllerGame.Notes.Count;
                    mapping.Keys.Add(controllerGame);
                }

                string tierSummary = string.Join(",", tierCounts.Select(pair => $"{pair.Key}:{pair.Value}"));
                MelonLogger.Msg($"[ChartDiag] Loaded {Path.GetFileName(usmPath)}!{chartName}: schema={schemaVersion}, mode={trackLabel}, selectedNotes={selectedNotes.GetArrayLength()}, loadedNotes={loadedSelectedNotes}, handNotes={handNotes.GetArrayLength()}, controllerNotes={controllerNotes.GetArrayLength()}, ignoredEmptyControllerNotes={ignoredEmptyControllerNotes}, stringLanes=[{tierSummary}], bpmChanges={mapping.BPMGrid.Count}, duration={chartDuration:F2}s, jsonParseMs={jsonParseMs}, gameObjectsMs={loadTimer.ElapsedMilliseconds - jsonParseMs}, totalMs={loadTimer.ElapsedMilliseconds}");
                if (loadedSelectedNotes == 0)
                    MelonLogger.Warning($"[ChartDiag] The selected {trackLabel} track in {chartName} has no notes.");

                return mapping;
            }
        }

        private static System.Text.Json.JsonElement RequireJsonArray(System.Text.Json.JsonElement parent, string propertyName, string archiveName)
        {
            if (parent.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !parent.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException($"Chart JSON '{archiveName}' is missing the '{propertyName}' array.");
            return value;
        }

        private static int ReadJsonInt(System.Text.Json.JsonElement parent, string propertyName)
        {
            return parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
                parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out int number) ? number : 0;
        }

        private static float ReadJsonFloat(System.Text.Json.JsonElement parent, string propertyName)
        {
            return parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
                parent.TryGetProperty(propertyName, out var value) && value.TryGetSingle(out float number) ? number : 0f;
        }

        private static bool ReadJsonBool(System.Text.Json.JsonElement parent, string propertyName)
        {
            return parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
                parent.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
        }

        private static int ReadJsonStringLane(System.Text.Json.JsonElement noteNode, string chartName)
        {
            int lane = noteNode.TryGetProperty("StringLane", out var explicitLane) && explicitLane.TryGetInt32(out int parsedLane)
                ? parsedLane
                : ReadJsonInt(noteNode, "Tier");
            if (lane < 0 || lane > 3)
                throw new InvalidDataException($"Chart JSON '{chartName}' contains string lane {lane}; expected 0-3.");
            return lane;
        }
#endif

        private static readonly Dictionary<Type, PropertyInfo> StringLaneProperties = new Dictionary<Type, PropertyInfo>();

        private static void SetGameplayStringLane(GuitarNote note, int lane)
        {
            if (note == null) throw new ArgumentNullException(nameof(note));
            if (lane < 0 || lane > 3)
                throw new InvalidDataException($"Chart string lane {lane} is outside the supported range 0-3.");

            // HandGuitarNote and ControllerGuitarNote override Tier's getter to
            // return their legacy `oldTier` property, but inherit the base Tier
            // setter. Assigning note.Tier therefore writes a different backing
            // field and makes every generated note appear on lane 0 in Unity.
            // Write the exact backing property used by both game getters.
            Type noteType = note.GetType();
            if (!StringLaneProperties.TryGetValue(noteType, out PropertyInfo oldTier))
            {
                for (Type type = noteType; type != null && oldTier == null; type = type.BaseType)
                {
                    var candidate = type.GetProperty("oldTier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (candidate != null && candidate.GetSetMethod(nonPublic: true) != null)
                        oldTier = candidate;
                }
                if (oldTier == null)
                    throw new MissingMemberException(noteType.FullName, "oldTier setter");
                StringLaneProperties.Add(noteType, oldTier);
            }
            oldTier.SetValue(note, lane);

            if (note.Tier != lane)
                throw new InvalidDataException($"Could not set {note.GetType().Name} string lane {lane}; game reports lane {note.Tier}.");
        }

        private static int ReadStringLane(JSONNode noteNode, string chartName)
        {
            // Schema 3 names the gameplay path explicitly. Schema 2 used Tier;
            // retain it as a backwards-compatible alias for existing .usm files.
            bool hasStringLane = noteNode != null && noteNode.IsObject && noteNode.HasKey("StringLane");
            int lane = hasStringLane ? noteNode["StringLane"].AsInt : noteNode["Tier"].AsInt;
            if (lane < 0 || lane > 3)
                throw new InvalidDataException($"Chart JSON '{chartName}' contains string lane {lane}; expected 0-3.");
            return lane;
        }

        public static void LogChartOutcome(KeyTime note, float timeStamp, bool succeeded)
        {
            if (!ModMain.ConfigChartDiagnostics || note == null)
                return;
            var currentSong = GameManager.CurrentSongData;
            if (currentSong == null || string.IsNullOrEmpty(currentSong.SongID) || !currentSong.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal))
                return;

#if QUEST_IL2CPP
            // Quest's hand/controller state getters cross into the Oculus tracking
            // runtime. Calling them from a note-result Harmony prefix can enter
            // Android JNI while Unity is processing the first controller note. Keep
            // Quest diagnostics limited to the already-loaded chart object itself.
            int questEventIndex = ChartOutcomeLogCount++;
            if (questEventIndex >= 40)
                return;

            string questInput = "noteType=other";
            string questMode = "unknown";
            // Harmony supplies the wrapper declared by RaiseFailed/RaiseSucceeded
            // (KeyTime), even when the underlying native object is a controller
            // note. A C# `is` test cannot inspect that native runtime type.
            var questControllerNote = note.TryCast<ControllerGuitarNote>();
            if (questControllerNote != null)
            {
                string trigger = questControllerNote.Buttons != null && questControllerNote.Buttons.Count > 0
                    ? questControllerNote.Buttons[0].ToString()
                    : "missing";
                string grip = questControllerNote.Buttons != null && questControllerNote.Buttons.Count > 1
                    ? questControllerNote.Buttons[1].ToString()
                    : "missing";
                questInput = $"noteType=controller tier={questControllerNote.Tier} trigger={trigger} grip={grip}";
                questMode = "controller";
            }
            else if (note.TryCast<HandGuitarNote>() is HandGuitarNote questHandNote)
            {
                questInput = $"noteType=hand tier={questHandNote.Tier}";
                questMode = "hand";
            }

            MelonLogger.Msg($"[ChartDiag] {(succeeded ? "HIT" : "MISS")} #{questEventIndex + 1} mode={questMode} eventTime={timeStamp:F3}s noteTime={note.Time:F3}s duration={note.Duration:F3}s {questInput}");
            return;
#else

            int eventIndex = ChartOutcomeLogCount++;
            if (eventIndex >= 40)
                return;

            string input = "noteType=other";
            if (note is ControllerGuitarNote controllerNote)
            {
                string trigger = controllerNote.Buttons != null && controllerNote.Buttons.Count > 0 ? controllerNote.Buttons[0].ToString() : "missing";
                string grip = controllerNote.Buttons != null && controllerNote.Buttons.Count > 1 ? controllerNote.Buttons[1].ToString() : "missing";
                input = $"noteType=controller tier={controllerNote.Tier} trigger={trigger} grip={grip}";
            }
            else if (note is HandGuitarNote handNote)
            {
                string fingers = "missing";
                if (handNote.Fingers != null)
                {
                    var fingerNames = new List<string>();
                    for (int i = 0; i < handNote.Fingers.Count; i++)
                        fingerNames.Add(handNote.Fingers[i].ToString());
                    fingers = string.Join("+", fingerNames);
                }
                input = $"noteType=hand tier={handNote.Tier} fingers={fingers}";
            }

            string runtimeType = "unknown";
            string actualState = "actualState=unavailable";
            try
            {
                runtimeType = note.GetType().FullName ?? note.GetType().Name;
                if (note is TierNoteBase tierNote)
                    input += $" expectedTier={tierNote.Tier}";

                var interaction = InteractionSystem.I;
                var primaryHand = interaction != null ? interaction.PrimaryHand : null;
                if (primaryHand != null)
                {
                    var controller = primaryHand.Controller;
                    string actualPose = "unavailable";
                    var guitarManager = UnityEngine.Object.FindObjectOfType<GuitarGameManager>();
                    if (guitarManager != null && guitarManager.HandPoseRecognition != null)
                    {
                        var pose = guitarManager.HandPoseRecognition.GetCurrentHandPose();
                        if (pose != null)
                        {
                            var poseNames = new List<string>();
                            for (int i = 0; i < pose.Count; i++)
                                poseNames.Add(pose[i].ToString());
                            actualPose = string.Join("+", poseNames);
                        }
                    }

                    string controllerState = controller != null
                        ? $"trigger={controller.CurrentTriggerState},grip={controller.CurrentGripState}"
                        : "controller=unavailable";
                    bool rotationUp = ControllerRotationIndicatorController.RotationIsUp;
                    bool rotationEnabled = GuitarGameManager.IsControllerRotationEnabled;
                    string selectedTier = guitarManager != null && guitarManager.SelectedFret != null
                        ? guitarManager.SelectedFret.Tier.ToString()
                        : "none";
                    actualState = $"actualState={controllerState},rotationUp={rotationUp},rotationEnabled={rotationEnabled},selectedTier={selectedTier},handTracked={primaryHand.IsTracked},handPose={actualPose},handPos={primaryHand.transform.position},grabPos={primaryHand.GrabPivot.position}";
                }
            }
            catch (Exception ex)
            {
                actualState = $"actualStateError={ex.GetType().Name}";
            }

            string mode = GameManager.CurrentSong != null && GameManager.CurrentSong.IsHandTrackingMapping ? "hand" : "controller";
            MelonLogger.Msg($"[ChartDiag] {(succeeded ? "HIT" : "MISS")} #{eventIndex + 1} mode={mode} eventTime={timeStamp:F3}s noteTime={note.Time:F3}s duration={note.Duration:F3}s {input} runtimeType={runtimeType} {actualState}");
#endif
        }

        public static void LoadCustomAudio(Mp3Player player, SongMappingMetaData data, bool loadSample)
        {
            var audioLoadTimer = Stopwatch.StartNew();
            string usmPath = ResolveUsmPath(data?.FileName);
            if (string.IsNullOrEmpty(usmPath))
            {
                MelonLogger.Error($"[CustomSongs] Cannot load audio: .usm archive '{data?.FileName}' was not found in '{CustomSongsFolder}'.");
                return;
            }

            string folder = Path.Combine(Application.temporaryCachePath, "USM_RUNTIME");

            try
            {
                ExtractCustomAudio(usmPath, folder);
                MelonLogger.Msg($"[AudioDiag] Extracted {Path.GetFileName(usmPath)}: elapsedMs={audioLoadTimer.ElapsedMilliseconds}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Failed to extract .usm for playback: {ex}");
                return;
            }

            if (player == null)
            {
                MelonLogger.Error($"[CustomSongs] Cannot load audio from '{Path.GetFileName(usmPath)}': Mp3Player is null.");
                return;
            }

            string audioLayout = ReadAudioLayout(usmPath);
            bool layoutRequiresStems = string.Equals(audioLayout, "full-multitrack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(audioLayout, "two-track-instrumental-vocals", StringComparison.OrdinalIgnoreCase);
            bool layoutDeclaresSingleTrack = string.Equals(audioLayout, "single-track", StringComparison.OrdinalIgnoreCase);
            bool isMulti = layoutRequiresStems || (!layoutDeclaresSingleTrack && data.IsMultiTrack && player.AllowMultitrack);

            // CRITICAL: Set IsMultiTrack property via Traverse
            Traverse.Create(player).Property("IsMultiTrack").SetValue(isMulti);

            MelonLogger.Msg($"[AudioDiag] Preparing '{data.SongName}', AudioLayout='{audioLayout}', IsMultiTrack={isMulti}.");

            var musicMixerGroup = player.MusicAudioSource != null ? player.MusicAudioSource.outputAudioMixerGroup : null;

            if (isMulti)
            {
                // Clear any leftover clips
                for (int i = 0; i < player.AudioSources.Count; i++)
                {
                    if (player.AudioSources[i] != null) player.AudioSources[i].clip = null;
                }

                bool hasGuitar = LoadTrack(player, folder, "song-guitar.mp3", Mp3Player.SongTrackNames.Guitar);
                bool hasBass = LoadTrack(player, folder, "song-bass.mp3", Mp3Player.SongTrackNames.Bass);
                bool hasDrums = LoadTrack(player, folder, "song-drums.mp3", Mp3Player.SongTrackNames.Drums);
                bool hasVocals = LoadTrack(player, folder, "song-vocals.mp3", Mp3Player.SongTrackNames.Vocals);
                bool hasOther = LoadTrack(player, folder, "song-other.mp3", Mp3Player.SongTrackNames.Other);

                // Configure EVERY AudioSource to be 2D stereo and route to the same music mixer group
                for (int i = 0; i < player.AudioSources.Count; i++)
                {
                    var src = player.AudioSources[i];
                    if (src != null)
                    {
                        src.spatialBlend = 0f;
                        src.spatialize = false;
                        src.mute = false;
                        src.enabled = true;
                        if (musicMixerGroup != null) src.outputAudioMixerGroup = musicMixerGroup;
                    }
                }

                AudioSource masterSrc = null;
                // Choose Drums, Other, Vocals or Bass as master sync clock (never Guitar so guitar muting never affects sync)
                int[] syncCandidates = {
                    (int)Mp3Player.SongTrackNames.Drums,
                    (int)Mp3Player.SongTrackNames.Other,
                    (int)Mp3Player.SongTrackNames.Vocals,
                    (int)Mp3Player.SongTrackNames.Bass
                };
                foreach (int idx in syncCandidates)
                {
                    if (idx < player.AudioSources.Count && player.AudioSources[idx] != null && player.AudioSources[idx].clip != null)
                    {
                        masterSrc = player.AudioSources[idx];
                        break;
                    }
                }
                if (masterSrc == null)
                {
                    foreach (var s in player.AudioSources)
                    {
                        if (s != null && s.clip != null)
                        {
                            masterSrc = s;
                            break;
                        }
                    }
                }

                player.MusicAudioSource = masterSrc;
                MelonLogger.Msg($"[CustomSongs] MultiTrack stems loaded: Guitar={hasGuitar}, Bass={hasBass}, Drums={hasDrums}, Vocals={hasVocals}, Other={hasOther}. MasterSyncSource={masterSrc?.clip?.name}");
            }
            else
            {
                string primaryName = loadSample ? "sample" : "song";
                string masterPath = Path.Combine(folder, primaryName + ".ogg");
                if (!File.Exists(masterPath)) masterPath = Path.Combine(folder, primaryName + ".mp3");
                if (!File.Exists(masterPath)) masterPath = Path.Combine(folder, "song.ogg");
                if (!File.Exists(masterPath)) masterPath = Path.Combine(folder, "song.mp3");
                if (File.Exists(masterPath))
                {
                    AudioClip clip = LoadClip(masterPath);
                    if (clip != null)
                    {
                        clip.name = "song";
                        if (player.MusicAudioSource != null)
                        {
                            player.MusicAudioSource.clip = clip;
                            player.MusicAudioSource.volume = 1f;
                            player.MusicAudioSource.spatialBlend = 0f;
                            player.MusicAudioSource.spatialize = false;
                            player.MusicAudioSource.mute = false;
                            player.MusicAudioSource.enabled = true;
                            if (musicMixerGroup != null) player.MusicAudioSource.outputAudioMixerGroup = musicMixerGroup;
                            MelonLogger.Msg($"[CustomSongs] SingleTrack master audio clip loaded: {Path.GetFileName(usmPath)}!{Path.GetFileName(masterPath)}");
                        }
                        else
                        {
                            MelonLogger.Error($"[CustomSongs] Mp3Player has no MusicAudioSource for '{Path.GetFileName(usmPath)}'.");
                        }
                    }
                }
                else
                {
                    MelonLogger.Error($"[CustomSongs] No sample/song .ogg or .mp3 was extracted from '{Path.GetFileName(usmPath)}'.");
                }
            }

            MelonLogger.Msg($"[AudioDiag] Prepared {Path.GetFileName(usmPath)}: totalMs={audioLoadTimer.ElapsedMilliseconds}, masterClipReady={player.MusicAudioSource != null && player.MusicAudioSource.clip != null}.");
        }

        private static void ExtractCustomAudio(string usmPath, string folder)
        {
            Directory.CreateDirectory(folder);
            // Preview requests own preview_<id> files until their decoder has
            // finished. Remove only audio files from the previous song load.
            // Deleting the whole directory could interrupt those requests.
            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);
                bool isAudio = name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
                if (isAudio && !name.StartsWith("preview_", StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
            using (var archive = System.IO.Compression.ZipFile.OpenRead(usmPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) &&
                        !entry.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                    string destination = Path.Combine(folder, entry.Name);
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                        input.CopyTo(output);
                }
            }
        }

        public static void StopCustomAudioPreview()
        {
            if (PendingPreviewArchivePath == null && ActivePreviewArchivePath == null) return;

            // Invalidate a decoder that is still running before it can start a
            // custom clip over a vanilla album's silent menu.
            PreviewLoadGeneration++;
            PendingPreviewArchivePath = null;
            ActivePreviewArchivePath = null;

            var player = GameManager.I != null ? GameManager.I.musicPlayer : null;
            if (player == null) player = UnityEngine.Object.FindObjectOfType<Mp3Player>();
            var source = player?.MusicAudioSource;
            var clip = source?.clip;
            if (clip == null || !clip.name.StartsWith("sample_", StringComparison.Ordinal)) return;

            source.Stop();
            source.clip = null;
            UnityEngine.Object.Destroy(clip);
            if (ModMain.ConfigChartDiagnostics)
                MelonLogger.Msg("[PreviewDiag] Stopped custom sample after leaving Custom Songs.");
        }

        public static void PlayCustomAudioSample(Mp3Player player, string path)
        {
            try
            {
                string folderName = Path.GetFileName(path);
                string usmPath = Path.Combine(CustomSongsFolder, folderName + ".usm");
                if (!File.Exists(usmPath)) usmPath = Path.Combine(CustomSongsFolder, folderName);
                if (!File.Exists(usmPath))
                {
                    if (Directory.Exists(CustomSongsFolder))
                    {
                        foreach (var d in Directory.GetFiles(CustomSongsFolder, "*.usm"))
                        {
                            if (path.Contains(Path.GetFileName(d)) || Path.GetFileName(d).Contains(folderName))
                            {
                                usmPath = d;
                                break;
                            }
                        }
                    }
                }

                if (!File.Exists(usmPath))
                {
                    MelonLogger.Warning($"[CustomSongs] Custom song .usm not found for sample: {path}");
                    return;
                }

                string previewArchivePath;
                try { previewArchivePath = Path.GetFullPath(usmPath); }
                catch { previewArchivePath = usmPath; }

                if (string.Equals(PendingPreviewArchivePath, previewArchivePath, StringComparison.OrdinalIgnoreCase)) return;

                string expectedClipName = "sample_" + Path.GetFileName(usmPath);
                if (player != null && player.MusicAudioSource != null &&
                    string.Equals(ActivePreviewArchivePath, previewArchivePath, StringComparison.OrdinalIgnoreCase) &&
                    player.MusicAudioSource.clip != null && player.MusicAudioSource.clip.name == expectedClipName &&
                    player.MusicAudioSource.isPlaying)
                {
                    return;
                }

                string tempDir = Path.Combine(Application.temporaryCachePath, "USM_RUNTIME");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string samplePath = null;

                using (var archive = System.IO.Compression.ZipFile.OpenRead(usmPath))
                {
                    var entry = FindArchiveEntry(archive, "sample.ogg", "sample.mp3", "song.ogg", "song.mp3");
                    if (entry != null)
                    {
                        // Each asynchronous request owns its file; changing the
                        // selection must not overwrite audio still being read.
                        samplePath = Path.Combine(tempDir, "preview_" + Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
                        using (var fs = new FileStream(samplePath, FileMode.Create, FileAccess.Write))
                        {
                            entry.Open().CopyTo(fs);
                        }
                    }
                }

                if (samplePath != null && File.Exists(samplePath))
                {
                    int request = ++PreviewLoadGeneration;
                    PendingPreviewArchivePath = previewArchivePath;
                    MelonCoroutines.Start(LoadPreviewClipAsync(samplePath, () => request == PreviewLoadGeneration, clip =>
                    {
                        if (request != PreviewLoadGeneration)
                        {
                            if (clip != null) UnityEngine.Object.Destroy(clip);
                            return;
                        }
                        PendingPreviewArchivePath = null;
                        var menu = StartSpace.I?.songMenu;
                        string currentArchive = menu != null && menu.gameObject.activeInHierarchy
                            ? ResolveUsmPath(menu.SelectedSong?.FileName) : null;
                        if (GameManager.IsPlaying || AlbumsManager.SelectedAlbum?.AlbumID != "CustomSongsAlbum" ||
                            !string.Equals(currentArchive, previewArchivePath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (clip != null) UnityEngine.Object.Destroy(clip);
                            return;
                        }
                        if (clip != null && player != null && player.MusicAudioSource != null)
                        {
                            clip.name = expectedClipName;
                            var previousClip = player.MusicAudioSource.clip;
                            player.MusicAudioSource.clip = clip;
                            player.MusicAudioSource.loop = true;
                            player.MusicAudioSource.volume = 1f;
                            player.MusicAudioSource.Play();
                            if (previousClip != null && previousClip != clip &&
                                previousClip.name.StartsWith("sample_", StringComparison.Ordinal))
                                UnityEngine.Object.Destroy(previousClip);
                            ActivePreviewArchivePath = previewArchivePath;
                            MelonLogger.Msg($"[CustomSongs] Turntable audio preview started for '{folderName}'");

                            // Live update Turntable Vinyl Texture to this song's cover!
                            try
                            {
                                Texture2D songCover = null;
                                if (CustomAlbumInstance != null && CustomAlbumInstance.builtInSongsMetadata != null)
                                {
                                    SongMappingMetaData foundMeta = null;
                                    foreach (var meta in CustomAlbumInstance.builtInSongsMetadata)
                                    {
                                        if (meta != null && meta.FileName == folderName)
                                        {
                                            foundMeta = meta;
                                            break;
                                        }
                                    }
                                    if (foundMeta != null && foundMeta.Thumbnail != null)
                                    {
                                        songCover = foundMeta.Thumbnail;
                                    }
                                }
                                                            if (songCover == null)
                                {
                                    using (var archive2 = System.IO.Compression.ZipFile.OpenRead(usmPath))
                                    {
                                        var coverEntry = archive2.GetEntry("album.jpg") ?? archive2.GetEntry("cover.jpg") ?? archive2.GetEntry("album.png") ?? archive2.GetEntry("cover.png");
                                        if (coverEntry != null)
                                        {
                                            using (var ms = new MemoryStream())
                                            {
                                                coverEntry.Open().CopyTo(ms);
                                                songCover = new Texture2D(2, 2);
                                                if (ImageConversion.LoadImage(songCover, ms.ToArray()))
                                                {
                                                    songCover.name = folderName;
                                                }
                                                else
                                                {
                                                    songCover = null;
                                                }
                                            }
                                        }
                                    }
                                }
                                if (songCover != null)
                                {
                                    if (StartSpace.I != null)
                                    {
                                        var r1 = StartSpace.I.StartSpaceLp?.GetComponent<Renderer>();
                                        if (r1 != null && r1.material != null) r1.material.SetTexture("_MainTex", songCover);
                                        var r2 = StartSpace.I.StartSpaceAuxLp?.GetComponent<Renderer>();
                                        if (r2 != null && r2.material != null) r2.material.SetTexture("_MainTex", songCover);
                                    }
                                    if (CustomAlbumInstance != null)
                                    {
                                        CustomAlbumInstance.VinylTexture = songCover;
                                    }
                                }
                            }
                            catch { }
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in PlayCustomAudioSample: {ex}");
            }
        }

        private static IEnumerator LoadPreviewClipAsync(string filePath, Func<bool> isCurrent, Action<AudioClip> onLoaded)
        {
            string clean = filePath.Replace("\\", "/");
            string url = clean.StartsWith("/") ? "file://" + clean : "file:///" + clean;
            AudioType audioType = clean.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.MPEG;
            WWW request = null;
            try { request = new WWW(url); }
            catch (Exception ex) { MelonLogger.Error($"[PreviewDiag] Could not open sample: {ex.Message}"); }
            var timer = Stopwatch.StartNew();
            try
            {
                // Yield to Unity while the decoder reads the file. The previous
                // Thread.Sleep loop could block this thread for five seconds
                // for every preview and trigger Android's not-responding dialog.
                while (request != null && !request.isDone && isCurrent() && timer.ElapsedMilliseconds < 10000)
                    yield return null;

                AudioClip clip = null;
                try
                {
                    if (request != null && isCurrent())
                    {
                        if (!request.isDone)
                            MelonLogger.Error("[PreviewDiag] Sample decode timed out after 10 seconds (menu remained responsive).");
                        else if (!string.IsNullOrEmpty(request.error))
                            MelonLogger.Error($"[PreviewDiag] Sample decode failed: {request.error}");
                        else
                            clip = request.GetAudioClip(false, false, audioType);
                    }
                    onLoaded(clip);
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[PreviewDiag] Decode finished: elapsedMs={timer.ElapsedMilliseconds}, canceled={!isCurrent()}, clipReady={clip != null}.");
                }
                catch (Exception ex) { MelonLogger.Error($"[PreviewDiag] Could not finish sample playback: {ex}"); }
            }
            finally
            {
                request?.Dispose();
                try { File.Delete(filePath); } catch { }
            }
        }

        private static bool LoadTrack(Mp3Player player, string folder, string fileName, Mp3Player.SongTrackNames track)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string[] candidates = { Path.Combine(folder, baseName + ".ogg"), Path.Combine(folder, baseName + ".mp3") };
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) continue;
                AudioClip clip = LoadClip(p);
                if (clip != null)
                {
                    clip.name = baseName;
                    int idx = (int)track;
                    if (idx >= 0 && idx < player.AudioSources.Count && player.AudioSources[idx] != null)
                    {
                        player.AudioSources[idx].clip = clip;
                        player.AudioSources[idx].volume = 1f;
                        return true;
                    }
                }
            }
            return false;
        }

        private static AudioClip LoadClip(string filePath)
        {
            var decodeTimer = Stopwatch.StartNew();
            string clean = filePath.Replace("\\", "/");
            string url = clean.StartsWith("/") ? "file://" + clean : "file:///" + clean;
            AudioType aType = AudioType.MPEG;
            if (clean.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) aType = AudioType.OGGVORBIS;
            else if (clean.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) aType = AudioType.WAV;

            var www = new WWW(url);
            int timeout = 0;
            while (!www.isDone && timeout < 500)
            {
                System.Threading.Thread.Sleep(10);
                timeout++;
            }
            try
            {
                if (!www.isDone)
                {
                    MelonLogger.Error($"[AudioDiag] Decode timed out: {Path.GetFileName(filePath)}, elapsedMs={decodeTimer.ElapsedMilliseconds}.");
                    return null;
                }
                if (!string.IsNullOrEmpty(www.error))
                {
                    MelonLogger.Error($"[AudioDiag] Decode failed: {Path.GetFileName(filePath)}, error={www.error}.");
                    return null;
                }
                AudioClip clip = www.GetAudioClip(false, false, aType);
                MelonLogger.Msg($"[AudioDiag] Decoded {Path.GetFileName(filePath)}: elapsedMs={decodeTimer.ElapsedMilliseconds}, clipReady={clip != null}.");
                return clip;
            }
            finally { www.Dispose(); }
        }

        public static DateTime GetSongFolderTimestamp(string dir)
        {
            try
            {
                DateTime latest = Directory.GetLastWriteTimeUtc(dir);
                DateTime created = Directory.GetCreationTimeUtc(dir);
                if (created > latest) latest = created;

                string dataXml = Path.Combine(dir, "data.xml");
                if (File.Exists(dataXml))
                {
                    DateTime xmlTime = File.GetLastWriteTimeUtc(dataXml);
                    if (xmlTime > latest) latest = xmlTime;
                }

                string songIni = Path.Combine(dir, "song.ini");
                if (File.Exists(songIni))
                {
                    DateTime iniTime = File.GetLastWriteTimeUtc(songIni);
                    if (iniTime > latest) latest = iniTime;
                }

                return latest;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        public static int GetLatestSongIndex()
        {
            if (CustomAlbumInstance == null || CustomAlbumInstance.builtInSongsMetadata == null || CustomAlbumInstance.builtInSongsMetadata.Count == 0)
                return -1;

            if (!Directory.Exists(CustomSongsFolder))
                return -1;

            try
            {
                var newestFile = Directory.GetFiles(CustomSongsFolder)
                    .Where(path => string.Equals(Path.GetExtension(path), ".usm", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(newestFile)) return -1;

                string fileName = Path.GetFileName(newestFile);
                int index = -1;
                for (int i = 0; i < CustomAlbumInstance.builtInSongsMetadata.Count; i++)
                {
                    var metadata = CustomAlbumInstance.builtInSongsMetadata[i];
                    if (metadata != null && string.Equals(Path.GetFileName(metadata.FileName), fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                return index;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Error finding newest song index: {ex.Message}");
                return -1;
            }
        }

        public static int GetLastPlayedCustomSongIndex()
        {
            if (CustomAlbumInstance?.builtInSongsMetadata == null || CustomAlbumInstance.builtInSongsMetadata.Count == 0)
                return -1;
            if (!string.Equals(GamePreferences.Load(UnpluggedInfo.LastSongPlayedAlbum, ""), "CustomSongsAlbum", StringComparison.Ordinal))
                return -1;

            string savedFileName = PlayerPrefs.GetString(LastCustomSongFilePreference, "");
            if (!string.IsNullOrWhiteSpace(savedFileName))
            {
                savedFileName = Path.GetFileName(savedFileName);
                for (int i = 0; i < CustomAlbumInstance.builtInSongsMetadata.Count; i++)
                {
                    var metadata = CustomAlbumInstance.builtInSongsMetadata[i];
                    if (metadata != null && string.Equals(Path.GetFileName(metadata.FileName), savedFileName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            int savedIndex = GamePreferences.Load(UnpluggedInfo.LastSongPlayed, 0);
            return savedIndex >= 0 && savedIndex < CustomAlbumInstance.builtInSongsMetadata.Count ? savedIndex : -1;
        }

        public static void QueueDirectSelectionIndex(int index)
        {
            _pendingDirectSelectionIndex = Math.Max(0, index);
            _hasPendingDirectSelection = true;
        }

        public static void MarkSavedCustomAlbumRestored()
        {
            _pendingSavedAlbumRestore = false;
        }

        public static int ConsumeDirectSelectionIndex(int songCount)
        {
            if (songCount <= 0) return 0;
            int target = _hasPendingDirectSelection
                ? _pendingDirectSelectionIndex
                : GetLastPlayedCustomSongIndex();
            _hasPendingDirectSelection = false;
            _pendingDirectSelectionIndex = 0;
            if (target < 0) target = 0;
            return Mathf.Clamp(target, 0, songCount - 1);
        }
    }

    [HarmonyPatch(typeof(AlbumsManager), "Awake")]
    public static class Patch_AlbumsManager_Awake
    {
        public static void Postfix(AlbumsManager __instance)
        {
            try
            {
                // Let the game's Awake initialize its album lists first. The mod
                // reads .usm metadata on a worker and registers the Unity assets
                // on the main thread after that scan has completed.
                CustomSongLoader.QueueCustomAlbumRegistration(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error queuing custom album registration after Awake: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(AlbumData), "RetrieveSongsMetadata")]
    public static class Patch_AlbumData_RetrieveSongsMetadata
    {
        public static bool Prefix(AlbumData __instance)
        {
            if (__instance.AlbumID == "CustomSongsAlbum")
            {
                CustomSongLoader.PopulateCustomSongs(__instance);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(StartSpace), "AlbumSelected")]
    public static class Patch_StartSpace_AlbumSelected
    {
        public static void Postfix(AlbumData album)
        {
            try
            {
                if (album == null || album.AlbumID != "CustomSongsAlbum")
                {
                    CustomSongLoader.StopCustomAudioPreview();
                    return;
                }
                int targetIndex = CustomSongLoader.GetLastPlayedCustomSongIndex();
                if (targetIndex < 0) targetIndex = 0;
                CustomSongLoader.QueueDirectSelectionIndex(targetIndex);
                if (StartSpace.I != null && StartSpace.I.songMenu != null)
                    StartSpace.I.songMenu.InitialOffset = 0;
                MelonLogger.Msg($"[CustomSongs] Album selected; queued direct song index {targetIndex} (no carousel walk).");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in AlbumSelected prefix: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(InteractiveHandOculus), "get_IsTracked")]
    public static class Patch_InteractiveHandOculus_IsTracked
    {
        private static readonly Dictionary<int, bool> LastControllerTrackingState = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> LastHandTrackingMode = new Dictionary<int, bool>();
        private static readonly Dictionary<int, HandSideState> OriginalHandSides = new Dictionary<int, HandSideState>();
        private static readonly HashSet<int> NormalizedControllerHands = new HashSet<int>();

        private sealed class HandSideState
        {
            public bool? ControllerIsLeft;
            public bool? SkeletonIsLeft;
            public bool? ControllerSkeletonIsLeft;
        }

        private static HandSkeletonControllerOculus GetControllerSkeleton(InteractiveHandOculus hand)
        {
#if QUEST_IL2CPP
            return hand.skeletonController?.TryCast<HandSkeletonControllerOculus>();
#else
            return hand.skeletonController as HandSkeletonControllerOculus;
#endif
        }

        public static void RememberOriginalHandSide(InteractiveHandOculus hand)
        {
            if (hand == null) return;
            var controllerSkeleton = GetControllerSkeleton(hand);
            OriginalHandSides[hand.GetInstanceID()] = new HandSideState
            {
                ControllerIsLeft = hand.Controller != null ? hand.Controller.IsLeftController : (bool?)null,
                SkeletonIsLeft = hand.SkeletonInfo != null ? hand.SkeletonInfo.IsLeftHand : (bool?)null,
                ControllerSkeletonIsLeft = controllerSkeleton != null ? controllerSkeleton.IsLeftController : (bool?)null
            };
        }

        private static void RestoreOriginalHandSide(InteractiveHandOculus hand, int instanceId)
        {
            if (!NormalizedControllerHands.Remove(instanceId) || !OriginalHandSides.TryGetValue(instanceId, out var original)) return;
            if (original.ControllerIsLeft.HasValue && hand.Controller != null)
                hand.Controller.IsLeftController = original.ControllerIsLeft.Value;
            if (original.SkeletonIsLeft.HasValue && hand.SkeletonInfo != null)
                hand.SkeletonInfo.IsLeftHand = original.SkeletonIsLeft.Value;
            var controllerSkeleton = GetControllerSkeleton(hand);
            if (original.ControllerSkeletonIsLeft.HasValue && controllerSkeleton != null)
                controllerSkeleton.IsLeftController = original.ControllerSkeletonIsLeft.Value;
        }

        public static void NormalizeHandSide(InteractiveHandOculus hand)
        {
            if (hand == null) return;
            bool isLeft = hand.IsLeftHand;
            if (hand.Controller != null && hand.Controller.IsLeftController != isLeft)
                hand.Controller.IsLeftController = isLeft;
            if (hand.SkeletonInfo != null && hand.SkeletonInfo.IsLeftHand != isLeft)
                hand.SkeletonInfo.IsLeftHand = isLeft;
            var controllerSkeleton = GetControllerSkeleton(hand);
            if (controllerSkeleton != null && controllerSkeleton.IsLeftController != isLeft)
                controllerSkeleton.IsLeftController = isLeft;
        }

        private static void ResetRecoveredControllerHand(InteractiveHandOculus hand)
        {
            try
            {
                GameFieldAccess.Set(hand, "prevPosition", hand.transform.position);
                GameFieldAccess.Set(hand, "prevPositionIndex", hand.IndexTip != null ? hand.IndexTip.position : hand.transform.position);
                hand.Velocity = Vector3.zero;
                hand.IndexVelocity = Vector3.zero;
                GameFieldAccess.Set(hand, "prevIsTracked", true);

                // Let the game's own input-mode-change handler rebuild the Oculus
                // skeleton from its calibrated bind pose. Manually zeroing Wrist
                // localPosition/Rotation changes that bind pose and can leave a
                // recovered hand displaced or turned after tracking resumes.
#if QUEST_IL2CPP
                var controllerSkeleton = hand.skeletonController?.TryCast<HandSkeletonControllerOculus>();
#else
                var controllerSkeleton = hand.skeletonController as HandSkeletonControllerOculus;
#endif
                if (controllerSkeleton != null)
                    Traverse.Create(controllerSkeleton)
                        .Method("VRPlatformBase_InputControllerChanged", VRInteractionType.Controllers)
                        .GetValue();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HandPoseDiag] Could not reset the recovered {(hand.IsLeftHand ? "left" : "right")} controller hand: {ex.Message}");
            }
        }

        public static bool Prefix(InteractiveHandOculus __instance, ref bool __result)
        {
            try
            {
                if (__instance == null)
                    return true;

                if (VRPlatformBase.I == null)
                    return true;

                int instanceId = __instance.GetInstanceID();
                bool nativeHandTracking = VRPlatformBase.I.IsHandTrackingEnabled;
                if (!LastHandTrackingMode.TryGetValue(instanceId, out bool previousMode) || previousMode != nativeHandTracking)
                {
                    LastHandTrackingMode[instanceId] = nativeHandTracking;
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[HandPoseDiag] {(__instance.IsLeftHand ? "left" : "right")} Oculus hand mode={(nativeHandTracking ? "native hands" : "controllers")}; native pose stays untouched in hand mode.");
                }
                if (nativeHandTracking)
                {
                    RestoreOriginalHandSide(__instance, instanceId);
                    return true;
                }

                if (!OriginalHandSides.ContainsKey(instanceId)) RememberOriginalHandSide(__instance);
                NormalizeHandSide(__instance);
                NormalizedControllerHands.Add(instanceId);

                if (Params.Global.DisableControllerMode)
                {
                    __result = false;
                    return false;
                }

                // The game's Oculus implementation checks LTouch twice. When one
                // controller sleeps on a table, the other controller can keep its
                // hand mesh marked tracked and leave that mesh at a stale pose.
                // Query the controller that owns this hand instead.
                var controller = __instance.IsLeftHand
                    ? OVRInput.Controller.LTouch
                    : OVRInput.Controller.RTouch;
                bool tracked = OVRInput.GetControllerPositionTracked(controller);
                __result = tracked;

                bool hadPrevious = LastControllerTrackingState.TryGetValue(instanceId, out bool previous);
                if (!hadPrevious || previous != tracked)
                {
                    LastControllerTrackingState[instanceId] = tracked;
                    if (hadPrevious && !previous && tracked)
                    {
                        ResetRecoveredControllerHand(__instance);
                        if (ModMain.ConfigChartDiagnostics)
                            MelonLogger.Msg($"[HandPoseDiag] {( __instance.IsLeftHand ? "left" : "right" )} controller reacquired; hand side, wrist, skeleton and velocity state reset.");
                    }
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[HandPoseDiag] {( __instance.IsLeftHand ? "left" : "right" )} controller tracking={tracked}.");
                }
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HandPoseDiag] Controller tracking query failed; using the game's getter: {ex.Message}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(InteractiveHandOculus), "Awake")]
    public static class Patch_InteractiveHandOculus_Awake
    {
        public static void Postfix(InteractiveHandOculus __instance)
        {
            try
            {
                Patch_InteractiveHandOculus_IsTracked.RememberOriginalHandSide(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HandPoseDiag] Could not remember the original Oculus hand side during Awake: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(StartSpace), "SetInitialSongInMenu")]
    public static class Patch_StartSpace_SetInitialSongInMenu
    {
        public static bool Prefix(StartSpace __instance)
        {
            try
            {
                string lastAlbum = GamePreferences.Load(UnpluggedInfo.LastSongPlayedAlbum, "");
                if (!string.Equals(lastAlbum, "CustomSongsAlbum", StringComparison.Ordinal))
                    return true;

                // StartSpace.Awake calls this before the mod has scanned and
                // registered the custom album. The game's method only copies
                // LastSongPlayed into SongMenu.InitialOffset; applying that
                // custom index to the temporary campaign list starts a visible
                // carousel walk (and plays each intervening preview). Skip that
                // assignment entirely; the custom album is restored once its
                // metadata is ready and CreateAlbum centers the saved song.
                if (__instance?.songMenu != null)
                    __instance.songMenu.InitialOffset = 0;
                MelonLogger.Msg("[StartupDiag] Skipped vanilla LastSongPlayed offset because it belongs to the custom album; campaign carousel starts at index 0.");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not defer the custom startup offset: {ex.Message}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(SongMenu), "CreateAlbum")]
    public static class Patch_SongMenu_CreateCustomAlbumDirectly
    {
        public static void Prefix(SongMenu __instance)
        {
            try
            {
                if (AlbumsManager.SelectedAlbum == null || AlbumsManager.SelectedAlbum.AlbumID != "CustomSongsAlbum")
                    return;

                var metadata = CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata;
                int songCount = metadata?.Count ?? 0;
                if (songCount <= 0) return;
#if QUEST_IL2CPP
                var songList = GameFieldAccess.Get<Il2CppSystem.Collections.Generic.List<SongMappingMetaData>>(__instance, "songList");
#else
                var songList = GameFieldAccess.Get<List<SongMappingMetaData>>(__instance, "songList");
#endif
                if (songList == null || songList.Count != songCount) return;

                int targetIndex = CustomSongLoader.ConsumeDirectSelectionIndex(songCount);
                int maxVisible = Mathf.Min(__instance.MaxItems, songCount);
                int centerIndex = Mathf.CeilToInt(maxVisible / 2f) - 1;
                int targetPosition = targetIndex;
                for (int i = 0; i < songList.Count; i++)
                {
                    if (songList[i] != null && metadata[targetIndex] != null &&
                        string.Equals(songList[i].SongID, metadata[targetIndex].SongID, StringComparison.Ordinal))
                    {
                        targetPosition = i;
                        break;
                    }
                }

                // Rotate the menu's private list before it creates any cards. The
                // saved song is therefore already in the center slot; neither
                // CreateAlbum nor Update needs to walk through the songs.
                bool alreadyCentered = songList[centerIndex].SongID == metadata[targetIndex].SongID;
                if (!alreadyCentered)
                {
#if QUEST_IL2CPP
                    var rotated = new Il2CppSystem.Collections.Generic.List<SongMappingMetaData>();
#else
                    var rotated = new List<SongMappingMetaData>();
#endif
                    for (int i = 0; i < songCount; i++)
                    {
                        int sourceIndex = ((i - centerIndex + targetPosition) % songCount + songCount) % songCount;
                        rotated.Add(songList[sourceIndex]);
                    }
                    GameFieldAccess.Set(__instance, "songList", rotated);
                }

                __instance.InitialOffset = centerIndex;
                GameFieldAccess.Set(__instance, "CumulativeIndex", 0);
                GameFieldAccess.Set(__instance, "Index", 0);
                GameFieldAccess.Set(__instance, "CumulativeOffset", 0f);
                GameFieldAccess.Set(__instance, "selectedIndex", centerIndex);
                MelonLogger.Msg($"[CustomSongs] Direct album selection prepared: index={targetIndex}, song='{metadata[targetIndex].SongName}', centerSlot={centerIndex}, animatedSteps=0.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Could not prepare direct album selection: {ex}");
            }
        }
    }

    // Fix for 2-song carousel virtualization: when an album has exactly 2 songs, instantiate both!
    [HarmonyPatch(typeof(SongMenu), "generateItems")]
    public static class Patch_SongMenu_generateItems
    {
        public static bool Prefix(SongMenu __instance)
        {
#if QUEST_IL2CPP
            var songList = GameFieldAccess.Get<Il2CppSystem.Collections.Generic.List<SongMappingMetaData>>(__instance, "songList");
#else
            var songList = GameFieldAccess.Get<List<SongMappingMetaData>>(__instance, "songList");
#endif
            if (songList != null && songList.Count == 2)
            {
                GameFieldAccess.Set(__instance, "smoothVirtualizedOffset", 0f);
                GameFieldAccess.Set(__instance, "Offset", 0.001f);
                GameProgress.Load();

#if QUEST_IL2CPP
                var virtualizedItemList = GameFieldAccess.Get<Il2CppSystem.Collections.Generic.List<SongMenuItem>>(__instance, "virtualizedItemList");
#else
                var virtualizedItemList = GameFieldAccess.Get<List<SongMenuItem>>(__instance, "virtualizedItemList");
#endif
                if (virtualizedItemList == null)
                {
#if QUEST_IL2CPP
                    virtualizedItemList = new Il2CppSystem.Collections.Generic.List<SongMenuItem>();
#else
                    virtualizedItemList = new List<SongMenuItem>();
#endif
                    GameFieldAccess.Set(__instance, "virtualizedItemList", virtualizedItemList);
                }
                foreach (var item in virtualizedItemList)
                {
                    if (item != null) UnityEngine.Object.Destroy(item.gameObject);
                }
                virtualizedItemList.Clear();

                Transform itemsPresenter = __instance.ItemsPresenter;
                GameObject songItemPrefab = __instance.SongItemPrefab;

                for (int i = 0; i < 2; i++)
                {
                    SongMappingMetaData songMappingMetaData = songList[i];
                    GameObject gameObject = new GameObject("ItemContainer");
                    gameObject.transform.SetParent(itemsPresenter, worldPositionStays: false);
                    SongMenuItem componentInChildren = UnityEngine.Object.Instantiate(songItemPrefab, gameObject.transform, worldPositionStays: false).GetComponentInChildren<SongMenuItem>();
                    componentInChildren.SongData = songMappingMetaData;
                    componentInChildren.DebugName = i + "  " + songMappingMetaData.BandName;
                    componentInChildren.InitialIndex = (i == 0 ? 0.5f : 0.0f);
                    virtualizedItemList.Add(componentInChildren);
                    componentInChildren.SetOffSet(0f);
                }

                MelonLogger.Msg("[CustomSongs] Generated 2 carousel items for 2 custom songs (front & back at 180°)");
                return false;
            }
            return true;
        }
    }

#if !QUEST_IL2CPP
    [HarmonyPatch(typeof(VRPlatformSteam), "IsHandTrackingEnabled", MethodType.Getter)]
    public static class Patch_VRPlatformSteam_IsHandTrackingEnabled
    {
        public static bool Prefix(ref bool __result)
        {
            __result = GamePreferences.Load(UnpluggedInfo.IndexHandsMode, defaultValue: false);
            return false;
        }
    }
#endif

    [HarmonyPatch(typeof(SongMapping), "Load", new Type[] { typeof(SongMappingMetaData), typeof(bool) })]
    public static class Patch_SongMapping_Load
    {
        public static bool Prefix(SongMappingMetaData song, bool loadHandTrackingTrack, ref SongMapping __result)
        {
            if (song != null && !string.IsNullOrEmpty(song.SongID) && song.SongID.StartsWith("CUSTOM_"))
            {
                try
                {
                    if (ModMain.ConfigChartDiagnostics)
                        MelonLogger.Msg($"[ChartDiag] Load begin: songId={song.SongID}, file={song.FileName}, requestedMode={(loadHandTrackingTrack ? "hand" : "controller")}");
                    __result = CustomSongLoader.LoadSongMapping(song, loadHandTrackingTrack);
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[ChartDiag] Load failed: songId={song.SongID}, file={song.FileName}, requestedMode={(loadHandTrackingTrack ? "hand" : "controller")}: {ex}");
                    throw;
                }
            }
            return true;
        }
    }

    public static class PlaybackDiagnostics
    {
        private static string DescribeSource(AudioSource source)
        {
            if (source == null) return "null";
            try
            {
                string clipName = source.clip != null ? source.clip.name : "null";
                string clipLength = source.clip != null ? $"{source.clip.length:F2}s" : "n/a";
                return $"enabled={source.enabled},active={source.gameObject.activeInHierarchy},playing={source.isPlaying},muted={source.mute},volume={source.volume:F2},time={source.time:F2}s,clip={clipName}({clipLength})";
            }
            catch (Exception ex)
            {
                return $"stateError={ex.GetType().Name}";
            }
        }

        public static void LogSongState(string phase)
        {
            if (!ModMain.ConfigChartDiagnostics) return;
            try
            {
                var data = GameManager.CurrentSongData;
                if (data == null || string.IsNullOrEmpty(data.SongID) || !data.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal)) return;
                var mapping = GameManager.CurrentSong;
                var player = GameManager.I != null ? GameManager.I.musicPlayer : null;
                int keyCount = mapping != null && mapping.Keys != null ? mapping.Keys.Count : -1;
                int bpmCount = mapping != null && mapping.BPMGrid != null ? mapping.BPMGrid.Count : -1;
                MelonLogger.Msg($"[PlaybackDiag] {phase}: songId={data?.SongID ?? "null"}, name={data?.SongName ?? "null"}, file={data?.FileName ?? "null"}, mappingKeys={keyCount}, bpmChanges={bpmCount}, handMode={mapping?.IsHandTrackingMapping}, isPlaying={GameManager.IsPlaying}, songTime={GameManager.SongTime:F3}s, master=[{DescribeSource(player != null ? player.MusicAudioSource : null)}]");
                LogPlayerState(phase, player);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[PlaybackDiag] Could not read song state during {phase}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void LogPlayerState(string phase, Mp3Player player)
        {
            if (!ModMain.ConfigChartDiagnostics || player == null) return;
            try
            {
                var currentSongData = GameManager.CurrentSongData;
                if (currentSongData == null || string.IsNullOrEmpty(currentSongData.SongID) || !currentSongData.SongID.StartsWith("CUSTOM_", StringComparison.Ordinal)) return;
                MelonLogger.Msg($"[PlaybackDiag] {phase}: IsMultiTrack={player.IsMultiTrack}, AllowMultitrack={player.AllowMultitrack}, sourceCount={player.AudioSources?.Count ?? 0}");
                if (player.AudioSources == null) return;
                for (int i = 0; i < player.AudioSources.Count; i++)
                {
                    var source = player.AudioSources[i];
                    MelonLogger.Msg($"[PlaybackDiag] {phase}: source[{i}] ({(Mp3Player.SongTrackNames)i}) [{DescribeSource(source)}]");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[PlaybackDiag] Could not read audio sources during {phase}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "StartSong")]
    public static class Patch_GameManager_StartSong_Diagnostics
    {
        public static void Prefix() => PlaybackDiagnostics.LogSongState("StartSong.begin");
        public static void Postfix() => PlaybackDiagnostics.LogSongState("StartSong.end");
    }

    [HarmonyPatch(typeof(KeyTime), "RaiseSuccessed")]
    public static class Patch_CustomChart_NoteSucceeded
    {
        public static void Prefix(KeyTime __instance, float timeStamp)
        {
            CustomSongLoader.LogChartOutcome(__instance, timeStamp, true);
        }
    }

    [HarmonyPatch(typeof(KeyTime), "RaiseFailed")]
    public static class Patch_CustomChart_NoteFailed
    {
        public static void Prefix(KeyTime __instance, float timeStamp)
        {
            CustomSongLoader.LogChartOutcome(__instance, timeStamp, false);
        }
    }

    [HarmonyPatch(typeof(Mp3Player), "LoadSongSync")]
    public static class Patch_Mp3Player_LoadSongSync
    {
        public static bool Prefix(Mp3Player __instance, SongMappingMetaData data, bool loadSample)
        {
            if (data != null && !string.IsNullOrEmpty(data.SongID) && data.SongID.StartsWith("CUSTOM_"))
            {
                CustomSongLoader.LoadCustomAudio(__instance, data, loadSample);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Mp3Player), "PlayAsync")]
    public static class Patch_Mp3Player_PlayAsync
    {
        public static bool Prefix(Mp3Player __instance, string path, bool playSample)
        {
            if (!string.IsNullOrEmpty(path))
            {
                string folderName = Path.GetFileName(path);
                string customSongDir = Path.Combine(CustomSongLoader.CustomSongsFolder, folderName);
                bool isCustom = Directory.Exists(customSongDir)
                    || path.Contains("CustomSongs")
                    || CustomSongLoader.ContainsSong(CustomSongLoader.CustomAlbumInstance, folderName);

                if (isCustom)
                {
                    CustomSongLoader.PlayCustomAudioSample(__instance, path);
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Mp3Player), "Play")]
    public static class Patch_Mp3Player_Play
    {
        public static void Prefix(Mp3Player __instance)
        {
            PlaybackDiagnostics.LogPlayerState("Mp3Player.Play.begin", __instance);
        }

        public static void Postfix(Mp3Player __instance)
        {
            PlaybackDiagnostics.LogPlayerState("Mp3Player.Play.end", __instance);
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), "GetFallBackLanguage")]
    public static class Patch_LocalizationManager_GetFallBackLanguage
    {
        public static bool Prefix(string langCode, ref CultureInfo __result)
        {
            try
            {
                var mgr = GameFieldAccess.Get<LocalizationManager>(typeof(LocalizationManager), "I");
                if (mgr == null || mgr.AvailableLanguages == null)
                {
                    __result = new CultureInfo("en-US");
                    return false;
                }

                if (string.IsNullOrEmpty(langCode))
                {
                    langCode = "en-US";
                }

                string match = null;
                foreach (var x in mgr.AvailableLanguages)
                {
                    if (!string.IsNullOrEmpty(x) && string.Equals(x, langCode, StringComparison.OrdinalIgnoreCase))
                    {
                        match = x;
                        break;
                    }
                }
                if (match != null)
                {
                    __result = new CultureInfo(match);
                    return false;
                }

                if (mgr.FallbackLanguages != null)
                {
                    foreach (var x in mgr.FallbackLanguages)
                    {
                        if (x != null && !string.IsNullOrEmpty(x.LanguageCode) && string.Equals(x.LanguageCode, langCode, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(x.FallbackLanguageCode))
                            {
                                __result = new CultureInfo(x.FallbackLanguageCode);
                                return false;
                            }
                        }
                    }
                }

                string langPrefix = langCode.Length >= 2 ? langCode.Substring(0, 2) : langCode;
                foreach (var x in mgr.AvailableLanguages)
                {
                    if (!string.IsNullOrEmpty(x) && x.Length >= 2 && string.Equals(x.Substring(0, 2), langPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        match = x;
                        break;
                    }
                }
                if (match != null)
                {
                    __result = new CultureInfo(match);
                    return false;
                }

                __result = new CultureInfo("en-US");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in GetFallBackLanguage prefix: {ex}");
                __result = new CultureInfo("en-US");
                return false;
            }
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), "SetLanguage")]
    public static class Patch_LocalizationManager_SetLanguage
    {
        public static bool Prefix(ref CultureInfo culture)
        {
            try
            {
                if (culture == null) culture = new CultureInfo("en-US");
                var fb = LocalizationManager.GetFallBackLanguage(culture.Name);
                if (fb != null) culture = fb;

                var data = Resources.Load<LocalizationData>("Localization/" + culture.Name);
                if (data == null)
                {
                    data = Resources.Load<LocalizationData>("Localization/en-US");
                }
                if (data != null)
                {
                    Traverse.Create(typeof(LocalizationManager)).Property("CurrentLocalization").SetValue(data);
                    MelonLogger.Msg($"[CustomSongs] Localization safely set to: {culture.Name}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in SetLanguage prefix: {ex}");
            }
            return true;
        }
    }

#if !QUEST_IL2CPP
    [HarmonyPatch(typeof(GuitarHandPoseRecognitionManager), "ResetFingerColors")]
    public static class Patch_GuitarHandPoseRecognitionManager_ResetFingerColors
    {
        public static bool Prefix()
        {
            try
            {
                return InteractionSystem.I != null
                    && InteractionSystem.I.PrimaryHand != null
                    && InteractionSystem.I.PrimaryHand.HandRender != null
                    && InteractionSystem.I.PrimaryHand.HandRender.material != null;
            }
            catch
            {
                return false;
            }
        }
    }
#endif

    // The game's "NoteReachedTime" timeline event does NoteController.Notes[key] without checking.
    // When a note view was already destroyed (e.g. note hit before its sustain starts) it throws
    // KeyNotFoundException inside GameTimeline.SetTime, aborting the remaining timeline events that frame.
    [HarmonyPatch]
    public static class Patch_StringManager_NoteReachedTime
    {
        static MethodBase Target()
        {
            var inner = AccessTools.Inner(typeof(StringManager), "<>c");
            return inner != null ? AccessTools.Method(inner, "<Initialize>b__31_1") : null;
        }

        static bool Prepare() => Target() != null;

        static MethodBase TargetMethod() => Target();

        static bool Prefix(KeyTime __0)
        {
            return __0 != null && NoteController.Notes.ContainsKey(__0);
        }
    }

#if !QUEST_IL2CPP
    [HarmonyPatch(typeof(GuitarHandPoseRecognitionManager), "CheckHandPoseColliderMethod")]
    public static class Patch_GuitarHandPoseRecognitionManager_CheckHandPoseColliderMethod
    {
        public static void Prefix(GuitarHandPoseRecognitionManager __instance, HandGuitarNote closerNote)
        {
            if (closerNote == null) return;
            try
            {
                if (closerNote.Fingers == null || closerNote.Fingers.Count == 0)
                {
                    int tier = closerNote.Tier;
#if QUEST_IL2CPP
                    var fList = new Il2CppSystem.Collections.Generic.List<TierFinger>();
                    fList.Add(tier switch
                    {
                        0 => TierFinger.Index,
                        1 => TierFinger.Middle,
                        2 => TierFinger.Ring,
                        _ => TierFinger.Pinky
                    });
                    closerNote.Fingers = fList;
#else
                    closerNote.Fingers = tier switch
                    {
                        0 => new List<TierFinger> { TierFinger.Index },
                        1 => new List<TierFinger> { TierFinger.Middle },
                        2 => new List<TierFinger> { TierFinger.Ring },
                        _ => new List<TierFinger> { TierFinger.Pinky }
                    };
#endif
                }
            }
            catch { }
        }
    }
#endif



    [HarmonyPatch(typeof(GameManager), "Finish")]
    public static class Patch_GameManager_Finish
    {
        private static float _lastSuppressedLog = 0f;

        public static bool Prefix()
        {
            IntroSkipManager.HidePrompt();
            if (GameManager.SongTime < 2.0f && GameManager.IsPlaying)
            {
                if (Time.unscaledTime - _lastSuppressedLog > 2.0f)
                {
                    _lastSuppressedLog = Time.unscaledTime;
                    MelonLogger.Msg($"[CustomSongs] Suppressed early Finish() at time={GameManager.SongTime:F2}s");
                }
                return false;
            }
            return true;
        }

        public static void Postfix()
        {
            try
            {
                if (GameManager.I != null && GameManager.I.musicPlayer != null && GameManager.I.musicPlayer.MusicAudioSource != null)
                {
                    GameManager.I.musicPlayer.MusicAudioSource.volume = 1f;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GameManager), "SaveLastSongPlayed")]
    public static class Patch_GameManager_SaveLastSongPlayed
    {
        public static bool Prefix()
        {
            try
            {
                if (GameManager.CurrentSongData != null)
                {
                    var asset = GameManager.CurrentSongData.SongAsset;
                    var album = asset?.AlbumData ?? AlbumsManager.SelectedAlbum;
                    if (album != null)
                    {
                        GamePreferences.Save(UnpluggedInfo.LastSongPlayedAlbum, album.AlbumID);
                        if (album.AlbumID == "CustomSongsAlbum")
                        {
                            string customFileName = Path.GetFileName(GameManager.CurrentSongData.FileName ?? asset?.OriginalFileName ?? "");
                            if (!string.IsNullOrWhiteSpace(customFileName))
                            {
                                PlayerPrefs.SetString(CustomSongLoader.LastCustomSongFilePreference, customFileName);
                                PlayerPrefs.Save();
                            }
                        }
                        var songs = album.SongsMetadata;
                        if (songs != null)
                        {
                            int idx = -1;
                            for (int i = 0; i < songs.Count; i++)
                            {
                                if (songs[i] != null && songs[i].SongID == GameManager.CurrentSongData.SongID)
                                {
                                    idx = i;
                                    break;
                                }
                            }
                            if (idx >= 0)
                            {
                                GamePreferences.Save(UnpluggedInfo.LastSongPlayed, idx);
                                MelonLogger.Msg($"[CustomSongs] Saved last played: Album='{album.AlbumID}', Index={idx} ('{GameManager.CurrentSongData.SongName}')");
                            }
                        }
                    }
                    if (CustomSongLoader.CustomAlbumInstance != null)
                    {
                        CustomSongLoader.UpdateAlbumCover(CustomSongLoader.CustomAlbumInstance);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in SaveLastSongPlayed: {ex}");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(GameManager), "StartSong")]
    public static class Patch_GameManager_StartSong
    {
        public static void Postfix()
        {
            try
            {
                if (GameManager.CurrentSongData != null)
                {
                    // Immediately bookmark this song as last played so pausing/quitting always remembers it!
                    try { if (GameManager.I != null) GameManager.I.SaveLastSongPlayed(); } catch { }

                    string song = GameManager.CurrentSongData.SongName;
                    string artist = GameManager.CurrentSongData.BandName;
                    string diff = GameStateManager.I != null ? GameStateManager.I.CurrentDifficulty.ToString() : "Normal";
                    DiscordRpcManager.UpdatePlaying(song, artist, diff);

                    if (GameManager.CurrentSongData.SongID != null && GameManager.CurrentSongData.SongID.StartsWith("CUSTOM_"))
                    {
                        CustomSongLoader.UpdateAlbumCover(CustomSongLoader.CustomAlbumInstance);
                    }

                    IntroSkipManager.OnSongStarted();
                }
            }
            catch (Exception ex)
            {
                if (ModMain.ConfigVerbose) MelonLogger.Error($"[DiscordRPC/Cover] Error in StartSong Postfix: {ex}");
            }
        }
    }

    public static class IntroSkipManager
    {
        public static bool CanSkip = false;
        public static bool HasSkipped = false;
        public static float FirstNoteTime = 0f;
        public static float SkipTargetTime = 0f;
        private static GameObject _promptObj = null;
        private static TextMeshPro _promptText = null;
        private static float _nextInputDiagnosticTime = 0f;
        private static bool _promptPlaced;

        public static void OnSongStarted()
        {
            CanSkip = false;
            HasSkipped = false;
            FirstNoteTime = 0f;
            SkipTargetTime = 0f;
            _promptPlaced = false;
            HidePrompt();

            try
            {
                if (GameManager.CurrentSong == null)
                    return;

                float minTime = float.MaxValue;
#if QUEST_IL2CPP
                if (GameManager.CurrentSong.Keys != null)
                {
                    foreach (var key in GameManager.CurrentSong.Keys)
                    {
                        var controllerGame = key?.TryCast<GuitarControllerGame>();
                        if (controllerGame?.Notes != null)
                            foreach (var note in controllerGame.Notes)
                                if (note != null && note.Time > 0.5f && note.Time < minTime)
                                    minTime = note.Time;
                        var handGame = key?.TryCast<GuitarGame>();
                        if (handGame?.Notes != null)
                            foreach (var note in handGame.Notes)
                                if (note != null && note.Time > 0.5f && note.Time < minTime)
                                    minTime = note.Time;
                        // IL2CPP exposes base KeyTime wrappers: managed 'is' does
                        // not identify native beat lines, which start at 0.5s.
                        bool playable = key != null && (key.TryCast<ControllerGuitarNote>() != null
                            || key.TryCast<HandGuitarNote>() != null);
                        if (playable && key.Time > 0.5f && key.Time < minTime)
                            minTime = key.Time;
                    }
                }
#else
                var allNotes = new List<KeyTime>();
                if (GameManager.CurrentSong.Keys != null)
                {
                    foreach (var key in GameManager.CurrentSong.Keys)
                    {
                        if (key is MiniGame miniGame)
                        {
                            var notes = miniGame.GetNotes();
                            if (notes != null) allNotes.AddRange(notes);
                        }
                        else if (key != null && !(key is BeatLineKey))
                        {
                            allNotes.Add(key);
                        }
                    }
                }

                if (GameManager.I?.TimelineManager?.Timelines != null)
                {
                    try
                    {
                        foreach (var timeline in GameManager.I.TimelineManager.Timelines)
                        {
                            if (timeline?.AllNotesInTimeLine != null)
                                allNotes.AddRange(timeline.AllNotesInTimeLine);
                        }
                    }
                    catch { }
                }

                foreach (var note in allNotes)
                {
                    if (note != null && !(note is BeatLineKey) && note.Time > 0.5f && note.Time < minTime)
                        minTime = note.Time;
                }
#endif

                if (minTime < float.MaxValue)
                {
                    FirstNoteTime = minTime;
                    // Only offer skip if silence/delay before first playable note is >= 5 seconds!
                    if (FirstNoteTime >= 5.0f)
                    {
                        // Jump to 2.5 seconds before the first note for comfortable reaction time
                        SkipTargetTime = Mathf.Max(0.5f, FirstNoteTime - 2.5f);
                        CanSkip = true;
                        MelonLogger.Msg($"[CustomSongs] Intro skip available: First note at {FirstNoteTime:F2}s -> Skip target: {SkipTargetTime:F2}s");
                    }
                    else
                    {
                        MelonLogger.Msg($"[CustomSongs] Intro skip not needed: First note is at {FirstNoteTime:F2}s (< 5.0s)");
                    }
                }
                else
                {
                    MelonLogger.Msg("[CustomSongs] Intro skip: No playable notes found with Time > 0.5s");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error analyzing intro notes: {ex.Message}");
            }
        }

        public static void Update()
        {
            if (!CanSkip || HasSkipped || GameManager.I == null || !GameManager.IsPlaying)
            {
                HidePrompt();
                return;
            }

            float currentTime = GameManager.SongTime;

            // If natural playback reached the skip point, dismiss without skipping
            if (currentTime >= SkipTargetTime - 0.3f)
            {
                CanSkip = false;
                HidePrompt();
                return;
            }

            ShowPrompt();

            if (CheckSkipInput())
            {
                PerformSkip();
            }
        }

        private static bool CheckSkipInput()
        {
            // Keyboard (PC / Desktop Debug) -> Key [3] / Keypad [3]
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                return true;
            }

            // Touch face buttons: A on the right controller, X on the left.
            // Grabbing the pick/guitar must never request an intro skip.
            try
            {
                bool a = OVRInput.Get(OVRInput.RawButton.A, OVRInput.Controller.All);
                bool x = OVRInput.Get(OVRInput.RawButton.X, OVRInput.Controller.All);
                if (ModMain.ConfigVerbose && Time.unscaledTime >= _nextInputDiagnosticTime)
                {
                    _nextInputDiagnosticTime = Time.unscaledTime + 1.0f;
                    MelonLogger.Msg($"[IntroSkip][Input] A={a}, X={x}, target={SkipTargetTime:F2}s, song={GameManager.SongTime:F2}s.");
                }
                return a && x;
            }
            catch { }
            return false;
        }

        public static void PerformSkip()
        {
            if (!CanSkip || HasSkipped || GameManager.I == null || GameManager.I.musicPlayer == null)
                return;

            HasSkipped = true;
            CanSkip = false;
            HidePrompt();

            try
            {
                var mp = GameManager.I.musicPlayer;
                float target = SkipTargetTime;

                // 1. Cancel any pending delayed start coroutine so it cannot overwrite time or reset playback
                try { mp.StopAllCoroutines(); } catch { }

                // 2. Immediately start master audio source if not playing and seek to target
                if (mp.MusicAudioSource != null)
                {
                    if (!mp.MusicAudioSource.isPlaying)
                    {
                        mp.MusicAudioSource.Play();
                    }
                    mp.MusicAudioSource.time = target;
                }

                // 3. Immediately start all multitrack stem audio sources and sync sample clocks
                if (mp.AudioSources != null)
                {
                    foreach (var src in mp.AudioSources)
                    {
                        if (src != null && src.clip != null)
                        {
                            if (!src.isPlaying) src.Play();
                            if (mp.MusicAudioSource != null && mp.MusicAudioSource.clip != null)
                            {
                                src.timeSamples = mp.MusicAudioSource.timeSamples;
                            }
                            else
                            {
                                src.time = target;
                            }
                        }
                    }
                }

                // 4. Update internal player clock and delay counter to target
                mp.CurrentTime = target;
                mp.SongTimeDelayCounter = GameManager.I.StartDelay + target;

                // 5. Update timeline and flush upcoming notes onto the highway
                if (GameManager.I.TimelineManager != null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        GameManager.I.TimelineManager.SetTime(target);
                    }
                }

                try
                {
                    OVRInput.SetControllerVibration(0.4f, 0.4f, OVRInput.Controller.All);
                    MelonCoroutines.Start(StopVibration(0.18f));
                }
                catch { }

                MelonLogger.Msg($"[CustomSongs] >> SKIPPED INTRO! Jumped to {target:F2}s (First note at {FirstNoteTime:F2}s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error performing intro skip: {ex}");
            }
        }

        private static IEnumerator StopVibration(float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.All);
            }
            catch { }
        }

        private static void ShowPrompt()
        {
            try
            {
                if (_promptObj == null)
                {
                    _promptObj = new GameObject("SkipIntroPrompt");
                    UnityEngine.Object.DontDestroyOnLoad(_promptObj);
                    _promptText = _promptObj.AddComponent<TextMeshPro>();
                    var font = Patch_SongMenuItem_SetSong.GameFont;
                    var material = Patch_SongMenuItem_SetSong.GameFontMaterial;
                    if (font == null)
                    {
                        foreach (var gameText in UnityEngine.Object.FindObjectsOfType<TextMeshPro>())
                            if (gameText != null && gameText.font != null && gameText != _promptText)
                            {
                                font = gameText.font;
                                material = gameText.fontSharedMaterial;
                                break;
                            }
                    }
                    if (font != null)
                    {
                        _promptText.font = font;
                        if (material != null) _promptText.fontSharedMaterial = new Material(material);
                    }
                    _promptText.fontSize = 0.8f;
                    _promptText.alignment = TextAlignmentOptions.Center;
                    _promptText.color = Color.white;
                    _promptText.faceColor = Color.white;
                    _promptText.enableWordWrapping = false;
                    _promptText.rectTransform.sizeDelta = new Vector2(0.65f, 0.20f);
                    _promptText.outlineColor = Color.black;
                    _promptText.outlineWidth = 0.26f;
                    try
                    {
                        var mat = _promptText.fontMaterial ?? _promptText.fontSharedMaterial;
                        if (mat != null)
                        {
                            mat.EnableKeyword(ShaderUtilities.Keyword_Outline);
                            mat.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                            mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.26f);
                        }
                    }
                    catch { }
                }

                if (_promptText != null)
                {
                    _promptText.text = "A + X\nSKIP INTRO";
                }

                Camera cam = CameraManager.Main != null ? CameraManager.Main : Camera.main;
                if (cam != null && !_promptPlaced)
                {
                    Transform ct = cam.transform;
                    var pause = PlaterSpaceController.I?.PauseButton;
                    if (pause != null)
                    {
                        var anchor = PlaterSpaceController.I.ButtonsAnchor ?? pause.transform;
                        _promptObj.transform.position = anchor.position + Vector3.up * 0.20f;
                        _promptObj.layer = pause.gameObject.layer;
                    }
                    else
                    {
                        Vector3 forward = Vector3.ProjectOnPlane(ct.forward, Vector3.up).normalized;
                        _promptObj.transform.position = ct.position + forward * 2.0f - Vector3.up * 0.25f;
                    }
                    Vector3 facing = Vector3.ProjectOnPlane(_promptObj.transform.position - ct.position, Vector3.up);
                    _promptObj.transform.rotation = Quaternion.LookRotation(facing.normalized, Vector3.up);
                    _promptPlaced = true;
                }
                if (!_promptObj.activeSelf) _promptObj.SetActive(true);
            }
            catch { }
        }

        public static void HidePrompt()
        {
            if (_promptObj != null && _promptObj.activeSelf)
            {
                _promptObj.SetActive(false);
            }
        }
    }

    public static class VinylCoverHelper
    {
        private static string _lastCoverKey = null;

        public static void UpdateTurntableVinyl(SongMappingMetaData song, bool force = false)
        {
            if (song == null) return;
            try
            {
                string songKey = $"{song.BandName}_{song.SongName}";
                if (!force && _lastCoverKey == songKey)
                {
                    return; // Already up-to-date, avoid redundant material texture sets and log spam
                }
                _lastCoverKey = songKey;

                Texture2D tex = song.Thumbnail;
                if (tex != null)
                {
                    if (StartSpace.I != null)
                    {
                        var r1 = StartSpace.I.StartSpaceLp?.GetComponent<Renderer>();
                        if (r1 != null && r1.material != null)
                        {
                            r1.material.SetTexture("_MainTex", tex);
                        }
                        var r2 = StartSpace.I.StartSpaceAuxLp?.GetComponent<Renderer>();
                        if (r2 != null && r2.material != null)
                        {
                            r2.material.SetTexture("_MainTex", tex);
                        }
                    }
                    if (CustomSongLoader.CustomAlbumInstance != null)
                    {
                        CustomSongLoader.CustomAlbumInstance.VinylTexture = tex;
                    }
                    MelonLogger.Msg($"[CustomSongs] Vinyl cover updated to: {song.BandName} - {song.SongName} ({tex.width}x{tex.height})");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error updating vinyl cover: {ex.Message}");
            }
        }
    }

    // Disable SongMenu's native Arrow key checks so that our mod has full, single-step and hold-accelerated control without doubling.
    [HarmonyPatch(typeof(SongMenu), "Update")]
    public static class Patch_SongMenu_Update
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Call && inst.operand is MethodInfo mi && mi.Name == "GetKeyDown")
                {
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                }
                else
                {
                    yield return inst;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StartSpace), "Start")]
    public static class Patch_StartSpace_Start
    {
        private static bool _eventSubscribed = false;

        public static void Postfix(StartSpace __instance)
        {
            try
            {
                DiscordRpcManager.UpdateMenu();

                if (!_eventSubscribed)
                {
                    _eventSubscribed = true;
#if QUEST_IL2CPP
                    SongMenu.add_SelectedSongChanged(new Action<SongMappingMetaData>((song) =>
                    {
                        if (song != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                        {
                            VinylCoverHelper.UpdateTurntableVinyl(song);
                        }
                    }));
#else
                    SongMenu.SelectedSongChanged += (song) =>
                    {
                        if (song != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                        {
                            VinylCoverHelper.UpdateTurntableVinyl(song);
                        }
                    };
#endif
                }

                // Saved custom-song restoration is centralized in
                // ProcessDeferredAlbumRegistration. Restoring again from Start
                // queued a second selection after the album was already being
                // created, which could reset the selected card and preview.
                CustomSongLoader.UpdateAlbumCover(CustomSongLoader.CustomAlbumInstance);

            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(DLCControllerNew), "OnEnable")]
    public static class Patch_DLCControllerNew_OnEnable
    {
        public static Exception Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                return null; // Suppress harmless NRE on returning to menu from custom song
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(MiniAlbumItem), "SetAlbumData")]
    public static class Patch_MiniAlbumItem_SetAlbumData
    {
        public static void Postfix(MiniAlbumItem __instance, AlbumData albumData)
        {
            try
            {
                if (albumData != null && albumData.AlbumID == "CustomSongsAlbum" && CustomSongLoader.CustomAlbumInstance != null)
                {
                    if (CustomSongLoader.CustomAlbumInstance.Cover != null)
                    {
                        var coverImg = GameFieldAccess.Get<UnityEngine.UI.RawImage>(__instance, "coverImage");
                        if (coverImg != null)
                        {
                            coverImg.texture = CustomSongLoader.CustomAlbumInstance.Cover;
                        }
                        else
                        {
                            var raw = __instance.GetComponentInChildren<UnityEngine.UI.RawImage>();
                            if (raw != null) raw.texture = CustomSongLoader.CustomAlbumInstance.Cover;
                        }
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SongMenuItem), "SetSong")]
    public static class Patch_SongMenuItem_SetSong
    {
        private static readonly Dictionary<int, string> PresentedSongs = new Dictionary<int, string>();
        private static readonly Dictionary<int, SongMenuItem> TrackedItems = new Dictionary<int, SongMenuItem>();
        public static TMP_FontAsset GameFont { get; private set; }
        public static Material GameFontMaterial { get; private set; }
        public static bool LabelsVisible { get; private set; }

        public static void SetLabelsVisible(bool visible)
        {
            LabelsVisible = visible;
            var menu = StartSpace.I?.songMenu;
            if (menu == null) return;
            TrackedItems.Clear();
            foreach (var item in menu.GetComponentsInChildren<SongMenuItem>(true))
            {
                TrackedItems[item.GetInstanceID()] = item;
                RestoreBadgeVisibility(item);
            }
        }

        public static void SyncTitleVisibility()
        {
            if (!LabelsVisible) return;
            foreach (var item in TrackedItems.Values)
                if (item != null) RestoreBadgeVisibility(item);
        }

        public static void Postfix(SongMenuItem __instance)
        {
            RefreshPresentation(__instance, true);
        }

        public static void RefreshPresentation(SongMenuItem item, bool force = false)
        {
            try
            {
                if (item?.SongData != null)
                {
                    int id = item.GetInstanceID();
                    TrackedItems[id] = item;
                    string songId = item.SongData.SongID ?? string.Empty;
                    if (!force && PresentedSongs.TryGetValue(id, out string previous) && previous == songId)
                    {
                        RestoreBadgeVisibility(item);
                        return;
                    }
                    CustomSongLoader.QueueSongCoverLoad(item, item.SongData);

                    SetTrackLabel(item, CustomSongLoader.GetTrackDisplayLabel(item.SongData));
                    PresentedSongs[id] = songId;
                    if (ModMain.ConfigVerbose)
                        MelonLogger.Msg($"[MenuDiag] Presented card={id}, song='{songId}', label='{CustomSongLoader.GetTrackDisplayLabel(item.SongData)}'.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomSongs] Could not show the custom song track label: {ex.Message}");
            }
            ApplyOutlines(item);
        }

        private static void SetTrackLabel(SongMenuItem item, string label)
        {
            var title = item.SongName;
            if (title == null) return;
            GameFont = title.font;
            GameFontMaterial = title.fontSharedMaterial;
            var existing = title.transform.Find("USMTrackLabel");
            if (string.IsNullOrEmpty(label))
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            TextMeshPro badge;
            if (existing != null)
                badge = existing.GetComponent<TextMeshPro>();
            else
            {
                var badgeObject = new GameObject("USMTrackLabel");
                badgeObject.layer = title.gameObject.layer;
                badgeObject.transform.SetParent(title.transform, false);
                badge = badgeObject.AddComponent<TextMeshPro>();
            }
            if (badge == null) return;

            title.ForceMeshUpdate(true);
            badge.gameObject.SetActive(LabelsVisible);
            badge.font = title.font;
            // The title's material/color may still be hidden by its native
            // entry animation. Give the badge its own opaque material.
            if (badge.fontSharedMaterial == null || badge.fontSharedMaterial == title.fontSharedMaterial)
                badge.fontSharedMaterial = new Material(title.fontSharedMaterial);
            badge.fontSize = title.fontSize * 0.65f;
            badge.enableAutoSizing = false;
            badge.enableWordWrapping = false;
            badge.overflowMode = TextOverflowModes.Overflow;
            badge.alignment = title.alignment;
            badge.color = Color.white;
            badge.faceColor = Color.white;
            badge.alpha = 1f;
            badge.richText = true;
            badge.rectTransform.sizeDelta = title.rectTransform.rect.size;
            badge.text = label;
            badge.ForceMeshUpdate(true);
            badge.transform.localPosition = new Vector3(0f, GetTrackLabelVerticalOffset(title.textBounds, badge.textBounds), 0f);
            var titleRenderer = title.GetComponent<MeshRenderer>();
            var badgeRenderer = badge.GetComponent<MeshRenderer>();
            if (titleRenderer != null && badgeRenderer != null)
            {
                badgeRenderer.sortingLayerID = titleRenderer.sortingLayerID;
                badgeRenderer.sortingOrder = titleRenderer.sortingOrder + 1;
                badgeRenderer.enabled = LabelsVisible;
            }
            ApplyOutline(badge);
            MelonLogger.Msg($"[MenuDiag] Track label ready: song='{item.SongData.SongID}', label='{label}', alpha={badge.alpha}, font='{badge.font?.name}', boundsHeight={badge.textBounds.size.y:F4}.");
        }

        private static void RestoreBadgeVisibility(SongMenuItem item)
        {
            if (item == null) return;
            var child = item.SongName?.transform.Find("USMTrackLabel");
            if (child == null) return;
            var badge = child.GetComponent<TextMeshPro>();
            if (badge == null || string.IsNullOrEmpty(badge.text)) return;
            float titleAlpha = item.SongName.alpha;
            if (!Mathf.Approximately(badge.alpha, titleAlpha)) badge.alpha = titleAlpha;
            if (Mathf.Abs(((Color)badge.faceColor).a - titleAlpha) > 1f / 255f)
                badge.faceColor = new Color(1f, 1f, 1f, titleAlpha);
            if (child.gameObject.activeSelf != LabelsVisible) child.gameObject.SetActive(LabelsVisible);
            var renderer = badge.GetComponent<MeshRenderer>();
            var titleRenderer = item.SongName.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.enabled = LabelsVisible && titleAlpha > 0.001f
                && titleRenderer != null && titleRenderer.enabled;
        }

        private static float GetTrackLabelVerticalOffset(Bounds titleBounds, Bounds labelBounds)
        {
            // Place the badge's top beneath the title's actual bottom, including
            // wrapped titles, without shifting the artist or song name.
            float gap = Math.Max(0.005f, titleBounds.size.y * 0.12f);
            return titleBounds.min.y - labelBounds.max.y - gap;
        }

        public static void ApplyOutlines(SongMenuItem item)
        {
            if (item == null) return;
            try
            {
                ApplyOutline(item.SongName);
                ApplyOutline(item.BandName);
                ApplyOutline(item.FileName);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error applying text outline: {ex}");
            }
        }

        private static void ApplyOutline(TextMeshPro tmp)
        {
            if (tmp == null) return;
            try
            {
                var mat = tmp.fontMaterial ?? tmp.fontSharedMaterial;
                if (mat != null)
                {
                    mat.EnableKeyword(ShaderUtilities.Keyword_Outline);
                    mat.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.22f);
                }
                tmp.outlineColor = Color.black;
                tmp.outlineWidth = 0.22f;
                tmp.UpdateMeshPadding();
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SongMenu), "EnterMenu")]
    public static class Patch_SongMenu_EnterMenuLabels
    {
        public static void Prefix()
        {
            // The badge travels and fades with its native title during entry.
            Patch_SongMenuItem_SetSong.SetLabelsVisible(true);
        }

        public static void Postfix(SongMenu __instance)
        {
            try
            {
                if (__instance == null || AlbumsManager.SelectedAlbum?.AlbumID != "CustomSongsAlbum")
                    return;
                if (CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata == null ||
                    CustomSongLoader.CustomAlbumInstance.builtInSongsMetadata.Count == 0)
                    return;

                // The native options panel hides on album/menu changes and only
                // shows difficulty after SelectedSongHasStopped. EnterMenu sets
                // ForceUpdateIndex, but Update clears it before assigning the
                // unchanged selection in multi-song albums. Schedule the game's
                // own settle notification even when the saved song stays centered.
                // It cancels the previous timer, so moving a card still postpones
                // the notification until the newly selected song has settled.
                __instance.CheckIfCarouselHasStoped();
                if (ModMain.ConfigVerbose)
                    MelonLogger.Msg("[MenuDiag] Scheduled native selection-settled notification on custom menu entry; difficulty and venue selectors refresh without rotating the disc.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MenuDiag] Could not refresh options on custom menu entry: {ex.Message}");
            }
        }
    }
    [HarmonyPatch(typeof(SongMenu), "ExitMenu")]
    public static class Patch_SongMenu_ExitMenuLabels
    {
        public static void Prefix()
        {
            Patch_SongMenuItem_SetSong.SetLabelsVisible(false);
        }
    }

    [HarmonyPatch(typeof(SongMenuItem), "Start")]
    public static class Patch_SongMenuItem_Start
    {
        public static void Postfix(SongMenuItem __instance)
        {
            Patch_SongMenuItem_SetSong.RefreshPresentation(__instance, true);
        }
    }

    // Native IL2CPP may inline SetSong when recycling a carousel card. Offset
    // updates still run for every displayed card; refresh only when its ID changes.
    [HarmonyPatch(typeof(SongMenuItem), "SetOffSet")]
    public static class Patch_SongMenuItem_SetOffSet
    {
        public static void Postfix(SongMenuItem __instance)
        {
            Patch_SongMenuItem_SetSong.RefreshPresentation(__instance);
        }
    }

}
