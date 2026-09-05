using System.IO;
using System.Text.Json;

namespace Pasty.Models;

public class AppSettings
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    public const uint VK_V = 0x56;

    public int RetentionDays { get; set; } = 0;          // 0 = 永久
    public int MaxItems { get; set; } = 500;
    public uint ShowHotkeyModifiers { get; set; } = MOD_CONTROL | MOD_SHIFT;
    public uint ShowHotkeyVk { get; set; } = VK_V;       // Ctrl+Shift+V
    public uint PasteTopHotkeyModifiers { get; set; } = MOD_CONTROL | MOD_ALT;
    public uint PasteTopHotkeyVk { get; set; } = VK_V;   // Ctrl+Alt+V
    public bool OverrideCtrlV { get; set; } = true;
    public bool AutoStart { get; set; }
    public int Theme { get; set; } = 0;                  // 0 跟随系统 1 浅色 2 深色
    public bool HideOnDeactivate { get; set; } = true;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pasty", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 损坏时使用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string HotkeyName(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(((char)vk).ToString());
        return string.Join(" + ", parts);
    }
}
