import urllib.request
import json
import os
import time

ENCHOR_SEARCH_URL = "https://api.enchor.us/search"
ENCHOR_FILES_URL = "https://files.enchor.us"

USER_AGENT = "UnpluggedSongStudio/1.0 (VR Rhythm Game Mod Tool)"

def format_duration(seconds):
    if not seconds:
        return "--:--"
    try:
        sec = int(seconds)
        m = sec // 60
        s = sec % 60
        return f"{m}:{s:02d}"
    except Exception:
        return "--:--"

class ChorusAPI:
    @staticmethod
    def search(query="", page=1, per_page=20, instrument="guitar"):
        """
        Searches the Chorus Encore database for charts.
        """
        payload = {
            "search": query.strip() if query.strip() else "*",
            "page": page,
            "per_page": per_page,
            "instrument": instrument if instrument else None,
            "source": "api"
        }

        data_bytes = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(
            ENCHOR_SEARCH_URL,
            data=data_bytes,
            headers={
                "Content-Type": "application/json",
                "User-Agent": USER_AGENT
            }
        )

        try:
            with urllib.request.urlopen(req, timeout=12) as response:
                result = json.loads(response.read().decode("utf-8"))
                charts = []
                for item in result.get("data", []):
                    md5 = item.get("md5", "")
                    art_md5 = item.get("albumArtMd5", "")
                    
                    diff_g = item.get("diff_guitar")
                    diff_guitar = int(diff_g) if diff_g is not None and str(diff_g).isdigit() else -1
                    
                    diff_b = item.get("diff_bass")
                    diff_bass = int(diff_b) if diff_b is not None and str(diff_b).isdigit() else -1

                    preview_sec = 30.0
                    if item.get("preview_start_time"):
                        try:
                            preview_sec = float(item["preview_start_time"]) / 1000.0
                        except Exception:
                            pass

                    chart_obj = {
                        "id": str(item.get("chartId", md5)),
                        "md5": md5,
                        "title": item.get("name", "Unknown Title"),
                        "artist": item.get("artist", "Unknown Artist"),
                        "album": item.get("album", ""),
                        "year": item.get("year", ""),
                        "charter": item.get("charter", "Unknown"),
                        "duration": format_duration(item.get("song_length")),
                        "duration_sec": item.get("song_length", 0),
                        "diff_guitar": diff_guitar,
                        "diff_bass": diff_bass,
                        "preview_start": preview_sec,
                        "download_url": f"{ENCHOR_FILES_URL}/{md5}.sng" if md5 else None,
                        "art_url": f"{ENCHOR_FILES_URL}/{art_md5}.jpg" if art_md5 else None
                    }
                    charts.append(chart_obj)

                return {
                    "total": result.get("total", len(charts)),
                    "charts": charts
                }
        except Exception as e:
            print(f"[ChorusAPI] Search error: {e}")
            raise e

    @staticmethod
    def download_file(url, target_path, progress_callback=None, cancel_flag=None):
        """
        Downloads a file chunk-by-chunk with a progress callback.
        progress_callback(received_bytes, total_bytes)
        """
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=30) as resp:
            total_size = int(resp.headers.get("Content-Length", 0))
            chunk_size = 64 * 1024 # 64 KB
            downloaded = 0

            os.makedirs(os.path.dirname(os.path.abspath(target_path)), exist_ok=True)
            with open(target_path, "wb") as f:
                while True:
                    if cancel_flag and cancel_flag():
                        raise Exception("Descarga cancelada por el usuario.")
                    chunk = resp.read(chunk_size)
                    if not chunk:
                        break
                    f.write(chunk)
                    downloaded += len(chunk)
                    if progress_callback:
                        progress_callback(downloaded, total_size)

        return target_path
