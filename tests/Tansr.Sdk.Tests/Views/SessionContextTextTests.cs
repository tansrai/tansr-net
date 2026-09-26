using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Sessions;

namespace Tansr.Sdk.Tests.Views;

public sealed class SessionContextTextTests
{
    [Fact]
    public void SelectedActiveBudgetAndObservedUsageRetainTheirSeparateMeanings()
    {
        var text = SessionContextText.FormatContext(Context());
        var selected = Section(text, "下一轮模型", "当前运行模型");
        var active = Section(text, "当前运行模型", "核心上下文预算");
        var budget = Section(text, "核心上下文预算", "最近主调用用量");
        var usage = text.Substring(text.IndexOf("最近主调用用量", StringComparison.Ordinal));
        Assert.Contains("provider=selected-provider / model=selected-model", selected);
        Assert.Contains("选择别名：next-alias", selected);
        Assert.Contains("16384 tokens", selected);
        Assert.Contains("provider=active-provider / model=active-model", active);
        Assert.Contains("8192 tokens", active);
        Assert.DoesNotContain("selected-model", active);
        Assert.Contains("当前输入估算：320 tokens", budget);
        Assert.Contains("剩余预算：5268 tokens", budget);
        Assert.Contains("local_estimate（Serve/core 本地估算，非平台计费用量）", budget);
        Assert.Contains("设置来源：external_manager", budget);
        Assert.DoesNotContain("123456", budget);
        Assert.Contains("provider=observed-provider / model=observed-model", usage);
        Assert.Contains("输入用量：123456 tokens", usage);
        Assert.Contains("缓存读取用量：未知", usage);
        Assert.Contains("独立观测，不代表当前上下文占用", usage);
        Assert.DoesNotContain("命中", text);
    }

    [Fact]
    public void ChangingUsageCannotAlterContextBudgetPresentation()
    {
        var before = SessionContextText.FormatContext(Context(usageInput: 123456));
        var after = SessionContextText.FormatContext(Context(usageInput: 999999));
        Assert.Equal(Section(before, "核心上下文预算", "最近主调用用量"), Section(after, "核心上下文预算", "最近主调用用量"));
        Assert.Contains("输入用量：999999 tokens", after);
        Assert.DoesNotContain("123456", after);
    }

    [Fact]
    public void NullActiveMeansNoRunningTurnButMissingActiveRemainsUnknown()
    {
        using var explicitNull = JsonDocument.Parse("{\"active\":null,\"lastUsage\":null}");
        var text = SessionContextText.FormatContext(explicitNull.RootElement);
        Assert.Contains("无运行轮（active = null）", text);
        Assert.Contains("尚无主调用用量观测（lastUsage = null）", text);
        var absent = SessionContextText.FormatContext(null);
        Assert.DoesNotContain("无运行轮", absent);
        Assert.DoesNotContain("尚无主调用用量观测", absent);
        Assert.Contains("模型：provider=未知 / model=未知", absent);
        Assert.Contains("当前输入估算：未知", absent);
        Assert.Contains("输入用量：未知", absent);
        Assert.DoesNotContain("0 tokens", absent);
        Assert.DoesNotContain("local_estimate", absent);
    }

    [Fact]
    public void UnknownPhysicalWindowAndNegativeRemainingBudgetAreNotReplacedOrClamped()
    {
        using var document = JsonDocument.Parse("""
            {"selected":{"model":{"provider":"p","model":"m"},"alias":null,"contextWindowTokens":8192},
             "budget":{"physicalWindowTokens":null,"effectiveWindowTokens":6000,"estimatedInputTokens":5999,
               "outputReserveTokens":1,"thinkingReserveTokens":0,"remainingTokens":-100,"fits":false,
               "source":"local_estimate","settingsSource":"configured","thresholds":null}}
            """);
        var text = SessionContextText.FormatContext(document.RootElement);
        Assert.Contains("物理窗口：未知；有效窗口：6000 tokens", text);
        Assert.Contains("思考预留：0 tokens", text);
        Assert.Contains("剩余预算：-100 tokens；能否容纳：否", text);
        Assert.Contains("选择别名：无", text);
        Assert.Contains("摘要预留：未知", text);
    }

    [Fact]
    public void MalformedOptionalValuesStayUnknownAndModelNewlinesCannotImpersonateAnotherSection()
    {
        using var document = JsonDocument.Parse("""
            {"selected":{"model":{"provider":"p\n当前运行模型","model":"m"},"contextWindowTokens":"8192"},
             "active":[],"budget":{"estimatedInputTokens":-2,"outputReserveTokens":2.5,
               "remainingTokens":9007199254740992,"fits":"yes","physicalWindowTokens":1e100},
             "sampledAt":9223372036854775807,"lastUsage":{"observedAt":-1,"usage":{"inputTokens":"0"}}}
            """);
        var text = SessionContextText.FormatContext(document.RootElement);
        Assert.Contains("provider=p\\n当前运行模型", text);
        Assert.Contains("已知上下文窗口：未知", text);
        Assert.Contains("当前输入估算：未知", text);
        Assert.Contains("输出预留：未知", text);
        Assert.Contains("剩余预算：未知；能否容纳：未知", text);
        Assert.Contains("采样时间：未知", text);
        Assert.Contains("观测时间：未知", text);
        Assert.Contains("输入用量：未知", text);
    }

    [Fact]
    public void MetadataFormatterUsesContextAndNeverSubstitutesAccumulatedSessionTokens()
    {
        var raw = JsonSerializer.SerializeToElement(new
        {
            sessionId = "session",
            endUserId = "user",
            status = "running",
            live = true,
            lastSeq = 0,
            createdAt = "2026-09-26T00:00:00Z",
            lastActivityAt = "2026-09-26T00:00:00Z",
            context = Context(),
            sessionTokens = 87654321,
            usage = new { inputTokens = 87654321 }
        });
        var text = SessionContextText.Format(new SessionMetadata(raw));
        Assert.StartsWith("会话：session\n状态：running\n实时会话：是", text);
        Assert.Contains("当前输入估算：320 tokens", text);
        Assert.DoesNotContain("87654321", text);
        Assert.Contains("1970-01-01 00:00:01.000 UTC", text);
    }

    private static string Section(string text, string start, string end)
    {
        var offset = text.IndexOf(start, StringComparison.Ordinal);
        return text.Substring(offset, text.IndexOf(end, offset, StringComparison.Ordinal) - offset);
    }
    private static JsonElement Context(long usageInput = 123456) => JsonSerializer.SerializeToElement(new
    {
        selected = new { model = new { provider = "selected-provider", model = "selected-model" }, alias = "next-alias", contextWindowTokens = 16384 },
        active = new { model = new { provider = "active-provider", model = "active-model" }, alias = "active-alias", contextWindowTokens = 8192 },
        budget = new
        {
            physicalWindowTokens = 8192,
            effectiveWindowTokens = 7000,
            estimatedInputTokens = 320,
            outputReserveTokens = 512,
            thinkingReserveTokens = 128,
            remainingTokens = 5268,
            fits = true,
            source = "local_estimate",
            settingsSource = "external_manager",
            thresholds = new { microcompactAt = 4000, warningAt = 4500, autoCompactAt = 5000, blockingAt = 6100, summaryReserveTokens = 200 }
        },
        lastUsage = new { model = new { provider = "observed-provider", model = "observed-model" }, observedAt = 1000, usage = new { inputTokens = usageInput, outputTokens = 7 } },
        sampledAt = 1000,
        history = "live"
    });
}
