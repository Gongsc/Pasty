using System.IO;
using System.Text.Json;

namespace Pasty.Models;

public class AppSettings
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    public const uint VK_V = 0x56;
    public const uint DefaultShowHotkeyModifiers = MOD_CONTROL | MOD_SHIFT;
    public const uint DefaultShowHotkeyVk = VK_V;
    public const uint DefaultPasteHotkeyModifiers = MOD_CONTROL | MOD_ALT;
    public const uint DefaultPasteHotkeyVk = VK_V;

    public int RetentionDays { get; set; } = 0;          // 0 = 永久
    public int MaxItems { get; set; } = 500;
    public uint ShowHotkeyModifiers { get; set; } = DefaultShowHotkeyModifiers;
    public uint ShowHotkeyVk { get; set; } = DefaultShowHotkeyVk;
    public uint PasteTopHotkeyModifiers { get; set; } = DefaultPasteHotkeyModifiers;
    public uint PasteTopHotkeyVk { get; set; } = DefaultPasteHotkeyVk;
    public bool OverrideCtrlV { get; set; } = true;
    public bool AutoStart { get; set; }
    public int Theme { get; set; } = 0;                  // 0 跟随系统 1 浅色 2 深色
    public bool HideOnDeactivate { get; set; } = true;
    public string Language { get; set; } = string.Empty; // 空值首次启动时：中文系统用中文，其余系统用英文

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
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }

    /// <summary>把虚拟键码转成适合设置页显示的稳定名称，不依赖固定快捷键选项表。</summary>
    private static string KeyName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2D => "Insert",
            0x2E => "Delete",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK 0x{vk:X2}",
        };
    }
}
