using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace USM.Core.Services;

/// <summary>
/// Prepares SteamVR actions before SteamVR starts. Doing this from MelonLoader
/// is too late when vrserver has already cached the game's binding file.
/// </summary>
internal static class PcSteamVrBindings
{
    private const string ActionPrefix = "/actions/default/in/";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Prepare(string gamePath)
    {
        string directory = Path.Combine(gamePath, "Unplugged_Data", "StreamingAssets", "SteamVR");
        string manifestPath = Path.Combine(directory, "actions.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("SteamVR actions.json was not found.", manifestPath);

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
            ?? throw new InvalidDataException("Invalid SteamVR actions.json.");
        var actions = manifest["actions"] as JsonArray
            ?? throw new InvalidDataException("SteamVR action list is missing.");
        bool changed = false;
        changed |= AddAction(actions, "USM_LeftStick", "vector2");
        changed |= AddAction(actions, "USM_RightStick", "vector2");
        changed |= AddAction(actions, "USM_LeftPrimary", "boolean");
        changed |= AddAction(actions, "USM_RightPrimary", "boolean");
        if (changed) Save(manifestPath, manifest);

        var profiles = new[]
        {
            (File: "bindings_oculus_touch.json", Axis: "joystick", Mode: "joystick", LeftButton: "x"),
            (File: "bindings_knuckles.json", Axis: "thumbstick", Mode: "joystick", LeftButton: "a"),
            (File: "bindings_holographic_controller.json", Axis: "trackpad", Mode: "trackpad", LeftButton: "x"),
            (File: "bindings_vive_cosmos_controller.json", Axis: "joystick", Mode: "joystick", LeftButton: "x"),
            (File: "bindings_vive_controller.json", Axis: "trackpad", Mode: "trackpad", LeftButton: (string?)null)
        };
        foreach (var profile in profiles)
        {
            string path = Path.Combine(directory, profile.File);
            if (!File.Exists(path)) continue;
            var binding = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"Invalid SteamVR binding: {profile.File}.");
            var sources = binding["bindings"]?["/actions/default"]?["sources"] as JsonArray
                ?? throw new InvalidDataException($"SteamVR source list is missing: {profile.File}.");
            bool bindingChanged = false;
            if (profile.File == "bindings_oculus_touch.json")
            {
                bindingChanged |= RemoveOldOculusStick(sources, "left");
                bindingChanged |= RemoveOldOculusStick(sources, "right");
            }
            bindingChanged |= AddStick(sources, "left", profile.Axis, profile.Mode, "USM_LeftStick");
            bindingChanged |= AddStick(sources, "right", profile.Axis, profile.Mode, "USM_RightStick");
            if (profile.LeftButton != null)
            {
                bindingChanged |= AddButton(sources, "left", profile.LeftButton, "USM_LeftPrimary");
                bindingChanged |= AddButton(sources, "right", "a", "USM_RightPrimary");
            }
            if (bindingChanged) Save(path, binding);
        }
    }

    private static bool AddAction(JsonArray actions, string name, string type)
    {
        string path = ActionPrefix + name;
        if (actions.OfType<JsonObject>().Any(item => EqualsIgnoreCase(item["name"], path))) return false;
        actions.Add(new JsonObject { ["name"] = path, ["type"] = type, ["requirement"] = "optional" });
        return true;
    }

    private static bool RemoveOldOculusStick(JsonArray sources, string hand)
    {
        string path = $"/user/hand/{hand}/input/thumbstick";
        string output = ActionPrefix + $"usm_{hand}stick";
        var old = sources.OfType<JsonObject>().Where(item =>
            EqualsIgnoreCase(item["path"], path) &&
            EqualsIgnoreCase(item["inputs"]?["position"]?["output"], output)).ToArray();
        foreach (var item in old) sources.Remove(item);
        return old.Length > 0;
    }

    private static bool AddStick(JsonArray sources, string hand, string axis, string mode, string action)
    {
        string path = $"/user/hand/{hand}/input/{axis}";
        string output = ActionPrefix + action.ToLowerInvariant();
        var source = sources.OfType<JsonObject>().FirstOrDefault(item => EqualsIgnoreCase(item["path"], path));
        bool changed = false;
        if (source == null)
        {
            source = new JsonObject { ["path"] = path, ["mode"] = mode,
                ["parameters"] = new JsonObject(), ["inputs"] = new JsonObject() };
            sources.Add(source);
            changed = true;
        }
        if (!EqualsIgnoreCase(source["mode"], mode)) { source["mode"] = mode; changed = true; }
        var inputs = source["inputs"] as JsonObject;
        if (inputs == null) { inputs = new JsonObject(); source["inputs"] = inputs; changed = true; }
        if (!EqualsIgnoreCase(inputs["position"]?["output"], output))
        {
            inputs["position"] = new JsonObject { ["output"] = output };
            changed = true;
        }
        return changed;
    }

    private static bool AddButton(JsonArray sources, string hand, string button, string action)
    {
        string output = ActionPrefix + action.ToLowerInvariant();
        if (sources.OfType<JsonObject>().Any(item => EqualsIgnoreCase(item["inputs"]?["click"]?["output"], output)))
            return false;
        sources.Add(new JsonObject
        {
            ["path"] = $"/user/hand/{hand}/input/{button}", ["mode"] = "button",
            ["parameters"] = new JsonObject(),
            ["inputs"] = new JsonObject { ["click"] = new JsonObject { ["output"] = output } }
        });
        return true;
    }

    private static bool EqualsIgnoreCase(JsonNode? node, string value) =>
        string.Equals(node?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase);

    private static void Save(string path, JsonObject document)
    {
        string backup = path + ".usm-original";
        if (!File.Exists(backup)) File.Copy(path, backup);
        string temporary = path + ".usm-writing";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
