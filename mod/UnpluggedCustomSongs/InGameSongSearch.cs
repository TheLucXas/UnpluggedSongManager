using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        private static PressableButton _searchButton;
        private static TextMeshPro _statusLabel;
        private static int _menuId;
        private static bool _isOpen;
        private static bool _isFiltered;
        private static bool _isChangingAlbum;
        private static float _changeStartedAt;
        private static string _query = string.Empty;
        private static string _message = string.Empty;
        private static Vector3 _keyboardCenter;
        private static Vector3 _keyboardRight;
        private static Vector3 _keyboardUp;
        private static float _keyColumn;
        private static float _keyRow;

        public static bool IsOpen => _isOpen;
        public static bool IsChangingAlbum => _isChangingAlbum;

        public static void ResetForLibraryReload() => DestroyControls();

        public static void Update()
        {
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

            if (_searchButton != null)
                _searchButton.IsDisabled = _isChangingAlbum;
            SetControlsVisible(true);
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
            _searchButton.transform.position = template.transform.position + right * (width * 0.90f)
                + up * (height * 0.7f) + towardPlayer * 0.025f;

            MelonLogger.Msg($"[CustomSongs][Search] Search button created for {CustomSongLoader.CustomAlbumInstance?.builtInSongsMetadata?.Count ?? 0} songs.");
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
            _isOpen = !_isOpen;
            if (_isOpen && KeyboardButtons.Count == 0)
                CreateKeyboard(StartSpace.I?.StartButton);
            _message = string.Empty;
            SetKeyboardVisible(_isOpen);
            RefreshLabel();
        }

        private static void Close()
        {
            _isOpen = false;
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
        }

        private static void ShowAll()
        {
            _query = string.Empty;
            Apply();
        }

        private static void Apply()
        {
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

        private static void RefreshLabel()
        {
            if (_statusLabel == null) return;
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
            SetKeyboardVisible(visible && _isOpen);
        }

        private static void SetKeyboardVisible(bool visible)
        {
            foreach (var button in KeyboardButtons)
                if (button != null && button.gameObject.activeSelf != visible)
                    button.gameObject.SetActive(visible);
            if (_statusLabel != null && _statusLabel.gameObject.activeSelf != visible)
                _statusLabel.gameObject.SetActive(visible);
        }

        private static void DestroyControls()
        {
            if (_searchButton != null) UnityEngine.Object.Destroy(_searchButton.gameObject);
            foreach (var button in KeyboardButtons)
                if (button != null) UnityEngine.Object.Destroy(button.gameObject);
            if (_statusLabel != null) UnityEngine.Object.Destroy(_statusLabel.gameObject);
            KeyboardButtons.Clear();
            _searchButton = null;
            _statusLabel = null;
            _menuId = 0;
            _isOpen = false;
            _isFiltered = false;
            _isChangingAlbum = false;
            _query = string.Empty;
            _message = string.Empty;
        }
    }
}
