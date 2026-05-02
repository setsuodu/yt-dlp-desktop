using Microsoft.Playwright;
using System.Text.Json;
using System.Diagnostics;

// --- 1. 配置加载与优雅报错 ---
const string ConfigName = "config.json";
CrawlerConfig? config = null;

try
{
    if (!File.Exists(ConfigName))
    {
        Console.WriteLine($"❌ 错误: 找不到配置文件 '{ConfigName}'");
        Console.WriteLine($"💡 请确保该文件位于程序运行目录下: {AppDomain.CurrentDomain.BaseDirectory}");
        return;
    }

    var jsonString = await File.ReadAllTextAsync(ConfigName);
    config = JsonSerializer.Deserialize<CrawlerConfig>(jsonString);

    if (config == null) throw new Exception("配置文件格式非法（无法解析为 JSON）");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 程序初始化失败: {ex.Message}");
    return;
}

// --- 2. 目录解构规范化 ---
string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string metadataDir = Path.Combine(baseDir, "Metadata", $"{config.Name}_Data");
string videoDir = Path.Combine(baseDir, "Downloads", config.Name);

Directory.CreateDirectory(metadataDir);
Directory.CreateDirectory(videoDir);

Console.WriteLine($"🚀 项目: {config.Name} | 目标: EP{config.StartEp} - EP{config.EndEp}");

// --- 3. 阶段一：提取地址 (Playwright) ---
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.0.3 Mobile/15E148 Safari/104.1"
});

for (int ep = config.StartEp; ep <= config.EndEp; ep++)
{
    string epFile = Path.Combine(metadataDir, $"ep_{ep}.json");
    if (File.Exists(epFile)) continue;

    var page = await context.NewPageAsync();
    try
    {
        string url = config.BaseUrl.Replace("{id}", config.VideoId).Replace("{ep}", ep.ToString());
        await page.GotoAsync(url);

        if (await page.Locator("text=确认你不是机器人").IsVisibleAsync())
        {
            Console.WriteLine($"⚠️ EP{ep} 等待手动过验证...");
            while (await page.Locator("text=确认你不是机器人").IsVisibleAsync()) await Task.Delay(1000);
        }

        await page.WaitForSelectorAsync("ul#plays-ul li a", new() { Timeout = 10000 });
        var links = await page.QuerySelectorAllAsync("ul#plays-ul li a");
        var sources = new Dictionary<string, string>();

        foreach (var link in links)
        {
            var name = (await link.InnerTextAsync()).Trim();
            var href = await link.GetAttributeAsync("href");
            if (!string.IsNullOrEmpty(href) && href.Contains("http"))
                sources[name] = href.Replace("/_player_x_/", "");
        }

        if (sources.Count > 0)
        {
            await File.WriteAllTextAsync(epFile, JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"✅ [Metadata] EP{ep} 地址提取成功");
        }
    }
    catch (Exception ex) { Console.WriteLine($"❌ EP{ep} 抓取异常: {ex.Message}"); }
    finally { await page.CloseAsync(); }
}
await browser.CloseAsync();

// --- 4. 阶段二：多源轮询下载 (yt-dlp) ---
Console.WriteLine("\n📡 准备进入下载流...");
var downloadOptions = new ParallelOptions { MaxDegreeOfParallelism = config.MaxConcurrent };

await Parallel.ForEachAsync(Enumerable.Range(config.StartEp, config.EndEp - config.StartEp + 1), downloadOptions, async (ep, ct) =>
{
    string epFile = Path.Combine(metadataDir, $"ep_{ep}.json");
    string fileName = Path.Combine(videoDir, $"{config.Name} - {ep:D2}.mp4");

    if (File.Exists(fileName) && new FileInfo(fileName).Length > 1024 * 1024) return;

    if (!File.Exists(epFile)) return;
    var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(epFile));
    if (sources == null) return;

    foreach (var source in sources)
    {
        Console.WriteLine($"[EP{ep}] 尝试源: {source.Key}");
        if (await RunYtDlp(source.Value, fileName)) break;
    }
});

async Task<bool> RunYtDlp(string url, string output)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "yt-dlp",
        Arguments = $"\"{url}\" -o \"{output}\" --concurrent-fragments 5",
        UseShellExecute = false,
        CreateNoWindow = false
    };
    try
    {
        using var p = Process.Start(startInfo);
        if (p == null) return false;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
    catch { return false; }
}

public record CrawlerConfig(string VideoId, string Name, int StartEp, int EndEp, int MaxConcurrent, string BaseUrl);