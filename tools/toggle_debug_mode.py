import sys, os, json, re, subprocess

enable_debug = (len(sys.argv) > 1 and sys.argv[1].lower() in ['1', 'true', 'enable', 'on'])

# 0. Kill running SteamVR processes if running, so settings apply freshly
try:
    subprocess.run(["taskkill", "/F", "/IM", "vrmonitor.exe", "/IM", "vrserver.exe", "/IM", "vrcompositor.exe"], 
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
except Exception:
    pass

# 1. Update MelonPreferences.cfg
prefs_path = r"C:\SteamLibrary\steamapps\common\Unplugged\UserData\MelonPreferences.cfg"
if os.path.exists(prefs_path):
    with open(prefs_path, "r", encoding="utf-8") as f:
        content = f.read()
    if "EnableDebugMode" in content:
        content = re.sub(r'EnableDebugMode\s*=\s*(true|false)', f'EnableDebugMode = {"true" if enable_debug else "false"}', content)
    else:
        content = content.replace("[UnpluggedCustomSongs]", f"[UnpluggedCustomSongs]\nEnableDebugMode = {'true' if enable_debug else 'false'}")
    with open(prefs_path, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"MelonPreferences actualizado: EnableDebugMode = {enable_debug}")

# 2. Update steamvr.vrsettings
vr_path = r"C:\Program Files (x86)\Steam\config\steamvr.vrsettings"
if os.path.exists(vr_path):
    try:
        with open(vr_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        if "steamvr" not in data: data["steamvr"] = {}
        if "driver_null" not in data: data["driver_null"] = {}
        if "dashboard" not in data: data["dashboard"] = {}
        
        if enable_debug:
            # Null driver (Desktop mode sin headset)
            data["steamvr"]["requireHmd"] = False
            data["steamvr"]["forcedDriver"] = "null"
            data["steamvr"]["activateMultipleDrivers"] = True
            data["driver_null"]["enable"] = True
            
            # Desactivar Dashboard / Menu de pausa de SteamVR
            data["dashboard"]["enableDashboard"] = False
            data["dashboard"]["autoShowGameTheater"] = False
            data["steamvr"]["startDashboardFromAppLaunch"] = False
            data["steamvr"]["startOverlayAppsFromDashboard"] = False
            data["steamvr"]["pauseGameWhenDashboardIsVisible"] = False
        else:
            # VR Normal (Headset Quest / SteamVR)
            data["steamvr"]["requireHmd"] = True
            data["steamvr"]["forcedDriver"] = ""
            data["steamvr"]["activateMultipleDrivers"] = False
            data["driver_null"]["enable"] = False
            
            # Restaurar Dashboard de SteamVR
            data["dashboard"]["enableDashboard"] = True
            data["dashboard"]["autoShowGameTheater"] = True
            data["steamvr"]["startDashboardFromAppLaunch"] = True
            data["steamvr"]["startOverlayAppsFromDashboard"] = True
            data["steamvr"]["pauseGameWhenDashboardIsVisible"] = True
            
        with open(vr_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=3)
        print(f"SteamVR vrsettings actualizado: Dashboard desactivado = {enable_debug}, Null driver = {enable_debug}")
    except Exception as e:
        print(f"Error actualizando steamvr.vrsettings: {e}")

print(f"=== MODO DESKTOP DEBUG {'ACTIVADO' if enable_debug else 'DESACTIVADO (MODO VR NORMAL)'} ===")
