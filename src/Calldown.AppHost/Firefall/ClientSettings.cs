using System.Text.Json;
using System.Text.Json.Nodes;

namespace Calldown.AppHost.Firefall;

internal enum ScreenMode
{
    Keep,
    Windowed,
    Borderless,
    Fullscreen,
}

internal static class ClientSettings
{
    private static readonly Dictionary<string, int> NamedKeys = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    };

    public static void SetScreenMode(string settingsCon, ScreenMode mode, int width, int height)
    {
        var lines = File.Exists(settingsCon)
            ? File.ReadAllLines(settingsCon).ToList()
            : ["// Written by Calldown, the client rewrites this file when it exits"];

        switch (mode)
        {
            case ScreenMode.Windowed:
                SetSave(lines, "rd.videoMode", "0");
                SetSave(lines, "rd.windowWidth", width.ToString());
                SetSave(lines, "rd.windowHeight", height.ToString());
                break;

            // maximized_notitle covers the screen without taking it exclusively and takes its size from the desktop
            case ScreenMode.Borderless:
                SetSave(lines, "rd.videoMode", "3");
                break;

            // The client picked rd.fullscreenWidth/Height from a mode the adapter reported, so only the mode changes
            case ScreenMode.Fullscreen:
                SetSave(lines, "rd.videoMode", "1");
                break;

            default:
                return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsCon)!);
        File.WriteAllLines(settingsCon, lines);
    }

    public static void SetExitVehicleKey(string keybinds, int keyCode)
    {
        var root =
            (
                File.Exists(keybinds)
                    ? JsonNode.Parse(File.ReadAllText(keybinds)) as JsonObject
                    : null
            ) ?? new JsonObject { ["keyversion"] = 3, ["modifier"] = "disabled" };

        var categories = Child(root, "categories");
        var vehicle = Child(categories, "Vehicle");

        // -1 leaves a slot on whatever the static DB has for it, which for this action is nothing
        if (vehicle["ExitVehicle"] is JsonArray slots && slots.Count > 0)
        {
            slots[0] = keyCode;
        }
        else
        {
            vehicle["ExitVehicle"] = new JsonArray(keyCode, -1, -1, -1);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(keybinds)!);
        File.WriteAllText(
            keybinds,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );
    }

    /// <summary>
    /// Writes the [Debug] section the client reads at startup. "off" puts every log back on info, the client's default.
    /// </summary>
    public static List<string> SetDebugLogging(IReadOnlyList<string> firefallIni, string level)
    {
        var on = level != "off";
        var lines = IniFile.SetValue(firefallIni, "Debug", "LogLevel-*", on ? level : "info");
        foreach (var flag in new[] { "MatrixMessages", "GssMessages" })
        {
            lines = IniFile.SetValue(lines, "Debug", flag, on ? "true" : "false");
        }

        return lines;
    }

    public static int? ToKeyCode(string? key)
    {
        key = key?.Trim() ?? string.Empty;

        // Letters and digits are their own key code, so a single 5 is the digit key and not the code of the second mouse button
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }

        if (int.TryParse(key, out var code))
        {
            return code;
        }

        if (
            key.Length is 2 or 3
            && (key[0] is 'F' or 'f')
            && int.TryParse(key[1..], out var f)
            && f is >= 1 and <= 12
        )
        {
            return 0x70 + f - 1;
        }

        return NamedKeys.TryGetValue(key, out var named) ? named : null;
    }

    public static (int Width, int Height)? ToSize(string? size)
    {
        var parts = size?.Split('x', 'X', '*') ?? [];
        return
            parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var width)
            && int.TryParse(parts[1].Trim(), out var height)
            && width > 0
            && height > 0
            ? (width, height)
            : null;
    }

    private static void SetSave(List<string> lines, string cvar, string value)
    {
        var prefix = $"SetSave {cvar} ";
        var line = $"{prefix}\"{value}\"";

        var index = lines.FindIndex(l =>
            l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        );
        if (index == -1)
        {
            lines.Add(line);
        }
        else
        {
            lines[index] = line;
        }
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var child = new JsonObject();
        parent[name] = child;
        return child;
    }
}
