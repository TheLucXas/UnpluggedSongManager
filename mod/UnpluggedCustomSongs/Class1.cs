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
using System.Xml;
using System.Linq;
using UnityEngine;
using HarmonyLib;
using MelonLoader;
using Unplugged.Model;

[assembly: MelonInfo(typeof(UnpluggedCustomSongs.ModMain), "Unplugged Custom Songs", "1.1.0", "Luc & Antigravity")]
[assembly: MelonGame("Anotherway", "Unplugged")]

namespace UnpluggedCustomSongs
{
    public static class DiscordRpcManager
    {
        private static NamedPipeClientStream pipe;
        private static bool isConnected = false;
        private static readonly object lockObj = new object();

        public static void Init()
        {
            if (!ModMain.ConfigDiscordRPC) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Connect());
        }

        public static void EnsureConnected()
        {
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
        public static MelonPreferences_Entry<string> PrefDiscordClientId;
        public static MelonPreferences_Entry<bool> PrefDebugMode;

        public static bool ConfigDiscordRPC => PrefDiscordRPC?.Value ?? true;
        public static bool ConfigVerbose => PrefVerbose?.Value ?? false;
        public static string ConfigDiscordClientId => !string.IsNullOrEmpty(PrefDiscordClientId?.Value) ? PrefDiscordClientId.Value : "1441987286022619299";

        public static bool DebugMode = false;
        public static bool AutoPlay = false;
        public static float yaw = 0f;
        public static float pitch = 0f;
        private static bool initializedAngles = false;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("=== Unplugged Custom Songs Mod Loaded! ===");
            PrefsCategory = MelonPreferences.CreateCategory("UnpluggedCustomSongs", "Unplugged Custom Songs");
            PrefDiscordRPC = PrefsCategory.CreateEntry("EnableDiscordRPC", true, "Enable Discord Rich Presence");
            PrefVerbose = PrefsCategory.CreateEntry("VerboseLogging", false, "Enable Verbose Debug Logging");
            PrefDiscordClientId = PrefsCategory.CreateEntry("DiscordClientId", "1441987286022619299", "Discord Application Client ID");
            PrefDebugMode = PrefsCategory.CreateEntry("EnableDebugMode", false, "Enable Desktop WASD/Mouse Look & Testing Controls");

            DebugMode = PrefDebugMode?.Value ?? false;
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

            Application.logMessageReceived += (condition, stackTrace, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error)
                {
                    LoggerInstance.Error($"[UNITY_{type}] {condition}\nSTACK TRACE:\n{stackTrace}");
                }
            };

            CustomSongLoader.Init();
        }
        // ===================== DESKTOP TEST CONTROLS =====================
        public static bool ShowButtonPanel = false; // Closed by default
        public static bool ShowMarkers = false;     // Off by default
        public static float ForcedGuitarVolume = -1f; // < 0 = no override
        private const float StrumWindow = 0.25f;      // seconds, manual strum hit window

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
            int prevCount = CustomSongLoader.CustomAlbumInstance.SongFiles != null ? CustomSongLoader.CustomAlbumInstance.SongFiles.Count : 0;
            MelonLogger.Msg("[CustomSongs] Refreshing custom songs in real time...");
            CustomSongLoader.PopulateCustomSongs(CustomSongLoader.CustomAlbumInstance);
            int newCount = CustomSongLoader.CustomAlbumInstance.SongFiles != null ? CustomSongLoader.CustomAlbumInstance.SongFiles.Count : 0;

            if (newCount != prevCount && StartSpace.I != null && StartSpace.I.gameObject.activeInHierarchy && StartSpace.I.songMenu != null)
            {
                if (AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                {
                    StartSpace.I.songMenu.ChangeAlbum(CustomSongLoader.CustomAlbumInstance.SongsMetadata);
                    MelonLogger.Msg($"[CustomSongs] Turntable updated with {newCount} songs in real time!");
                }
            }
        }

        public override void OnUpdate()
        {
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

            // VR Thumbstick & Keyboard song carousel navigation (with hold-acceleration)
            HandleSongMenuNavigation();

            if (Input.GetKeyDown(KeyCode.F1))
            {
                DebugMode = !DebugMode;
                LoggerInstance.Msg($"[CustomSongs] Debug Mode is now {(DebugMode ? "ENABLED" : "DISABLED")}");
            }

            if (!DebugMode) return;

            HandleCameraAndMovement();
            RefreshClickTargets();
            HandleActionKeybinds();
            HandleGuitarPlaying();
        }

        public override void OnLateUpdate()
        {
            if (!DebugMode) return;

            // Persistent guitar volume override (key [3]) - applied after the game's own Update
            if (ForcedGuitarVolume >= 0f && GameManager.IsPlaying)
            {
                var guitarMgr = UnityEngine.Object.FindObjectOfType<GuitarGameManager>();
                if (guitarMgr != null)
                {
                    guitarMgr.InstrumentVolumeTarget = ForcedGuitarVolume;
                    guitarMgr.InstrumentVolumeSmooth = ForcedGuitarVolume;
                }
                if (GameManager.I != null && GameManager.I.musicPlayer != null)
                {
                    GameManager.I.musicPlayer.SetTrackVolume(Mp3Player.SongTrackNames.Guitar, ForcedGuitarVolume);
                }
            }
        }

        public override void OnApplicationQuit()
        {
            DiscordRpcManager.Shutdown();
        }

        private static int _navHoldDir = 0;
        private static float _navHoldStartTime = 0f;
        private static float _navNextStepTime = 0f;

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

            // Check if there are at least 2 songs to navigate between
            try
            {
                var songList = Traverse.Create(sm).Field("songList").GetValue<List<SongMappingMetaData>>();
                if (songList == null || songList.Count <= 1)
                {
                    _navHoldDir = 0;
                    return;
                }
            }
            catch
            {
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
                    Traverse.Create(sm).Method("Next_Pressed").GetValue();
                }
                else
                {
                    Traverse.Create(sm).Method("Prev_Pressed").GetValue();
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
                    var trav = Traverse.Create(StartSpace.I.songMenu);
                    var list = trav.Field("songList").GetValue<List<SongMappingMetaData>>();
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
            if (!DebugMode) return;

            Event e = Event.current;

            // ---- HUD help ----
            GUI.color = Color.white;
            string vol = ForcedGuitarVolume >= 0f ? $"<color=yellow>FORZADO {ForcedGuitarVolume:P0}</color>" : "normal";
            string currentSongText = "";
            SongMappingMetaData sel = GetSafeSelectedSong();
            if (sel != null)
            {
                currentSongText = $" | Disco: <color=cyan><b>{sel.BandName} - {sel.SongName}</b></color>";
            }

            GUI.Label(new Rect(15, 12, Screen.width - 30, 22), "<b>[WASD/QE]</b> Moverse | <b>[Click Der + Mouse]</b> Mirar | <b>[Click Izq]</b> Click botón | <b>[Z/X o ←/→]</b> Cambiar canción" + currentSongText);
            GUI.Label(new Rect(15, 34, Screen.width - 30, 22), $"<b>[1 o Enter]</b> Iniciar canción / Agarrar púa | <b>[2]</b> AutoPlay: {(AutoPlay ? "<color=green>ACTIVO</color>" : "<color=yellow>MANUAL</color>")} | <b>[Espacio]</b> Rasguear manual | <b>[3]</b> Vol: {vol} | <b>[C]</b> Álbum custom | <b>[Tab]</b> Botones");

            // ---- Button list panel (opened with Tab) ----
            panelRect = new Rect(0, 0, 0, 0);
            if (ShowButtonPanel)
            {
                var visible = targets.Where(t => t.Button != null && t.Distance < 6f).OrderBy(t => t.Distance).Take(20).ToList();
                float w = 260f;
                float h = Mathf.Min(40f + visible.Count * 26f, Screen.height - 110f);
                panelRect = new Rect(Screen.width - w - 10f, 70f, w, h);
                GUI.Box(panelRect, "Botones detectados (click)");
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
                    }
                    Traverse.Create(StartSpace.I).Method("StartButton_Pressed").GetValue();
                }
            }

            // [2]: Toggle AutoPlay Bot
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                AutoPlay = !AutoPlay;
                LoggerInstance.Msg($"[CustomSongs] Key [2]: AutoPlay Bot is now {(AutoPlay ? "ENABLED" : "DISABLED (Manual Strumming)")}");
            }

            // [3]: Persistent guitar volume override: normal -> 10% -> normal
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                ForcedGuitarVolume = ForcedGuitarVolume < 0f ? 0.1f : -1f;
                if (ForcedGuitarVolume < 0f)
                {
                    var guitarMgr = UnityEngine.Object.FindObjectOfType<GuitarGameManager>();
                    if (guitarMgr != null) { guitarMgr.InstrumentVolumeTarget = 1f; guitarMgr.InstrumentVolumeSmooth = 1f; }
                }
                LoggerInstance.Msg($"[CustomSongs] Key [3]: Guitar volume override = {(ForcedGuitarVolume < 0f ? "OFF (game controls volume)" : ForcedGuitarVolume.ToString("F2"))}");
            }

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
        public static string CustomSongsFolder => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CustomSongs");
        public static DLCAlbumData CustomAlbumInstance;

        private static FileSystemWatcher watcher;

        public static void Init()
        {
            if (!Directory.Exists(CustomSongsFolder))
            {
                Directory.CreateDirectory(CustomSongsFolder);
            }
            StartWatcher();
        }

        public static void StartWatcher()
        {
            if (watcher != null) return;
            try
            {
                if (!Directory.Exists(CustomSongsFolder)) Directory.CreateDirectory(CustomSongsFolder);
                watcher = new FileSystemWatcher(CustomSongsFolder);
                watcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
                watcher.IncludeSubdirectories = true;
                watcher.Created += (s, e) => ModMain.ScheduleReload();
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

                if (manager.DlcAlbums != null && !manager.DlcAlbums.Contains(CustomAlbumInstance))
                {
                    manager.DlcAlbums.Add(CustomAlbumInstance);
                    MelonLogger.Msg($"[CustomSongs] Added Custom Songs Album to DLC shelf with {CustomAlbumInstance.SongFiles.Count} song(s)!");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error registering custom album: {ex}");
            }
        }

        public static void PopulateCustomSongs(AlbumData album)
        {
            album.SongFiles = new List<SongAsset>();
            album.builtInSongsMetadata = new List<SongMappingMetaData>();

            if (!Directory.Exists(CustomSongsFolder)) return;

            string[] subdirs = Directory.GetDirectories(CustomSongsFolder);
            foreach (var dir in subdirs)
            {
                try
                {
                    string folderName = Path.GetFileName(dir);
                    string songName = folderName;
                    string artistName = "Custom Artist";
                    float initialDelay = 4f;

                    string iniPath = Path.Combine(dir, "song.ini");
                    if (File.Exists(iniPath))
                    {
                        foreach (var line in File.ReadAllLines(iniPath))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("="))
                                songName = trimmed.Split(new char[] { '=' }, 2)[1].Trim();
                            if (trimmed.StartsWith("artist", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("="))
                                artistName = trimmed.Split(new char[] { '=' }, 2)[1].Trim();
                        }
                    }

                    string dataPath = Path.Combine(dir, "data.xml");
                    if (File.Exists(dataPath))
                    {
                        try
                        {
                            var xmlDoc = new XmlDocument();
                            xmlDoc.Load(dataPath);
                            var sName = xmlDoc.GetElementsByTagName("SongName");
                            if (sName.Count > 0 && !string.IsNullOrEmpty(sName[0].InnerText))
                                songName = sName[0].InnerText;
                            var bName = xmlDoc.GetElementsByTagName("BandName");
                            if (bName.Count > 0 && !string.IsNullOrEmpty(bName[0].InnerText))
                                artistName = bName[0].InnerText;
                            var delay = xmlDoc.GetElementsByTagName("InitialDelay");
                            if (delay.Count > 0 && float.TryParse(delay[0].InnerText, out float d))
                                initialDelay = d;
                        }
                        catch { }
                    }

                    // Cover: Clone Hero charts use album.png / album.jpg
                    Texture2D coverTex = null;
                    string[] coverNames = { "album.png", "album.jpg", "album.jpeg", "cover.png", "cover.jpg", "cover.jpeg" };
                    string coverPath = coverNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                    if (coverPath != null)
                    {
                        coverTex = new Texture2D(2, 2);
                        if (ImageConversion.LoadImage(coverTex, File.ReadAllBytes(coverPath)))
                            MelonLogger.Msg($"[CustomSongs] Cover loaded: {Path.GetFileName(coverPath)} ({coverTex.width}x{coverTex.height})");
                        else
                            coverTex = null;
                    }

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

                    bool isMulti = File.Exists(Path.Combine(dir, "song-guitar.mp3"));

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
                        Thumbnail = coverTex ?? album.Cover
                    };
                    album.builtInSongsMetadata.Add(meta);

                    MelonLogger.Msg($"[CustomSongs] Found song: {artistName} - {songName} (MultiTrack={isMulti})");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[CustomSongs] Error reading song folder {dir}: {ex}");
                }
            }

            UpdateAlbumCover(album as DLCAlbumData ?? CustomAlbumInstance);
        }

        public static void UpdateAlbumCover(DLCAlbumData album)
        {
            try
            {
                if (album == null) return;
                Texture2D chosenCover = null;

                int lastSongIndex = GamePreferences.Load(UnpluggedInfo.LastSongPlayed, 0);
                if (album.builtInSongsMetadata != null && album.builtInSongsMetadata.Count > 0)
                {
                    if (lastSongIndex >= 0 && lastSongIndex < album.builtInSongsMetadata.Count)
                    {
                        var lastSong = album.builtInSongsMetadata[lastSongIndex];
                        if (lastSong != null && lastSong.Thumbnail != null)
                        {
                            chosenCover = lastSong.Thumbnail;
                        }
                    }
                    if (chosenCover == null)
                    {
                        var firstWithCover = album.builtInSongsMetadata.FirstOrDefault(s => s.Thumbnail != null);
                        if (firstWithCover != null) chosenCover = firstWithCover.Thumbnail;
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
            string folder = Path.Combine(CustomSongsFolder, song.FileName);
            string diff = SongMapping.GetSongDataPathByDificulty(GameStateManager.I.CurrentDifficulty);
            string xmlPath = Path.Combine(folder, diff);
            if (!File.Exists(xmlPath)) xmlPath = Path.Combine(folder, "songdata-Normal.xml");
            if (!File.Exists(xmlPath)) xmlPath = Path.Combine(folder, "songdata-Hard.xml");
            if (!File.Exists(xmlPath)) xmlPath = Path.Combine(folder, "songdata-Easy.xml");

            MelonLogger.Msg($"[CustomSongs] Loading chart XML: {xmlPath}");
            using var stream = File.OpenRead(xmlPath);
            var serializer = new DataContractSerializer(typeof(SongMapping));
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment });
            reader.Read();
            var mapping = (SongMapping)serializer.ReadObject(reader, false);
            mapping.IsHandTrackingMapping = isHandTracking;

                        // DUAL-TRACK GUARANTEE: Ensure GuitarControllerGame exists for controllers / PC desktop!
            if (mapping.Keys != null)
            {
                bool hasHand = mapping.Keys.Any(k => k is GuitarGame);
                bool hasController = mapping.Keys.Any(k => k is GuitarControllerGame);

                if (hasHand && !hasController)
                {
                    var handGame = mapping.Keys.OfType<GuitarGame>().FirstOrDefault();
                    if (handGame != null)
                    {
                        var ctrlGame = new GuitarControllerGame
                        {
                            Duration = handGame.Duration,
                            Time = handGame.Time,
                            PrewarmTime = handGame.PrewarmTime,
                            Notes = new List<ControllerGuitarNote>()
                        };
                        var notes = handGame.GetNotes();
                        if (notes != null)
                        {
                            foreach (var k in notes)
                            {
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

                                    ctrlGame.Notes.Add(new ControllerGuitarNote
                                    {
                                        Time = n.Time,
                                        Duration = n.Duration,
                                        Tier = n.Tier,
                                        CursableProbability = n.CursableProbability,
                                        IsBlind = n.IsBlind,
                                        Buttons = new List<bool> { hasTrigger, hasGrip },
                                        ChordRotation = 0,
                                        Intensity = 0
                                    });
                                }
                            }
                        }
                        mapping.Keys.Add(ctrlGame);
                        MelonLogger.Msg($"[CustomSongs] Converted {ctrlGame.Notes.Count} notes to GuitarControllerGame!");
                    }
                }
            }

            return mapping;
        }

        public static void LoadCustomAudio(Mp3Player player, SongMappingMetaData data, bool loadSample)
        {
            string folder = Path.Combine(CustomSongsFolder, data.FileName);
            bool isMulti = data.IsMultiTrack && player.AllowMultitrack;

            // CRITICAL: Set IsMultiTrack property via Traverse
            Traverse.Create(player).Property("IsMultiTrack").SetValue(isMulti);

            MelonLogger.Msg($"[CustomSongs] LoadCustomAudio: '{data.SongName}', IsMultiTrack set to: {isMulti}");

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
                    masterSrc = player.AudioSources.FirstOrDefault(s => s != null && s.clip != null);
                }

                player.MusicAudioSource = masterSrc;
                MelonLogger.Msg($"[CustomSongs] MultiTrack stems loaded: Guitar={hasGuitar}, Bass={hasBass}, Drums={hasDrums}, Vocals={hasVocals}, Other={hasOther}. MasterSyncSource={masterSrc?.clip?.name}");
            }
            else
            {
                string masterPath = Path.Combine(folder, loadSample ? "sample.mp3" : "song.mp3");
                if (!File.Exists(masterPath)) masterPath = Path.Combine(folder, "song.mp3");
                if (File.Exists(masterPath))
                {
                    AudioClip clip = LoadClip(masterPath);
                    if (clip != null)
                    {
                        clip.name = "song";
                        player.MusicAudioSource.clip = clip;
                        player.MusicAudioSource.volume = 1f;
                        player.MusicAudioSource.spatialBlend = 0f;
                        player.MusicAudioSource.spatialize = false;
                        player.MusicAudioSource.mute = false;
                        player.MusicAudioSource.enabled = true;
                        if (musicMixerGroup != null) player.MusicAudioSource.outputAudioMixerGroup = musicMixerGroup;
                        MelonLogger.Msg($"[CustomSongs] SingleTrack master audio clip loaded: {masterPath}");
                    }
                }
            }
        }

        public static void PlayCustomAudioSample(Mp3Player player, string path)
        {
            try
            {
                string folderName = Path.GetFileName(path);
                string folder = Path.Combine(CustomSongsFolder, folderName);
                if (!Directory.Exists(folder))
                {
                    if (Directory.Exists(CustomSongsFolder))
                    {
                        foreach (var d in Directory.GetDirectories(CustomSongsFolder))
                        {
                            string dName = Path.GetFileName(d);
                            if (path.Contains(dName) || dName.Contains(folderName))
                            {
                                folder = d;
                                folderName = dName;
                                break;
                            }
                        }
                    }
                }
                if (!Directory.Exists(folder))
                {
                    MelonLogger.Warning($"[CustomSongs] Custom song folder not found for sample: {path}");
                    return;
                }

                string sample = Path.Combine(folder, "sample.mp3");
                if (!File.Exists(sample)) sample = Path.Combine(folder, "song.mp3");
                if (File.Exists(sample))
                {
                    AudioClip clip = LoadClip(sample);
                    if (clip != null && player.MusicAudioSource != null)
                    {
                        clip.name = "sample_" + folderName;
                        player.MusicAudioSource.clip = clip;
                        player.MusicAudioSource.loop = true;
                        player.MusicAudioSource.volume = 1f;
                        player.MusicAudioSource.Play();
                        MelonLogger.Msg($"[CustomSongs] Turntable audio preview started for '{folderName}' ({sample})");

                        // Live update Turntable Vinyl Texture to this song's cover!
                        try
                        {
                            Texture2D songCover = null;
                            if (CustomAlbumInstance != null && CustomAlbumInstance.builtInSongsMetadata != null)
                            {
                                var foundMeta = CustomAlbumInstance.builtInSongsMetadata.FirstOrDefault(s => s.FileName == folderName);
                                if (foundMeta != null && foundMeta.Thumbnail != null)
                                {
                                    songCover = foundMeta.Thumbnail;
                                }
                            }
                            if (songCover == null)
                            {
                                string[] coverNames = new string[] { "cover.png", "album.jpg", "album.png", "cover.jpg" };
                                foreach (var cn in coverNames)
                                {
                                    string cp = Path.Combine(folder, cn);
                                    if (File.Exists(cp))
                                    {
                                        songCover = new Texture2D(2, 2);
                                        if (ImageConversion.LoadImage(songCover, File.ReadAllBytes(cp)))
                                        {
                                            songCover.name = folderName;
                                            break;
                                        }
                                        songCover = null;
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
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error in PlayCustomAudioSample: {ex}");
            }
        }

        private static bool LoadTrack(Mp3Player player, string folder, string fileName, Mp3Player.SongTrackNames track)
        {
            string p = Path.Combine(folder, fileName);
            if (File.Exists(p))
            {
                AudioClip clip = LoadClip(p);
                if (clip != null)
                {
                    clip.name = Path.GetFileNameWithoutExtension(fileName);
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
            string url = "file:///" + filePath.Replace("\\", "/");
            using var www = new WWW(url);
            int timeout = 0;
            while (!www.isDone && timeout < 500)
            {
                System.Threading.Thread.Sleep(10);
                timeout++;
            }
            return www.GetAudioClip(false, false, AudioType.MPEG);
        }
    }

    [HarmonyPatch(typeof(AlbumsManager), "Awake")]
    public static class Patch_AlbumsManager_Awake
    {
        public static void Prefix(AlbumsManager __instance)
        {
            try
            {
                CustomSongLoader.RegisterCustomAlbum(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[CustomSongs] Error registering custom album in Awake Prefix: {ex}");
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

    // Fix for 2-song carousel virtualization: when an album has exactly 2 songs, instantiate both!
    [HarmonyPatch(typeof(SongMenu), "generateItems")]
    public static class Patch_SongMenu_generateItems
    {
        public static bool Prefix(SongMenu __instance)
        {
            var trav = Traverse.Create(__instance);
            var songList = trav.Field("songList").GetValue<List<SongMappingMetaData>>();
            if (songList != null && songList.Count == 2)
            {
                trav.Field("smoothVirtualizedOffset").SetValue(0f);
                trav.Field("Offset").SetValue(0.001f);
                GameProgress.Load();

                var virtualizedItemList = trav.Field("virtualizedItemList").GetValue<List<SongMenuItem>>();
                if (virtualizedItemList == null)
                {
                    virtualizedItemList = new List<SongMenuItem>();
                    trav.Field("virtualizedItemList").SetValue(virtualizedItemList);
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

    [HarmonyPatch(typeof(SongMapping), "Load", new Type[] { typeof(SongMappingMetaData), typeof(bool) })]
    public static class Patch_SongMapping_Load
    {
        public static bool Prefix(SongMappingMetaData song, bool loadHandTrackingTrack, ref SongMapping __result)
        {
            if (song != null && !string.IsNullOrEmpty(song.SongID) && song.SongID.StartsWith("CUSTOM_"))
            {
                __result = CustomSongLoader.LoadSongMapping(song, loadHandTrackingTrack);
                return false;
            }
            return true;
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
                    || (AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                    || (CustomSongLoader.CustomAlbumInstance != null && CustomSongLoader.CustomAlbumInstance.SongFiles.Any(s => s.OriginalFileName == folderName || s.Path == folderName));

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
        public static void Postfix(Mp3Player __instance)
        {
            MelonLogger.Msg($"[CustomSongs] Mp3Player.Play invoked! IsMultiTrack={__instance.IsMultiTrack}");
            for (int i = 0; i < __instance.AudioSources.Count; i++)
            {
                var src = __instance.AudioSources[i];
                if (src != null && src.clip != null)
                {
                    MelonLogger.Msg($"[CustomSongs] Track [{(Mp3Player.SongTrackNames)i}]: isPlaying={src.isPlaying}, vol={src.volume:F2}, clip={src.clip.name}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(LocalizationManager), "GetFallBackLanguage")]
    public static class Patch_LocalizationManager_GetFallBackLanguage
    {
        public static bool Prefix(string langCode, ref CultureInfo __result)
        {
            try
            {
                var mgr = Traverse.Create(typeof(LocalizationManager)).Field("I").GetValue<LocalizationManager>();
                if (mgr == null || mgr.AvailableLanguages == null)
                {
                    __result = new CultureInfo("en-US");
                    return false;
                }

                if (string.IsNullOrEmpty(langCode))
                {
                    langCode = "en-US";
                }

                string match = mgr.AvailableLanguages
                    .FirstOrDefault(x => !string.IsNullOrEmpty(x) && string.Equals(x, langCode, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    __result = new CultureInfo(match);
                    return false;
                }

                if (mgr.FallbackLanguages != null)
                {
                    var fallback = mgr.FallbackLanguages
                        .FirstOrDefault(x => x != null && !string.IsNullOrEmpty(x.LanguageCode) && string.Equals(x.LanguageCode, langCode, StringComparison.OrdinalIgnoreCase));
                    if (fallback != null && !string.IsNullOrEmpty(fallback.FallbackLanguageCode))
                    {
                        __result = new CultureInfo(fallback.FallbackLanguageCode);
                        return false;
                    }
                }

                string langPrefix = langCode.Length >= 2 ? langCode.Substring(0, 2) : langCode;
                match = mgr.AvailableLanguages
                    .FirstOrDefault(x => !string.IsNullOrEmpty(x) && x.Length >= 2 && string.Equals(x.Substring(0, 2), langPrefix, StringComparison.OrdinalIgnoreCase));
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

    [HarmonyPatch(typeof(ControllerGuitarNote), "get_HasTrigger")]
    public static class Patch_ControllerGuitarNote_HasTrigger
    {
        public static bool Prefix(ControllerGuitarNote __instance, ref bool __result)
        {
            if (__instance.Buttons == null || __instance.Buttons.Count == 0)
            {
                __result = __instance.Tier < 2;
                return false;
            }
            if (!__instance.Buttons[0] && (__instance.Buttons.Count < 2 || !__instance.Buttons[1]))
            {
                __result = __instance.Tier < 2;
                return false;
            }
            __result = __instance.Buttons[0];
            return false;
        }
    }

    [HarmonyPatch(typeof(ControllerGuitarNote), "get_HasGrip")]
    public static class Patch_ControllerGuitarNote_HasGrip
    {
        public static bool Prefix(ControllerGuitarNote __instance, ref bool __result)
        {
            if (__instance.Buttons == null || __instance.Buttons.Count < 2)
            {
                __result = __instance.Tier >= 2;
                return false;
            }
            if (!__instance.Buttons[0] && !__instance.Buttons[1])
            {
                __result = __instance.Tier >= 2;
                return false;
            }
            __result = __instance.Buttons[1];
            return false;
        }
    }

    [HarmonyPatch(typeof(GuitarHandPoseRecognitionManager), "CheckHandPoseControllerMethod")]
    public static class Patch_GuitarHandPoseRecognitionManager_CheckHandPoseControllerMethod
    {
        public static void Prefix(GuitarHandPoseRecognitionManager __instance, ControllerGuitarNote controllerGuitarNote)
        {
            if (controllerGuitarNote == null) return;
            try
            {
                if (controllerGuitarNote.Buttons == null)
                {
                    controllerGuitarNote.Buttons = new List<bool>();
                }
                while (controllerGuitarNote.Buttons.Count < 2)
                {
                    int tier = controllerGuitarNote.Tier;
                    controllerGuitarNote.Buttons.Add(controllerGuitarNote.Buttons.Count == 0 ? (tier < 2) : (tier >= 2));
                }
            }
            catch { }
        }
    }

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
                    closerNote.Fingers = tier switch
                    {
                        0 => new List<TierFinger> { TierFinger.Index },
                        1 => new List<TierFinger> { TierFinger.Middle },
                        2 => new List<TierFinger> { TierFinger.Ring },
                        _ => new List<TierFinger> { TierFinger.Pinky }
                    };
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GuitarNoteController), "SetFingers", new Type[] { typeof(List<TierFinger>) })]
    public static class Patch_GuitarNoteController_SetFingers
    {
        public static void Prefix(GuitarNoteController __instance, ref List<TierFinger> fingers)
        {
            if (fingers == null || fingers.Count == 0)
            {
                int tier = 0;
                if (__instance.Note is GuitarNote gn) tier = gn.Tier;
                fingers = tier switch
                {
                    0 => new List<TierFinger> { TierFinger.Index },
                    1 => new List<TierFinger> { TierFinger.Middle },
                    2 => new List<TierFinger> { TierFinger.Ring },
                    _ => new List<TierFinger> { TierFinger.Pinky }
                };
            }
        }

        public static void Postfix(GuitarNoteController __instance)
        {
            try
            {
                var block = Traverse.Create(__instance).Field("notePropertyBlock").GetValue<MaterialPropertyBlock>();
                if (__instance.NoteRenderer != null && block != null)
                {
                    __instance.NoteRenderer.SetPropertyBlock(block);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GuitarControllerNoteController), "SetFingers", new Type[] { typeof(List<bool>), typeof(int) })]
    public static class Patch_GuitarControllerNoteController_SetFingers_List
    {
        public static void Prefix(GuitarControllerNoteController __instance, ref List<bool> buttons)
        {
            if (buttons == null || buttons.Count < 2 || (!buttons[0] && !buttons[1]))
            {
                int tier = 0;
                if (__instance.Note is GuitarNote gn) tier = gn.Tier;
                buttons = new List<bool> { tier < 2, tier >= 2 };
            }
        }

        public static void Postfix(GuitarControllerNoteController __instance)
        {
            try
            {
                var block = Traverse.Create(__instance).Field("notePropertyBlock").GetValue<MaterialPropertyBlock>();
                if (__instance.NoteRenderer != null && block != null)
                {
                    __instance.NoteRenderer.SetPropertyBlock(block);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GuitarControllerNoteController), "SetFingers", new Type[] { typeof(bool), typeof(bool), typeof(int) })]
    public static class Patch_GuitarControllerNoteController_SetFingers_Bools
    {
        public static void Prefix(GuitarControllerNoteController __instance, ref bool triggerActive, ref bool gripActive)
        {
            if (!triggerActive && !gripActive)
            {
                int tier = 0;
                if (__instance.Note is GuitarNote gn) tier = gn.Tier;
                triggerActive = tier < 2;
                gripActive = tier >= 2;
            }
        }

        public static void Postfix(GuitarControllerNoteController __instance)
        {
            try
            {
                var block = Traverse.Create(__instance).Field("notePropertyBlock").GetValue<MaterialPropertyBlock>();
                if (__instance.NoteRenderer != null && block != null)
                {
                    __instance.NoteRenderer.SetPropertyBlock(block);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(GameManager), "Finish")]
    public static class Patch_GameManager_Finish
    {
        public static bool Prefix()
        {
            if (GameManager.SongTime < 2.0f && GameManager.IsPlaying)
            {
                MelonLogger.Msg($"[CustomSongs] Suppressed early Finish() at time={GameManager.SongTime:F2}s");
                return false;
            }
            return true;
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
                        var songs = album.SongsMetadata;
                        if (songs != null)
                        {
                            int idx = songs.FindIndex(s => s.SongID == GameManager.CurrentSongData.SongID);
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
                }
            }
            catch (Exception ex)
            {
                if (ModMain.ConfigVerbose) MelonLogger.Error($"[DiscordRPC/Cover] Error in StartSong Postfix: {ex}");
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
                    SongMenu.SelectedSongChanged += (song) =>
                    {
                        if (song != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                        {
                            VinylCoverHelper.UpdateTurntableVinyl(song);
                        }
                    };
                }

                string lastAlbum = GamePreferences.Load(UnpluggedInfo.LastSongPlayedAlbum, "");
                if (lastAlbum == "CustomSongsAlbum" && CustomSongLoader.CustomAlbumInstance != null)
                {
                    if (AlbumsManager.SelectedAlbum != CustomSongLoader.CustomAlbumInstance)
                    {
                        int lastSong = GamePreferences.Load(UnpluggedInfo.LastSongPlayed, 0);
                        AlbumsManager.SelectedAlbum = CustomSongLoader.CustomAlbumInstance;
                        if (__instance.songMenu != null)
                        {
                            __instance.songMenu.InitialOffset = lastSong;
                        }
                        MelonLogger.Msg($"[CustomSongs] StartSpace: Restored Custom Songs Album with song index {lastSong}!");
                    }
                }
                CustomSongLoader.UpdateAlbumCover(CustomSongLoader.CustomAlbumInstance);

                if (__instance.songMenu != null && __instance.songMenu.SelectedSong != null && AlbumsManager.SelectedAlbum != null && AlbumsManager.SelectedAlbum.AlbumID == "CustomSongsAlbum")
                {
                    VinylCoverHelper.UpdateTurntableVinyl(__instance.songMenu.SelectedSong);
                }
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
                        var trav = Traverse.Create(__instance);
                        var coverImg = trav.Field("coverImage").GetValue<UnityEngine.UI.RawImage>();
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

}