using Microsoft.Playwright;
using System.Text.Json;

var jsonString = await File.ReadAllTextAsync("config.json");
var config = JsonSerializer.Deserialize<CrawlerConfig>(jsonString);
if (config == null) return;

string outputDir = $"{config.Name}_Data";
if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

using var playwright = await Playwright.CreateAsync();
// 【改动】Headless 设为 false，让你能看到浏览器并手动点击验证码
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false,
    SlowMo = 500 // 动作放慢点，更像真人
});

// 创建持久化上下文，这样点过一次验证码后，Cookie 会被记录，不用集集都点
var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 14_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/14.0.3 Mobile/15E148 Safari/104.1",
    ViewportSize = new ViewportSize { Width = 375, Height = 667 }
});

for (int ep = config.StartEp; ep <= config.EndEp; ep++)
{
    string epFile = Path.Combine(outputDir, $"ep_{ep}.json");
    if (File.Exists(epFile)) continue;

    var page = await context.NewPageAsync();
    try
    {
        string url = config.BaseUrl.Replace("{id}", config.VideoId).Replace("{ep}", ep.ToString());
        await page.GotoAsync(url);

        // 【关键】检测是否弹出了验证码
        if (await page.Locator("text=确认你不是机器人").IsVisibleAsync())
        {
            Console.WriteLine($"⚠️ 第 {ep} 集卡验证码了！赶紧去浏览器里点一下那个按钮！");
            // 死等，直到那个验证码弹窗消失
            while (await page.Locator("text=确认你不是机器人").IsVisibleAsync())
            {
                await Task.Delay(1000);
            }
            Console.WriteLine("✅ 检测到验证通过，继续干活...");
        }

        // 等待列表加载
        await page.WaitForSelectorAsync("ul#plays-ul li a", new() { State = WaitForSelectorState.Attached, Timeout = 30000 });

        var sourceLinks = await page.QuerySelectorAllAsync("ul#plays-ul li a");
        var epSources = new Dictionary<string, string>();

        foreach (var link in sourceLinks)
        {
            var sourceName = (await link.InnerTextAsync()).Trim();
            var href = await link.GetAttributeAsync("href");
            if (!string.IsNullOrEmpty(href) && href.Contains("http"))
            {
                epSources[sourceName] = href.Replace("/_player_x_/", "");
            }
        }

        if (epSources.Count > 0)
        {
            await File.WriteAllTextAsync(epFile, JsonSerializer.Serialize(epSources, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"✅ EP{ep} 成功");
        }
    }
    catch (Exception ex) { Console.WriteLine($"❌ EP{ep} 报错: {ex.Message}"); }
    finally { await page.CloseAsync(); }
}

// 汇总逻辑同上...
Console.WriteLine("🎉 搞定，去看 full_sources.json 吧。");

// --- 功能 2：自动化下载与多源容灾 ---
Console.WriteLine("\n开始扫描并下载视频文件...");

for (int ep = config.StartEp; ep <= config.EndEp; ep++)
{
    string epFile = Path.Combine(outputDir, $"ep_{ep}.json");
    if (!File.Exists(epFile)) continue;

    // 读取该集的所有可用源
    var jsonContent = await File.ReadAllTextAsync(epFile);
    var epSources = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent);

    if (epSources == null || epSources.Count == 0) continue;

    bool downloadSuccess = false;
    string fileName = $"{config.Name} - {ep:D2}.mp4"; // 格式化文件名

    // 依次尝试每一个 m3u8 地址
    foreach (var source in epSources)
    {
        Console.WriteLine($"[EP{ep}] 尝试使用源 {source.Key}: {source.Value}");

        // 调用 yt-dlp 进行下载
        if (await TryDownloadWithYtDlp(source.Value, fileName))
        {
            Console.WriteLine($"[EP{ep}] ✅ 下载成功 (源: {source.Key})");
            downloadSuccess = true;
            break; // 成功则跳出，处理下一集
        }
        else
        {
            Console.WriteLine($"[EP{ep}] ❌ 源 {source.Key} 失效，尝试下一个...");
        }
    }

    if (!downloadSuccess)
    {
        Console.WriteLine($"[EP{ep}] ⚠️ 所有源均已尝试，下载失败。");
    }
}

// 封装 yt-dlp 调用
async Task<bool> TryDownloadWithYtDlp(string m3u8Url, string outputName)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "yt-dlp",
        // 参数说明:
        // -o: 输出文件名
        // --fragment-retries: 单个分片重试次数
        // --check-formats: 下载前检查链接有效性
        Arguments = $"\"{m3u8Url}\" -o \"{outputName}\" --concurrent-fragments 5 --fragment-retries 3",
        UseShellExecute = false,
        CreateNoWindow = false
    };

    try
    {
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) return false;

        await process.WaitForExitAsync();
        return process.ExitCode == 0; // 退出码为 0 表示成功
    }
    catch
    {
        return false;
    }
}

public record CrawlerConfig(string VideoId, string Name, int StartEp, int EndEp, int MaxConcurrent, string BaseUrl);