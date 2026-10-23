using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnobControl;

internal sealed class MenuItemConfig
{
    /// <summary>"brightness", "volume" or "app".</summary>
    public string Kind { get; set; } = "app";
    public string Name { get; set; } = "";
    /// <summary>Start-menu AppID (see Get-StartApps), launched through shell:AppsFolder.</summary>
    public string? AppId { get; set; }
    /// <summary>Executable path, used when there is no AppId.</summary>
    public string? Path { get; set; }
    /// <summary>Process name to bring to the front if it is already running, instead of launching again.</summary>
    public string? Process { get; set; }

    [JsonIgnore] public bool IsLevel => Kind.Equals("brightness", StringComparison.OrdinalIgnoreCase)
                                     || Kind.Equals("volume", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsBrightness => Kind.Equals("brightness", StringComparison.OrdinalIgnoreCase);
}

internal sealed class Settings
{
    public List<MenuItemConfig> Items { get; set; } = new();
    /// <summary>Percent change per knob click while adjusting brightness or volume.</summary>
    public int Step { get; set; } = 5;
    /// <summary>Diameter of the circle in device-independent pixels.</summary>
    public int Size { get; set; } = 440;
    public int HideAfterMs { get; set; } = 2500;
    /// <summary>Substring of the Raw Input device path that identifies the knob's keyboard.</summary>
    public string DeviceMatch { get; set; } = "VID_320F&PID_5055";

    public static string FilePath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KnobControl", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        if (!File.Exists(FilePath))
        {
            var created = CreateDefault();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(created, Json));
            return created;
        }

        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? CreateDefault();
        if (s.Items.Count < 2 || s.Items.Count > 8)
            throw new InvalidDataException($"settings.json needs between 2 and 8 items (found {s.Items.Count}).");
        s.Step = Math.Clamp(s.Step, 1, 50);
        s.Size = Math.Clamp(s.Size, 240, 1000);
        s.HideAfterMs = Math.Clamp(s.HideAfterMs, 500, 30000);
        return s;
    }

    static Settings CreateDefault() => new()
    {
        Items =
        {
            new() { Kind = "brightness", Name = "Brightness" },
            new() { Kind = "volume", Name = "Volume" },
            new() { Name = "Chrome", AppId = "Chrome", Process = "chrome" },
            new() { Name = "Claude", AppId = "Claude_pzs8sxrjxfjjc!Claude" },
            new() { Name = "ChatGPT", AppId = "OpenAI.Codex_2p2nqsd0c76g0!App" },
        },
    };
}
