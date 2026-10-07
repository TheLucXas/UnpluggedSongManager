"""
Quest Patcher for Unplugged: Air Guitar
Automates extracting, patching, cloning (coexistence as 'Unplugged Modded'),
repackaging, signing, and deploying the modded APK to Meta Quest via ADB.
"""

import os
import sys
import subprocess
import shutil
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Optional, List, Tuple, Callable

# Paths relative to repository
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
TOOLS_DIR = REPO_ROOT / "tools" / "quest"
APKTOOL_JAR = TOOLS_DIR / "apktool.jar"
UBER_SIGNER_JAR = TOOLS_DIR / "uber-apk-signer.jar"
DEFAULT_WORK_DIR = REPO_ROOT / "tools" / "quest_work"

ORIGINAL_PACKAGE = "com.Anotherway.UnpluggedVR"
MODDED_PACKAGE = "com.Anotherway.UnpluggedVR.modded"
MODDED_APP_NAME = "Unplugged Modded"

class AdbHelper:
    def __init__(self, adb_path: str = "adb"):
        self.adb_path = adb_path

    def run_adb(self, args: List[str], device_id: Optional[str] = None, timeout: int = 60) -> Tuple[int, str, str]:
        cmd = [self.adb_path]
        if device_id:
            cmd.extend(["-s", device_id])
        cmd.extend(args)
        
        proc = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout
        )
        return proc.returncode, proc.stdout.strip(), proc.stderr.strip()

    def get_devices(self) -> List[Tuple[str, str, str]]:
        """Returns list of (serial, status, model_info)"""
        code, out, _ = self.run_adb(["devices", "-l"])
        if code != 0:
            return []
        
        devices = []
        for line in out.splitlines()[1:]:
            line = line.strip()
            if not line:
                continue
            parts = line.split()
            if len(parts) >= 2:
                serial = parts[0]
                status = parts[1]
                model = "Unknown"
                for p in parts[2:]:
                    if p.startswith("model:"):
                        model = p.split(":", 1)[1]
                        break
                devices.append((serial, status, model))
        return devices

    def connect_wireless(self, ip_port: str) -> Tuple[bool, str]:
        if ":" not in ip_port:
            ip_port = f"{ip_port}:5555"
        code, out, err = self.run_adb(["connect", ip_port], timeout=15)
        if "connected" in out.lower() and "unable" not in out.lower():
            return True, out
        return False, out or err

    def is_package_installed(self, package_name: str, device_id: Optional[str] = None) -> bool:
        code, out, _ = self.run_adb(["shell", "pm", "list", "packages"], device_id=device_id)
        if code != 0:
            return False
        for line in out.splitlines():
            line = line.strip()
            if line.startswith("package:"):
                line = line[len("package:"):].strip()
            if line.lower() == package_name.lower():
                return True
        return False

    def get_package_apk_path(self, package_name: str, device_id: Optional[str] = None) -> Optional[str]:
        # Resolve exact package casing
        code, out, _ = self.run_adb(["shell", "pm", "list", "packages"], device_id=device_id)
        target = package_name
        if code == 0:
            for line in out.splitlines():
                pkg = line.strip()
                if pkg.startswith("package:"):
                    pkg = pkg[len("package:"):].strip()
                if pkg.lower() == package_name.lower():
                    target = pkg
                    break

        code, out, _ = self.run_adb(["shell", "pm", "path", target], device_id=device_id)
        if code != 0 or not out:
            return None
        # Format: package:/data/app/.../base.apk
        for line in out.splitlines():
            line = line.strip()
            if line.startswith("package:"):
                return line.split(":", 1)[1]
        return None

    def pull_file(self, remote_path: str, local_path: str, device_id: Optional[str] = None) -> bool:
        code, _, _ = self.run_adb(["pull", remote_path, local_path], device_id=device_id, timeout=300)
        return code == 0 and os.path.exists(local_path)

    def install_apk(self, local_apk_path: str, device_id: Optional[str] = None) -> Tuple[bool, str]:
        code, out, err = self.run_adb(["install", "-r", "-d", local_apk_path], device_id=device_id, timeout=300)
        success = "success" in out.lower()
        return success, out or err

    def grant_storage_permissions(self, package_name: str, device_id: Optional[str] = None):
        self.run_adb(["shell", "appops", "set", package_name, "MANAGE_EXTERNAL_STORAGE", "allow"], device_id=device_id)
        self.run_adb(["shell", "pm", "grant", package_name, "android.permission.READ_EXTERNAL_STORAGE"], device_id=device_id)
        self.run_adb(["shell", "pm", "grant", package_name, "android.permission.WRITE_EXTERNAL_STORAGE"], device_id=device_id)

    def push_songs(self, local_songs_dir: str, device_id: Optional[str] = None, progress_cb: Optional[Callable[[str], None]] = None) -> bool:
        if not os.path.isdir(local_songs_dir):
            if progress_cb: progress_cb(f"Local songs directory not found: {local_songs_dir}")
            return False
        
        target_dir = "/sdcard/Unplugged/Songs"
        self.run_adb(["shell", "mkdir", "-p", target_dir], device_id=device_id)
        if progress_cb: progress_cb(f"Pushing songs from {local_songs_dir} to {target_dir}...")
        
        code, out, err = self.run_adb(["push", local_songs_dir, "/sdcard/Unplugged/"], device_id=device_id, timeout=600)
        if code == 0:
            if progress_cb: progress_cb("Custom songs synced successfully to Quest!")
            return True
        else:
            if progress_cb: progress_cb(f"Failed to sync songs: {out or err}")
            return False


class QuestPatcher:
    def __init__(self, work_dir: Optional[Path] = None, logger: Optional[Callable[[str], None]] = None):
        self.work_dir = work_dir or DEFAULT_WORK_DIR
        self.work_dir.mkdir(parents=True, exist_ok=True)
        self.logger = logger or print
        self.adb = AdbHelper()

    def log(self, msg: str):
        self.logger(msg)

    def check_prerequisites(self) -> Tuple[bool, str]:
        # Check Java
        try:
            res = subprocess.run(["java", "-version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if res.returncode != 0:
                return False, "Java is not installed or not in system PATH."
        except Exception:
            return False, "Java is not installed or not in system PATH."

        # Check apktool
        if not APKTOOL_JAR.exists():
            return False, f"apktool.jar not found at {APKTOOL_JAR}"

        # Check uber-apk-signer
        if not UBER_SIGNER_JAR.exists():
            return False, f"uber-apk-signer.jar not found at {UBER_SIGNER_JAR}"

        # Check adb
        try:
            res = subprocess.run(["adb", "version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            if res.returncode != 0:
                return False, "ADB is not installed or not in system PATH."
        except Exception:
            return False, "ADB is not installed or not in system PATH."

        return True, "All prerequisites verified."

    def extract_from_quest(self, device_id: Optional[str] = None, target_apk: Optional[Path] = None) -> Optional[Path]:
        self.log("Detecting Unplugged package on Quest...")
        if not self.adb.is_package_installed(ORIGINAL_PACKAGE, device_id):
            self.log(f"Original package '{ORIGINAL_PACKAGE}' not found on device.")
            # Check if modded package is already installed
            if self.adb.is_package_installed(MODDED_PACKAGE, device_id):
                self.log(f"Found existing modded package '{MODDED_PACKAGE}'. Pulling existing APK...")
                apk_remote = self.adb.get_package_apk_path(MODDED_PACKAGE, device_id)
            else:
                return None
        else:
            apk_remote = self.adb.get_package_apk_path(ORIGINAL_PACKAGE, device_id)

        if not apk_remote:
            self.log("Could not resolve APK path on device.")
            return None

        dest_apk = target_apk or (self.work_dir / "unplugged_pulled.apk")
        self.log(f"Pulling APK from Quest ({apk_remote}) -> {dest_apk}...")
        if self.adb.pull_file(apk_remote, str(dest_apk), device_id):
            size_mb = dest_apk.stat().st_size / (1024 * 1024)
            self.log(f"APK successfully extracted ({size_mb:.1f} MB).")
            return dest_apk
        else:
            self.log("Failed to pull APK from Quest.")
            return None

    def decompile_apk(self, apk_path: Path, out_dir: Path) -> bool:
        if out_dir.exists():
            shutil.rmtree(out_dir, ignore_errors=True)
        
        self.log(f"Decompiling APK with Apktool (this may take 1-2 minutes)...")
        cmd = ["java", "-jar", str(APKTOOL_JAR), "d", "-f", "-o", str(out_dir), str(apk_path)]
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
        if proc.returncode != 0:
            self.log(f"Decompilation failed: {proc.stderr}")
            return False
        self.log("APK decompiled successfully.")
        return True

    def patch_manifest_and_metadata(self, decompiled_dir: Path) -> bool:
        manifest_file = decompiled_dir / "AndroidManifest.xml"
        if not manifest_file.exists():
            self.log(f"AndroidManifest.xml not found in {decompiled_dir}")
            return False

        self.log("Patching AndroidManifest.xml for modding & coexistence...")
        content = manifest_file.read_text(encoding="utf-8", errors="replace")

        # 1. Update package attribute cleanly
        content = re.sub(r'package="[^"]*"', f'package="{MODDED_PACKAGE}"', content)

        # 2. Update provider authorities cleanly
        content = re.sub(r'android:authorities="[^"]*"', f'android:authorities="{MODDED_PACKAGE}"', content)

        # 3. Add storage permissions if missing
        permissions_to_add = [
            '<uses-permission android:name="android.permission.READ_EXTERNAL_STORAGE" />',
            '<uses-permission android:name="android.permission.WRITE_EXTERNAL_STORAGE" />',
            '<uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE" />'
        ]
        
        manifest_tag_match = re.search(r'(<manifest[^>]*>)', content)
        if manifest_tag_match:
            insert_pos = manifest_tag_match.end()
            for perm in permissions_to_add:
                perm_name = re.search(r'android:name="([^"]+)"', perm).group(1)
                if perm_name not in content:
                    content = content[:insert_pos] + "\n    " + perm + content[insert_pos:]
                    insert_pos += len("\n    " + perm)

        # 4. Request legacy external storage & extractNativeLibs in <application>
        app_tag_match = re.search(r'<application([^>]*)>', content)
        if app_tag_match:
            attrs = app_tag_match.group(1)
            updated_attrs = attrs
            if 'android:requestLegacyExternalStorage' not in updated_attrs:
                updated_attrs += ' android:requestLegacyExternalStorage="true"'
            if 'android:hasFragileUserData' not in updated_attrs:
                updated_attrs += ' android:hasFragileUserData="true"'
            if 'android:extractNativeLibs' not in updated_attrs:
                updated_attrs += ' android:extractNativeLibs="true"'
            
            content = content[:app_tag_match.start()] + f"<application{updated_attrs}>" + content[app_tag_match.end():]

        manifest_file.write_text(content, encoding="utf-8")
        self.log("AndroidManifest.xml updated with cloned package ID and storage permissions.")

        # 5. Update app name / label in res/values/strings.xml
        strings_xml = decompiled_dir / "res" / "values" / "strings.xml"
        if strings_xml.exists():
            s_content = strings_xml.read_text(encoding="utf-8", errors="replace")
            if 'name="app_name"' in s_content:
                s_content = re.sub(r'<string name="app_name">.*?</string>', f'<string name="app_name">{MODDED_APP_NAME}</string>', s_content)
                strings_xml.write_text(s_content, encoding="utf-8")
                self.log(f"Updated app title to '{MODDED_APP_NAME}' in strings.xml.")

        # 6. Create custom songs target directory structure in assets or sdcard config
        return True

    def rebuild_apk(self, decompiled_dir: Path, output_unsigned_apk: Path) -> bool:
        self.log("Rebuilding APK with Apktool (this may take 1-2 minutes)...")
        if output_unsigned_apk.exists():
            output_unsigned_apk.unlink()

        cmd = ["java", "-jar", str(APKTOOL_JAR), "b", str(decompiled_dir), "-o", str(output_unsigned_apk)]
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
        if proc.returncode != 0:
            self.log(f"APK rebuild failed: {proc.stderr}")
            return False
        
        self.log(f"APK rebuilt successfully -> {output_unsigned_apk}")
        return True

    def sign_apk(self, unsigned_apk: Path, output_dir: Path) -> Optional[Path]:
        self.log("Signing APK with Uber-APK-Signer (v2/v3 signing)...")
        output_dir.mkdir(parents=True, exist_ok=True)
        
        cmd = [
            "java", "-jar", str(UBER_SIGNER_JAR),
            "-a", str(unsigned_apk),
            "-o", str(output_dir)
        ]
        proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
        if proc.returncode != 0:
            self.log(f"APK signing failed: {proc.stderr}")
            return False

        # Find signed apk in output_dir
        stem = unsigned_apk.stem
        candidates = list(output_dir.glob(f"{stem}*aligned*debugSigned.apk")) or list(output_dir.glob(f"{stem}*.apk"))
        if candidates:
            signed_apk = candidates[0]
            self.log(f"Signed APK generated successfully: {signed_apk}")
            return signed_apk
        
        # Check if original was overwritten
        if unsigned_apk.exists():
            return unsigned_apk
        return None

    def deploy_to_quest(self, signed_apk: Path, device_id: Optional[str] = None) -> bool:
        self.log(f"Installing modded APK to Quest ({MODDED_PACKAGE})...")
        success, out = self.adb.install_apk(str(signed_apk), device_id)
        if not success:
            self.log(f"Installation failed: {out}")
            return False

        # Ensure OBB exists
        obb_target = f"/sdcard/Android/obb/{MODDED_PACKAGE}"
        self.adb.run_adb(["shell", "mkdir", "-p", obb_target], device_id)
        self.adb.run_adb(["shell", f"cp /sdcard/Android/obb/{ORIGINAL_PACKAGE}/main.*.{ORIGINAL_PACKAGE}.obb {obb_target}/main.410.{MODDED_PACKAGE}.obb"], device_id)

        self.log("Installation successful! Configuring storage permissions...")
        self.adb.grant_storage_permissions(MODDED_PACKAGE, device_id)
        
        # Create directories for custom songs
        self.adb.run_adb(["shell", "mkdir", "-p", "/sdcard/Unplugged/Songs"], device_id)
        self.adb.run_adb(["shell", "mkdir", "-p", f"/sdcard/ModData/{MODDED_PACKAGE}/Songs"], device_id)

        # Inject unlocked save data (Tutorial Skipped + Campaign Albums 1-5 Unlocked)
        self.setup_unlocked_save_data(device_id)

        self.log("Quest storage permissions and directories configured!")
        return True

    def setup_unlocked_save_data(self, device_id: Optional[str] = None):
        self.log("Injecting pre-unlocked save file (Tutorial skipped & Campaign Albums 1-5 unlocked)...")
        # Pre-encrypted AES payload with game's key 'b14ca5898a4e4133bbce2ea2315a1916'
        save_b64 = (
            "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"
            "bn0prQ1MXK0Ku8jxKmV2givdCS"
        )
        local_save = self.work_dir / "SavedData.json"
        local_save.write_text(save_b64, encoding="utf-8")
        
        target_dir1 = f"/sdcard/Android/data/{MODDED_PACKAGE}/files"
        self.adb.run_adb(["shell", "mkdir", "-p", target_dir1], device_id)
        self.adb.run_adb(["push", str(local_save), f"{target_dir1}/SavedData.json"], device_id)

        target_dir2 = f"/sdcard/ModData/{MODDED_PACKAGE}/files"
        self.adb.run_adb(["shell", "mkdir", "-p", target_dir2], device_id)
        self.adb.run_adb(["push", str(local_save), f"{target_dir2}/SavedData.json"], device_id)
        self.log("Unlocked save data deployed successfully!")

    def run_full_pipeline(self, device_id: Optional[str] = None, source_apk: Optional[str] = None) -> bool:
        self.log("=== STARTING QUEST MODDED PIPELINE ===")
        ok, msg = self.check_prerequisites()
        if not ok:
            self.log(f"Prerequisite error: {msg}")
            return False

        # Step 1: Obtain APK
        if source_apk and os.path.isfile(source_apk):
            apk_file = Path(source_apk)
            self.log(f"Using provided APK: {apk_file}")
        else:
            apk_file = self.extract_from_quest(device_id)
            if not apk_file:
                self.log("Aborting: Could not retrieve Unplugged APK from Quest.")
                return False

        # Step 2: Decompile
        decompiled_dir = self.work_dir / "unplugged_decompiled"
        if not self.decompile_apk(apk_file, decompiled_dir):
            return False

        # Step 3: Patch
        if not self.patch_manifest_and_metadata(decompiled_dir):
            return False

        # Step 4: Rebuild
        unsigned_apk = self.work_dir / "unplugged_modded_unsigned.apk"
        if not self.rebuild_apk(decompiled_dir, unsigned_apk):
            return False

        # Step 5: Sign
        signed_dir = self.work_dir / "signed"
        signed_apk = self.sign_apk(unsigned_apk, signed_dir)
        if not signed_apk:
            self.log("Aborting: Signing failed.")
            return False

        # Step 6: Deploy to Quest if device connected
        if device_id or self.adb.get_devices():
            if not self.deploy_to_quest(signed_apk, device_id):
                return False
        else:
            self.log(f"No device specified/connected. Modded APK is saved at: {signed_apk}")

        self.log("=== QUEST MODDED PIPELINE COMPLETE ===")
        return True

if __name__ == "__main__":
    patcher = QuestPatcher()
    if len(sys.argv) > 1 and sys.argv[1] == "--check":
        ok, msg = patcher.check_prerequisites()
        print(f"Prerequisites: {ok} - {msg}")
        devices = patcher.adb.get_devices()
        print(f"Connected devices: {devices}")
        sys.exit(0 if ok else 1)
    
    dev_id = sys.argv[1] if len(sys.argv) > 1 else None
    success = patcher.run_full_pipeline(device_id=dev_id)
    sys.exit(0 if success else 1)
