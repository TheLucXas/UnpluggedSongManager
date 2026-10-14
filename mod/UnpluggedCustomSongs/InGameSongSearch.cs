using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MelonLoader;

#if QUEST_IL2CPP
using Il2Cpp;
using Il2CppTMPro;
using Il2CppUnplugged.Model;
using SongList = Il2CppSystem.Collections.Generic.List<Il2CppUnplugged.Model.SongMappingMetaData>;
#else
using TMPro;
using Unplugged.Model;
using SongList = System.Collections.Generic.List<Unplugged.Model.SongMappingMetaData>;
#endif

namespace UnpluggedCustomSongs
{
    // The search operates on the album's existing metadata. No archive is opened
    // while typing, and the album's full song list remains intact for saves.
    internal static class InGameSongSearch
    {
        private static readonly List<PressableButton> KeyboardButtons = new List<PressableButton>();
        private static readonly List<PressableButton> ChorusButtons = new List<PressableButton>();
        private static PressableButton _searchButton;
        private static PressableButton _chorusButton;
        private static TextMeshPro _statusLabel;
        private static int _menuId;
        private static bool _isOpen;
        private static bool _isFiltered;
        private static bool _isChangingAlbum;
        private static bool _buttonsAnchored;
        private static float _changeStartedAt;
        private static string _query = string.Empty;
        private static string _message = string.Empty;
        private static bool _chorusMode;
        private static int _chorusPage = 1;
        private static int _chorusTotal;
        private static int _chorusSelection;
        private static List<ChorusResult> _chorusResults = new List<ChorusResult>();
        private static Task<(int Total, List<ChorusResult> Charts)> _chorusSearchTask;
        private static Task<string> _chorusDownloadTask;
        private static string _chorusProgress = string.Empty;
        private static float _normalStatusFontSize;
        private static Vector3 _keyboardCenter;
        private static Vector3 _keyboardRight;
        private static Vector3 _keyboardUp;
        private static float _keyColumn;
        private static float _keyRow;

        public static bool IsOpen => _isOpen;
        public static bool IsChangingAlbum => _isChangingAlbum;

        public static void ResetForLibraryReload()
        {
            // Updating the custom album recreates its menu objects. Keep the
            // browser open so a completed import remains visible to the player.
            bool wasOpen = _isOpen;
            bool wasChorus = _chorusMode;
            string query = _query;
            DestroyControls();
            _isOpen = wasOpen;
            _chorusMode = wasOpen && wasChorus;
            _query = query;
        }

        public static void Update()
        {
            CompleteChorusWork();
            var start = StartSpace.I;
            var menu = start?.songMenu;
            bool customAlbum = AlbumsManager.SelectedAlbum?.AlbumID == "CustomSongsAlbum";
            if (start == null || menu == null || !customAlbum)
            {
                DestroyControls();
                return;
            }

            if (_menuId != menu.GetInstanceID())
            {
                DestroyControls();
                _menuId = menu.GetInstanceID();
            }

            if (!start.gameObject.activeInHierarchy || !menu.gameObject.activeInHierarchy || GameManager.IsPlaying)
            {
                SetControlsVisible(false);
                return;
            }

            if (_isChangingAlbum && Time.unscaledTime - _changeStartedAt >= 4.2f)
                _isChangingAlbum = false;

            if (_searchButton == null)
                CreateControls(start);
            PositionLauncherButtons(start);
            if (_isOpen && KeyboardButtons.Count == 0)
            {
                CreateKeyboard(start.StartButton);
                if (_chorusMode)
                    ConfigureChorusLabel();
            }

            if (_searchButton != null)
                _searchButton.IsDisabled = _isChangingAlbum;
            if (_chorusButton != null)
                _chorusButton.IsDisabled = _isChangingAlbum;
            SetControlsVisible(_buttonsAnchored);
            if (_isOpen)
                ReadPhysicalKeyboard();
        }

        private static void CreateControls(StartSpace start)
        {
            var template = start.StartButton;
            if (template == null) return;

            var collider = template.GetComponentInChildren<BoxCollider>(true);
            float width = collider == null ? 0.35f :
                Mathf.Clamp(Mathf.Abs(collider.size.x * collider.transform.lossyScale.x), 0.25f, 0.65f);
            float height = collider == null ? 0.09f :
                Mathf.Clamp(Mathf.Abs(collider.size.y * collider.transform.lossyScale.y), 0.07f, 0.18f);
            _keyColumn = Mathf.Clamp(width * 0.19f, 0.055f, 0.09f);
            _keyRow = Mathf.Clamp(height * 0.85f, 0.060f, 0.095f);
            Vector3 right = template.transform.right;
            Vector3 up = template.transform.up;
            var camera = CameraManager.Main;
            Vector3 towardPlayer = camera == null ? template.transform.forward :
                (camera.transform.position - template.transform.position).normalized;
            _keyboardCenter = template.transform.position + up * (height * 2.7f) + towardPlayer * 0.14f;
            _keyboardRight = right;
            _keyboardUp = up;

            _searchButton = CreateButton(template, "USM_SearchToggle", "SEARCH", 0.42f, 0.72f, Toggle);
            if (_searchButton == null) return;

            _chorusButton = CreateButton(template, "USM_ChorusToggle", "CHORUS", 0.42f, 0.72f, ToggleChorus);

            MelonLogger.Msg($"[CustomSongs][Search] Search button created for {CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata?.Count ?? 0} songs.");
        }

        private static void PositionLauncherButtons(StartSpace start)
        {
            if (_buttonsAnchored || _searchButton == null || _chorusButton == null ||
                !ModMain.IsCustomCarouselSettled) return;

            var venue = start.GetComponentInChildren<VenueSelectionController>(true);
            if (venue == null)
                venue = UnityEngine.Object.FindObjectOfType<VenueSelectionController>();
            var label = venue?.VenueNameLabel;
            var difficulty = start.difficultyPanel;
            if (label == null || difficulty?.PrevButton == null || difficulty.NextButtton == null)
                return;

            Vector3 axis = difficulty.NextButtton.transform.position - difficulty.PrevButton.transform.position;
            axis.y = 0f;
            float difficultySpan = axis.magnitude;
            if (difficultySpan < 0.05f) return;
            axis /= difficultySpan;

            var labelRenderer = label.GetComponent<Renderer>();
            Vector3 anchor = labelRenderer != null ? labelRenderer.bounds.center : label.transform.position;
            var camera = CameraManager.Main;
            if (camera != null)
                anchor += (camera.transform.position - anchor).normalized * 0.08f;

            // Symmetric offsets from the venue title keep both buttons in the
            // same visible row across menu animation and headset recentering.
            float distance = difficultySpan * 0.95f;
            PositionButtonVisualCenter(_chorusButton, anchor - axis * distance);
            PositionButtonVisualCenter(_searchButton, anchor + axis * distance);
            _buttonsAnchored = true;
            MelonLogger.Msg($"[CustomSongs][Search] Buttons anchored beside venue '{label.text}': " +
                $"center={anchor}, distance={distance:F3}, " +
                $"left={GetButtonVisualCenter(_chorusButton)}, right={GetButtonVisualCenter(_searchButton)}.");
        }

        private static Vector3 GetButtonVisualCenter(PressableButton button)
        {
            var collider = button.GetComponentInChildren<BoxCollider>(true);
            return collider == null ? button.transform.position :
                collider.transform.TransformPoint(collider.center);
        }

        private static void PositionButtonVisualCenter(PressableButton button, Vector3 target)
        {
            button.transform.position = target;
            button.transform.position += target - GetButtonVisualCenter(button);
        }

        private static void CreateKeyboard(PressableButton template)
        {
            if (template == null || KeyboardButtons.Count > 0) return;

            string[] rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                string letters = rows[rowIndex];
                for (int col = 0; col < letters.Length; col++)
                {
                    char letter = letters[col];
                    var key = CreateButton(template, "USM_Search_" + letter, letter.ToString(),
                        0.18f, 0.55f, () => Append(letter));
                    if (key == null) continue;
                    key.transform.position = _keyboardCenter
                        + _keyboardRight * ((col - (letters.Length - 1) * 0.5f) * _keyColumn)
                        + _keyboardUp * ((2f - rowIndex) * _keyRow);
                    KeyboardButtons.Add(key);
                }
            }

            string[] actions = { "SP", "DEL", "GO", "ALL", "X" };
            Action[] callbacks = { () => Append(' '), Backspace, Apply, ShowAll, Close };
            for (int col = 0; col < actions.Length; col++)
            {
                var key = CreateButton(template, "USM_Search_" + actions[col], actions[col],
                    0.30f, 0.55f, callbacks[col]);
                if (key == null) continue;
                key.transform.position = _keyboardCenter
                    + _keyboardRight * ((col - 2f) * _keyColumn * 1.7f)
                    - _keyboardUp * (2f * _keyRow);
                KeyboardButtons.Add(key);
            }

            string[] chorusActions = { "< PAGE", "UP", "DOWN", "PAGE >", "GET" };
            Action[] chorusCallbacks = { PreviousChorusPage, PreviousChorusResult,
                NextChorusResult, NextChorusPage, DownloadChorusSelection };
            for (int col = 0; col < chorusActions.Length; col++)
            {
                var key = CreateButton(template, "USM_Chorus_" + col, chorusActions[col],
                    0.32f, 0.55f, chorusCallbacks[col]);
                if (key == null) continue;
                key.transform.position = _keyboardCenter
                    + _keyboardRight * ((col - 2f) * _keyColumn * 1.7f)
                    - _keyboardUp * (3.0f * _keyRow);
                ChorusButtons.Add(key);
            }

            var originalText = template.GetComponentInChildren<TextMeshPro>(true);
            if (originalText != null)
            {
                var labelObject = UnityEngine.Object.Instantiate(originalText.gameObject, template.transform.parent);
                labelObject.name = "USM_SearchStatus";
                _statusLabel = labelObject.GetComponent<TextMeshPro>();
                if (_statusLabel != null)
                {
                    _statusLabel.transform.position = _keyboardCenter + _keyboardUp * (3.2f * _keyRow);
                    _statusLabel.transform.rotation = originalText.transform.rotation;
                    _statusLabel.alignment = TextAlignmentOptions.Center;
                    _statusLabel.color = Color.white;
                    _statusLabel.fontSize = originalText.fontSize * 0.75f;
                    _statusLabel.overflowMode = TextOverflowModes.Overflow;
                    _normalStatusFontSize = _statusLabel.fontSize;
                }
            }

            RefreshLabel();
            SetKeyboardVisible(false);
            MelonLogger.Msg("[CustomSongs][Search] On-demand keyboard created.");
        }

        private static PressableButton CreateButton(PressableButton template, string name, string label,
            float widthScale, float heightScale, Action callback)
        {
            var obj = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
            obj.SetActive(false);
            obj.name = name;
            var button = obj.GetComponent<PressableButton>();
            if (button == null)
            {
                UnityEngine.Object.Destroy(obj);
                return null;
            }

            button.IgnoreLocalization = true;
            button.OnPressed = null;
            button.Text = label;
            button.SetText(label);
            button.transform.rotation = template.transform.rotation;
            Vector3 scale = template.transform.localScale;
            button.transform.localScale = new Vector3(scale.x * widthScale,
                scale.y * heightScale, scale.z * 0.82f);
            foreach (var text in button.GetComponentsInChildren<TextMeshPro>(true))
            {
                text.text = label;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Color.white;
            }
#if QUEST_IL2CPP
            button.add_Pressed(callback);
#else
            button.Pressed += callback;
#endif
            return button;
        }

        private static void Toggle()
        {
            if (_isChangingAlbum) return;
            _isOpen = !_isOpen || _chorusMode;
            _chorusMode = false;
            if (_isOpen && KeyboardButtons.Count == 0)
                CreateKeyboard(StartSpace.I?.StartButton);
            _message = string.Empty;
            if (_statusLabel != null)
            {
                _statusLabel.fontSize = _normalStatusFontSize;
                _statusLabel.alignment = TextAlignmentOptions.Center;
                _statusLabel.transform.position = _keyboardCenter + _keyboardUp * (3.2f * _keyRow);
            }
            SetKeyboardVisible(_isOpen);
            RefreshLabel();
        }

        private static void ToggleChorus()
        {
            if (_isChangingAlbum) return;
            _isOpen = !_isOpen || !_chorusMode;
            _chorusMode = _isOpen;
            if (_isOpen && KeyboardButtons.Count == 0)
                CreateKeyboard(StartSpace.I?.StartButton);
            ConfigureChorusLabel();
            SetKeyboardVisible(_isOpen);
            RefreshLabel();
        }

        private static void ConfigureChorusLabel()
        {
            if (_statusLabel == null) return;
            _statusLabel.fontSize = _normalStatusFontSize * 0.60f;
            _statusLabel.alignment = TextAlignmentOptions.BottomLeft;
            _statusLabel.transform.position = _keyboardCenter + _keyboardUp * (3.2f * _keyRow)
                - _keyboardRight * (4.3f * _keyColumn);
        }

        private static void Close()
        {
            _isOpen = false;
            _chorusMode = false;
            SetKeyboardVisible(false);
        }

        private static void Append(char value)
        {
            if (_query.Length >= 40) return;
            _query += value;
            _message = string.Empty;
            RefreshLabel();
        }

        private static void Backspace()
        {
            if (_query.Length > 0) _query = _query.Substring(0, _query.Length - 1);
            _message = string.Empty;
            RefreshLabel();
        }

        private static void ReadPhysicalKeyboard()
        {
            foreach (char value in Input.inputString)
            {
                if (value == '\b') Backspace();
                else if (value == '\r' || value == '\n') Apply();
                else if (!char.IsControl(value)) Append(value);
            }
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            if (_chorusMode && Input.GetKeyDown(KeyCode.UpArrow)) PreviousChorusResult();
            if (_chorusMode && Input.GetKeyDown(KeyCode.DownArrow)) NextChorusResult();
        }

        private static void ShowAll()
        {
            _query = string.Empty;
            if (_chorusMode)
            {
                SearchChorus(1);
                return;
            }
            Apply();
        }

        private static void Apply()
        {
            if (_chorusMode)
            {
                SearchChorus(1);
                return;
            }
            if (_isChangingAlbum) return;
            var all = CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata;
            var menu = StartSpace.I?.songMenu;
            if (all == null || all.Count == 0 || menu == null) return;

            string query = _query.Trim();
            SongList matches;
            if (query.Length == 0)
            {
                matches = CustomSongLoader.CustomAlbumInstance.SongsMetadata;
            }
            else
            {
                matches = new SongList();
                foreach (var song in all)
                    if (song != null && IsMatch(song, query)) matches.Add(song);
            }

            if (matches.Count == 0)
            {
                _message = "NO MATCHES";
                RefreshLabel();
                return;
            }

            if (query.Length == 0 && !_isFiltered)
            {
                Close();
                return;
            }

            if (query.Length == 0)
            {
                try
                {
                    string selectedId = menu.SelectedSong?.SongID;
                    for (int i = 0; i < all.Count; i++)
                        if (all[i] != null && string.Equals(all[i].SongID, selectedId, StringComparison.Ordinal))
                        {
                            CustomSongLoader.QueueDirectSelectionIndex(i);
                            break;
                        }
                }
                catch { }
            }

            _isFiltered = query.Length > 0;
            _isChangingAlbum = true;
            _changeStartedAt = Time.unscaledTime;
            menu.InitialOffset = 0;
            ModMain.MarkCarouselChanging();
            Close();
            if (_searchButton != null)
            {
                string caption = _isFiltered ? "FILTER" : "SEARCH";
                _searchButton.Text = caption;
                _searchButton.SetText(caption);
            }
            menu.ChangeAlbum(matches);
            MelonLogger.Msg($"[CustomSongs][Search] Applied '{query}': {matches.Count}/{all.Count} song(s), difficulty unchanged.");
        }

        private static bool IsMatch(SongMappingMetaData song, string query)
        {
            string haystack = Normalize((song.SongName ?? "") + " " + (song.BandName ?? ""));
            foreach (string token in Normalize(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (haystack.IndexOf(token, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        private static string Normalize(string text)
        {
            string decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (char value in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(value) != UnicodeCategory.NonSpacingMark)
                    result.Append(char.ToUpperInvariant(value));
            return result.ToString();
        }

        private static void SearchChorus(int page)
        {
            if (_chorusSearchTask != null || _chorusDownloadTask != null) return;
            _chorusPage = Math.Max(1, page);
            _chorusResults.Clear();
            _chorusSelection = 0;
            _chorusProgress = "SEARCHING CHORUS...";
            string query = _query.Trim();
            int requestedPage = _chorusPage;
            _chorusSearchTask = Task.Run(() => InGameChorusService.SearchAsync(
                query, requestedPage, CancellationToken.None));
            RefreshLabel();
        }

        private static void PreviousChorusPage()
        {
            if (_chorusPage > 1) SearchChorus(_chorusPage - 1);
        }

        private static void NextChorusPage()
        {
            if (_chorusPage * InGameChorusService.PageSize < _chorusTotal) SearchChorus(_chorusPage + 1);
        }

        private static void PreviousChorusResult()
        {
            if (_chorusResults.Count == 0) return;
            _chorusSelection = (_chorusSelection + _chorusResults.Count - 1) % _chorusResults.Count;
            RefreshLabel();
        }

        private static void NextChorusResult()
        {
            if (_chorusResults.Count == 0) return;
            _chorusSelection = (_chorusSelection + 1) % _chorusResults.Count;
            RefreshLabel();
        }

        private static void DownloadChorusSelection()
        {
            if (_chorusResults.Count == 0 || _chorusDownloadTask != null || _chorusSearchTask != null) return;
            ChorusResult selected = _chorusResults[_chorusSelection];
            _chorusProgress = "DOWNLOADING " + selected.Title;
            string songsFolder = CustomSongLoader.CustomSongsFolder;
            string temporaryFolder = Application.temporaryCachePath;
            _chorusDownloadTask = Task.Run(() => InGameChorusService.DownloadAndConvertAsync(
                selected, songsFolder, temporaryFolder,
                step => _chorusProgress = step, CancellationToken.None));
            RefreshLabel();
        }

        private static void CompleteChorusWork()
        {
            if (_chorusSearchTask != null && _chorusSearchTask.IsCompleted)
            {
                var completed = _chorusSearchTask;
                _chorusSearchTask = null;
                if (completed.IsFaulted)
                {
                    _chorusProgress = ShortError("SEARCH FAILED: ", completed.Exception?.GetBaseException());
                    MelonLogger.Error("[CustomSongs][Chorus] " + completed.Exception?.GetBaseException());
                }
                else if (completed.IsCanceled)
                    _chorusProgress = "SEARCH CANCELLED";
                else
                {
                    _chorusTotal = completed.Result.Total;
                    _chorusResults = completed.Result.Charts;
                    _chorusSelection = 0;
                    _chorusProgress = _chorusResults.Count == 0 ? "NO RESULTS" :
                        "SELECT A RESULT, THEN PRESS GET";
                }
                RefreshLabel();
            }

            if (_chorusDownloadTask != null && _chorusDownloadTask.IsCompleted)
            {
                var completed = _chorusDownloadTask;
                _chorusDownloadTask = null;
                if (completed.IsFaulted)
                {
                    _chorusProgress = ShortError("IMPORT FAILED: ", completed.Exception?.GetBaseException());
                    MelonLogger.Error("[CustomSongs][Chorus] " + completed.Exception?.GetBaseException());
                    RefreshLabel();
                }
                else if (completed.IsCanceled)
                {
                    _chorusProgress = "IMPORT CANCELLED";
                    RefreshLabel();
                }
                else
                {
                    _chorusProgress = "INSTALLED: " + System.IO.Path.GetFileName(completed.Result);
                    MelonLogger.Msg("[CustomSongs][Chorus] Installed " + completed.Result);
                    ModMain.PerformSongRefresh();
                }
            }
        }

        private static string ShortError(string prefix, Exception error)
        {
            string message = error?.Message ?? "Unknown error";
            return prefix + (message.Length > 100 ? message.Substring(0, 99) + "…" : message);
        }

        private static void RefreshLabel()
        {
            if (_statusLabel == null) return;
            if (_chorusMode)
            {
                string heading = _query.Trim().Length == 0 ? "CHORUS: ALL CHARTS" : "CHORUS: " + _query.Trim();
                var lines = new List<string> { heading + "  PAGE " + _chorusPage + "  (" + _chorusTotal + ")" };
                if (!string.IsNullOrWhiteSpace(_chorusProgress)) lines.Add(_chorusProgress);
                for (int i = 0; i < _chorusResults.Count; i++)
                {
                    var chart = _chorusResults[i];
                    string title = chart.Title.Length > 31 ? chart.Title.Substring(0, 30) + "…" : chart.Title;
                    string artist = chart.Artist.Length > 22 ? chart.Artist.Substring(0, 21) + "…" : chart.Artist;
                    lines.Add((i == _chorusSelection ? "> " : "  ") + (i + 1) + ". " + title + " - " + artist);
                }
                if (_chorusResults.Count > 0)
                {
                    var selected = _chorusResults[_chorusSelection];
                    string charter = selected.Charter.Length > 26 ? selected.Charter.Substring(0, 25) + "…" : selected.Charter;
                    lines.Add("CHARTER: " + charter + "  |  GUITAR TIER: " + selected.GuitarDifficulty +
                        "  |  " + (selected.DurationSeconds / 60) + ":" + (selected.DurationSeconds % 60).ToString("D2"));
                }
                _statusLabel.text = string.Join("\n", lines);
                return;
            }
            int total = CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata?.Count ?? 0;
            int matches = 0;
            var all = CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata;
            if (all != null)
                foreach (var song in all)
                    if (song != null && IsMatch(song, _query.Trim())) matches++;
            string shownQuery = _query.Length == 0 ? "TYPE TITLE OR ARTIST" : _query;
            _statusLabel.text = $"{shownQuery}  [{matches}/{total}]" +
                (string.IsNullOrEmpty(_message) ? "" : "\n" + _message);
        }

        private static void SetControlsVisible(bool visible)
        {
            if (_searchButton != null && _searchButton.gameObject.activeSelf != visible)
                _searchButton.gameObject.SetActive(visible);
            if (_chorusButton != null && _chorusButton.gameObject.activeSelf != visible)
                _chorusButton.gameObject.SetActive(visible);
            SetKeyboardVisible(visible && _isOpen);
        }

        private static void SetKeyboardVisible(bool visible)
        {
            foreach (var button in KeyboardButtons)
                if (button != null && button.gameObject.activeSelf != visible)
                    button.gameObject.SetActive(visible);
            foreach (var button in ChorusButtons)
                if (button != null && button.gameObject.activeSelf != (visible && _chorusMode))
                    button.gameObject.SetActive(visible && _chorusMode);
            if (_statusLabel != null && _statusLabel.gameObject.activeSelf != visible)
                _statusLabel.gameObject.SetActive(visible);
        }

        private static void DestroyControls()
        {
            if (_searchButton != null) UnityEngine.Object.Destroy(_searchButton.gameObject);
            if (_chorusButton != null) UnityEngine.Object.Destroy(_chorusButton.gameObject);
            foreach (var button in KeyboardButtons)
                if (button != null) UnityEngine.Object.Destroy(button.gameObject);
            foreach (var button in ChorusButtons)
                if (button != null) UnityEngine.Object.Destroy(button.gameObject);
            if (_statusLabel != null) UnityEngine.Object.Destroy(_statusLabel.gameObject);
            KeyboardButtons.Clear();
            ChorusButtons.Clear();
            _searchButton = null;
            _chorusButton = null;
            _statusLabel = null;
            _menuId = 0;
            _isOpen = false;
            _chorusMode = false;
            _isFiltered = false;
            _isChangingAlbum = false;
            _buttonsAnchored = false;
            _query = string.Empty;
            _message = string.Empty;
        }
    }
}
