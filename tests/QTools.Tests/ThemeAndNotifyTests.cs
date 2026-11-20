using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Cli;
using QTools.Host.Configuration;
using QTools.Host.Launcher;
using QTools.Host.Serialization;
using QTools.Host.Server;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>界面主题（system / light / dark）与系统通知：配置持久化、HTML 注入、桥接校验。</summary>
[Collection("AI 配置串行")] // 与其它改写 AppPaths/config.json 的测试串行，避免静态路径互相覆盖
public sealed class ThemeAndNotifyTests : IDisposable
{
    private readonly string _root;
    private readonly ToolRegistry _tools;
    private readonly BridgeDispatcher _dispatcher;

    public ThemeAndNotifyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qtools-theme-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
        AppPaths.Initialize(_root);

        _tools = new ToolRegistry(Path.Combine(_root, "tools"));
        _tools.Rescan();
        _dispatcher = new BridgeDispatcher(_tools, new ToolStorage(Path.Combine(_root, "data", "storage")));
    }

    private void WriteTool(string id)
    {
        var directory = Path.Combine(_root, "tools", id);
        Directory.CreateDirectory(directory);
        var manifest = new ToolManifest(
            id, id, "1.0.0", null, "index.html",
            Array.Empty<string>(),
            new ToolWindowSettings(640, 480, true),
            "2026-01-01T00:00:00.0000000+00:00",
            AllowEval: false, Csp: null);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, AppJsonContext.Default.ToolManifest));
        File.WriteAllText(Path.Combine(directory, "index.html"), "<h1>probe</h1>");
        _tools.Rescan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 忽略 */ }
    }

    private async Task<JsonElement> DispatchAsync(string method, object payload, string toolId = BridgeDispatcher.HostToolId)
    {
        var envelope = new
        {
            id = "req-" + Guid.NewGuid().ToString("n")[..6],
            method,
            toolId,
            payload = JsonSerializer.SerializeToElement(payload)
        };
        var raw = await _dispatcher.DispatchAsync(toolId, JsonSerializer.Serialize(envelope));
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string ErrorCode(JsonElement response)
    {
        Assert.True(response.TryGetProperty("error", out var error), "响应应当是错误信封");
        return error.GetProperty("code").GetString()!;
    }

    // ---------------- 主题取值与 HTML 注入 ----------------

    [Fact]
    public void ThemeSettingNormalizesAndOnlyExposesExplicitValues()
    {
        Assert.Equal("system", ThemeSetting.Normalize(null));
        Assert.Equal("system", ThemeSetting.Normalize(""));
        Assert.Equal("system", ThemeSetting.Normalize("DARK-ish"));
        Assert.Equal("light", ThemeSetting.Normalize("light"));
        Assert.Equal("dark", ThemeSetting.Normalize("dark"));

        Assert.Null(ThemeSetting.Explicit("system"));
        Assert.Null(ThemeSetting.Explicit(null));
        Assert.Equal("light", ThemeSetting.Explicit("light"));
        Assert.Equal("dark", ThemeSetting.Explicit("dark"));

        Assert.True(ThemeSetting.IsDarkNow("dark"));
        Assert.False(ThemeSetting.IsDarkNow("light"));
        Assert.Equal(ThemeSetting.IsSystemDark(), ThemeSetting.IsDarkNow("system"));
    }

    [Fact]
    public void InjectThemeAttributeAddsReplacesAndIgnoresSystem()
    {
        var plain = "<!DOCTYPE html><html lang=\"zh-CN\"><head></head><body></body></html>";
        Assert.Equal(
            "<!DOCTYPE html><html lang=\"zh-CN\" data-theme=\"dark\"><head></head><body></body></html>",
            HtmlRewriter.InjectThemeAttribute(plain, "dark"));

        var withAttr = "<html data-theme=\"light\"><body></body></html>";
        Assert.Equal(
            "<html data-theme=\"dark\"><body></body></html>",
            HtmlRewriter.InjectThemeAttribute(withAttr, "dark"));

        Assert.Same(withAttr, HtmlRewriter.InjectThemeAttribute(withAttr, null));
        Assert.Same(withAttr, HtmlRewriter.InjectThemeAttribute(withAttr, "system"));
        Assert.Equal("<div>无 html 标签</div>", HtmlRewriter.InjectThemeAttribute("<div>无 html 标签</div>", "dark"));
    }

    // ---------------- 桥接：getTheme / setTheme ----------------

    [Fact]
    public async Task GetThemeDefaultsToSystemAndSetThemePersists()
    {
        var initial = await DispatchAsync("manager.getTheme", new { });
        Assert.Equal("system", initial.GetProperty("result").GetProperty("theme").GetString());
        Assert.Equal(
            ThemeSetting.IsSystemDark(),
            initial.GetProperty("result").GetProperty("dark").GetBoolean());

        var saved = await DispatchAsync("manager.setTheme", new { theme = "dark" });
        Assert.Equal("dark", saved.GetProperty("result").GetProperty("theme").GetString());
        Assert.True(saved.GetProperty("result").GetProperty("dark").GetBoolean());

        var config = QTools.Host.Ai.ConfigStore.Load(AppPaths.ConfigFilePath);
        Assert.Equal("dark", config.Shell?.Theme);

        var reloaded = await DispatchAsync("manager.getTheme", new { });
        Assert.Equal("dark", reloaded.GetProperty("result").GetProperty("theme").GetString());

        await DispatchAsync("manager.setTheme", new { theme = "system" });
        Assert.Equal("system", QTools.Host.Ai.ConfigStore.Load(AppPaths.ConfigFilePath).Shell?.Theme);
    }

    [Fact]
    public async Task ThemeGetIsReadableFromToolPages()
    {
        WriteTool("themeprobe");
        await DispatchAsync("manager.setTheme", new { theme = "dark" });

        var dark = await DispatchAsync("theme.get", new { }, "themeprobe");
        Assert.Equal("dark", dark.GetProperty("result").GetProperty("theme").GetString());
        Assert.True(dark.GetProperty("result").GetProperty("dark").GetBoolean());

        await DispatchAsync("manager.setTheme", new { theme = "light" });
        var light = await DispatchAsync("theme.get", new { }, "themeprobe");
        Assert.False(light.GetProperty("result").GetProperty("dark").GetBoolean());

        // 写操作仍然只有宿主页面可用
        var denied = await DispatchAsync("manager.setTheme", new { theme = "dark" }, "themeprobe");
        Assert.Equal("PERMISSION_DENIED", ErrorCode(denied));
    }

    [Fact]
    public void InjectToolThemeMarksLegacyAndThemedPages()
    {
        const string legacy =
            "<!DOCTYPE html><html lang=\"zh-CN\"><head><title>x</title></head><body>hi</body></html>";

        var legacyDark = HtmlRewriter.InjectToolTheme(legacy, "dark", dark: true);
        Assert.Contains("data-qtools-theme=\"dark\"", legacyDark);
        Assert.Contains("data-qtools-themed=\"0\"", legacyDark);
        Assert.Contains("data-qtools-auto-dark=\"1\"", legacyDark);
        Assert.Contains("data-theme=\"dark\"", legacyDark);
        Assert.Contains("/__host__/tool-theme.css", legacyDark);
        Assert.Contains("/__host__/tool-theme.js", legacyDark);

        var legacyLight = HtmlRewriter.InjectToolTheme(legacy, "light", dark: false);
        Assert.DoesNotContain("data-qtools-auto-dark", legacyLight);
        Assert.Contains("data-theme=\"light\"", legacyLight);

        // 跟随系统 + 系统深色：不写死 data-theme，只加反色兜底
        var legacySystem = HtmlRewriter.InjectToolTheme(legacy, "system", dark: true);
        Assert.Contains("data-qtools-auto-dark=\"1\"", legacySystem);
        Assert.DoesNotContain("<html data-theme", legacySystem);

        // 自带主题能力的页面：只切换 data-theme，不做反色
        const string themed =
            "<html data-theme='light'><head><style>:root{color-scheme:light dark}</style></head><body></body></html>";
        var themedDark = HtmlRewriter.InjectToolTheme(themed, "dark", dark: true);
        Assert.Contains("data-qtools-themed=\"1\"", themedDark);
        Assert.DoesNotContain("data-qtools-auto-dark", themedDark);
        Assert.Contains("data-theme='dark'", themedDark);
        Assert.DoesNotContain("data-theme='light'", themedDark);
    }

    [Fact]
    public async Task SetThemeRejectsInvalidValueAndIsHostOnly()
    {
        var invalid = await DispatchAsync("manager.setTheme", new { theme = "blue" });
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(invalid));

        var missing = await DispatchAsync("manager.setTheme", new { });
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(missing));

        var denied = await DispatchAsync("manager.setTheme", new { theme = "dark" }, "themeprobe");
        Assert.Equal("PERMISSION_DENIED", ErrorCode(denied));

        Assert.NotEqual("dark", QTools.Host.Ai.ConfigStore.Load(AppPaths.ConfigFilePath).Shell?.Theme);
    }

    // ---------------- 桥接：notify ----------------

    [Fact]
    public async Task NotifyValidatesPayloadAndReportsUnavailableWithoutTray()
    {
        var emptyTitle = await DispatchAsync("manager.notify", new { title = "  " });
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(emptyTitle));

        var tooLong = await DispatchAsync("manager.notify", new { title = new string('x', 65) });
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(tooLong));

        var badLevel = await DispatchAsync("manager.notify", new { title = "提醒", level = "loud" });
        Assert.Equal("BRIDGE_BAD_REQUEST", ErrorCode(badLevel));

        // 无宿主壳（无头 / 非 Windows）时明确报不可用，而不是静默成功
        var unavailable = await DispatchAsync("manager.notify", new { title = "提醒", body = "喝水" });
        Assert.Equal("NOTIFY_UNAVAILABLE", ErrorCode(unavailable));

        var denied = await DispatchAsync("manager.notify", new { title = "提醒" }, "some-tool");
        Assert.Equal("PERMISSION_DENIED", ErrorCode(denied));
    }

    // ---------------- 单实例消息与 CLI ----------------

    [Fact]
    public void SingleInstanceMessageRoundTripsThroughSourceGeneratedJson()
    {
        var message = new SingleInstanceMessage(
            SingleInstanceMessage.NotifyCommand, Title: "构建完成", Body: "第一行\n第二行");
        var line = JsonSerializer.Serialize(message, AppJsonContext.Default.SingleInstanceMessage);
        Assert.DoesNotContain('\n', line);

        var back = JsonSerializer.Deserialize(line, AppJsonContext.Default.SingleInstanceMessage);
        Assert.NotNull(back);
        Assert.Equal("notify", back!.Cmd);
        Assert.Null(back.Id);
        Assert.Equal("构建完成", back.Title);
        Assert.Equal("第一行\n第二行", back.Body);
    }

    [Fact]
    public void CommandLineParsesNotifyTitleAndOptionalBody()
    {
        var options = CommandLine.Parse(new[] { "--notify", "  构建完成  ", "耗时 42 秒" });
        Assert.Equal("构建完成", options.NotifyTitle);
        Assert.Equal("耗时 42 秒", options.NotifyBody);

        var titleOnly = CommandLine.Parse(new[] { "--notify", "提醒", "--dev" });
        Assert.Equal("提醒", titleOnly.NotifyTitle);
        Assert.Null(titleOnly.NotifyBody);
        Assert.True(titleOnly.DevMode);

        Assert.Throws<FormatException>(() => CommandLine.Parse(new[] { "--notify" }));
        Assert.Throws<FormatException>(() => CommandLine.Parse(new[] { "--notify", "  " }));
        Assert.Throws<FormatException>(() => CommandLine.Parse(new[] { "--notify", new string('x', 65) }));
        Assert.Throws<FormatException>(() => CommandLine.Parse(new[] { "--notify", "提醒", new string('x', 257) }));
    }
}
