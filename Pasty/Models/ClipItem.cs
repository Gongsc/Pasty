using System.Text.Json.Serialization;

namespace Pasty.Models;

public enum ClipType
{
    Text,
    Image,
}

/// <summary>列表中的分组标题行。</summary>
public class HeaderRow
{
    public string Title { get; set; } = string.Empty;
}

public class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipType Type { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }
    public int UseCount { get; set; }

    [JsonIgnore]
    public string PreviewText => Type == ClipType.Text
        ? Text.ReplaceLineEndings(" ").Trim()
        : $"图片 · {System.IO.Path.GetFileName(ImagePath)}";

    [JsonIgnore]
    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718"; // 取消置顶 / 置顶

    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var parts = new List<string> { LastUsedAt.ToString("MM-dd HH:mm") };
            if (Type == ClipType.Text)
            {
                var len = Text.Length;
                parts.Add(len > 1000 ? $"{len / 1000.0:F1}k 字符" : $"{len} 字符");
            }
            else if (ImagePath != null && File.Exists(ImagePath))
            {
                parts.Add($"{new FileInfo(ImagePath).Length / 1024.0:F0} KB");
            }
            if (IsPinned) parts.Add("置顶");
            return string.Join(" · ", parts);
        }
    }
}
