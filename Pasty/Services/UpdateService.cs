using System.Text.Json;

namespace Pasty.Services;

public sealed record UpdateCheckResult(bool HasUpdate, string LatestVersion, Uri ReleaseUri);

/// <summary>仅在用户主动点击“检查更新”时查询 GitHub Release。</summary>
public static class UpdateService
{
    public static readonly Uri ProjectUri = new("https://github.com/Gongsc/Pasty");
    private static readonly Uri LatestReleaseApi = new("https://api.github.com/repos/Gongsc/Pasty/releases/latest");
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 拒绝没有 User-Agent 的请求；只带应用名与公开版本号，不发送设备信息。
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Pasty/{App.Version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        using var response = await Client.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);

        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        var releaseUrl = root.GetProperty("html_url").GetString();
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl))
            throw new InvalidDataException("GitHub Release 缺少版本或地址");

        var normalized = tag.TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(normalized, out var latest) ||
            !Version.TryParse(App.Version, out var current))
            throw new InvalidDataException("GitHub Release 版本号格式无效");

        return new UpdateCheckResult(latest > current, tag, new Uri(releaseUrl));
    }
}
