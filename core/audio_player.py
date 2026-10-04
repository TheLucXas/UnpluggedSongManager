import os
import tempfile
import urllib.request
import threading

try:
    import pygame
    PYGAME_AVAILABLE = True
except ImportError:
    PYGAME_AVAILABLE = False

class AudioPlayer:
    _instance = None

    def __init__(self):
        self.initialized = False
        self.current_track = None
        self.temp_stream_file = None
        self._init_mixer()

    def _init_mixer(self):
        if not PYGAME_AVAILABLE:
            return
        try:
            pygame.mixer.init(frequency=44100, size=-16, channels=2, buffer=2048)
            self.initialized = True
        except Exception as e:
            print(f"[AudioPlayer] Failed to initialize pygame.mixer: {e}")

    def play(self, audio_source):
        """
        audio_source can be a local file path or an HTTP URL.
        """
        if not self.initialized:
            self._init_mixer()
            if not self.initialized:
                return False

        self.stop()

        if audio_source.startswith("http://") or audio_source.startswith("https://"):
            threading.Thread(target=self._stream_url_and_play, args=(audio_source,), daemon=True).start()
        else:
            return self._play_local(audio_source)
        return True

    def _play_local(self, file_path):
        if not os.path.isfile(file_path):
            return False
        try:
            pygame.mixer.music.load(file_path)
            pygame.mixer.music.set_volume(0.85)
            pygame.mixer.music.play()
            self.current_track = file_path
            return True
        except Exception as e:
            print(f"[AudioPlayer] Error playing local file: {e}")
            return False

    def _stream_url_and_play(self, url):
        try:
            with tempfile.NamedTemporaryFile(suffix=".mp3", delete=False) as tf:
                self.temp_stream_file = tf.name
                req = urllib.request.Request(url, headers={"User-Agent": "UnpluggedSongStudio/1.0"})
                with urllib.request.urlopen(req, timeout=10) as resp:
                    tf.write(resp.read())
            self._play_local(self.temp_stream_file)
        except Exception as e:
            print(f"[AudioPlayer] Error streaming url preview: {e}")

    def stop(self):
        if not self.initialized:
            return
        try:
            pygame.mixer.music.stop()
            pygame.mixer.music.unload()
        except Exception:
            pass
        self.current_track = None
        if self.temp_stream_file and os.path.isfile(self.temp_stream_file):
            try:
                os.remove(self.temp_stream_file)
            except Exception:
                pass
            self.temp_stream_file = None

    def pause(self):
        if self.initialized:
            try: pygame.mixer.music.pause()
            except Exception: pass

    def unpause(self):
        if self.initialized:
            try: pygame.mixer.music.unpause()
            except Exception: pass

    def is_playing(self):
        if self.initialized:
            try: return pygame.mixer.music.get_busy()
            except Exception: pass
        return False

    def set_volume(self, vol):
        if self.initialized:
            try: pygame.mixer.music.set_volume(max(0.0, min(1.0, float(vol))))
            except Exception: pass

    def shutdown(self):
        self.stop()
        if self.initialized:
            try:
                pygame.mixer.quit()
                self.initialized = False
            except Exception:
                pass

player_instance = AudioPlayer()
