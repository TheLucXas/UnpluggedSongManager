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

ORIGINAL_PACKAGE = "com.anotherway.unplugged"
MODDED_PACKAGE = "com.anotherway.unplugged.modded"
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
        code, out, _ = self.run_adb(["shell", "pm", "list", "packages", package_name], device_id=device_id)
        if code != 0:
            return False
        for line in out.splitlines():
            line = line.strip()
            if line == f"package:{package_name}":
                return True
        return False

    def get_package_apk_path(self, package_name: str, device_id: Optional[str] = None) -> Optional[str]:
        code, out, _ = self.run_adb(["shell", "pm", "path", package_name], device_id=device_id)
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

        # 1. Update package attribute
        content = re.sub(
            r'package="' + re.escape(ORIGINAL_PACKAGE) + r'"',
            f'package="{MODDED_PACKAGE}"',
            content
        )

        # 2. Update provider authorities
        content = re.sub(
            re.escape(ORIGINAL_PACKAGE),
            MODDED_PACKAGE,
            content
        )

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
            "-o", str(output_dir),
            "--overwrite"
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

        self.log("Installation successful! Configuring storage permissions...")
        self.adb.grant_storage_permissions(MODDED_PACKAGE, device_id)
        
        # Create directories for custom songs
        self.adb.run_adb(["shell", "mkdir", "-p", "/sdcard/Unplugged/Songs"], device_id)
        self.adb.run_adb(["shell", "mkdir", "-p", f"/sdcard/ModData/{MODDED_PACKAGE}/Songs"], device_id)
        self.log("Quest storage permissions and directories configured!")
        return True

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
