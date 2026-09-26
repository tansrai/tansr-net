using System.Globalization;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Sessions;

namespace Tansr.Examples;

// 应用宿主配置只是请求；实际模型、工具、技能和扩展范围仍由 Serve 的可信 profile 决定。
internal static class ExampleSessionOptions
{
    internal static CreateSessionOptions Create(string? model, string? resume, JsonElement? clientTools,
        Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;
        var options = new CreateSessionOptions
        {
            Model = Empty(model),
            ResumeSessionId = Empty(resume),
            ClientTools = Empty(resume) == null ? clientTools : null,
            Profile = Empty(read("TANSR_PROFILE")),
            CapabilitiesProfile = Empty(read("TANSR_CAPABILITIES_PROFILE")),
            Cwd = Empty(read("TANSR_SESSION_CWD")),
            ThinkingBudget = Integer(read("TANSR_THINKING_BUDGET"), "TANSR_THINKING_BUDGET", true),
            MaxTokens = Integer(read("TANSR_MAX_TOKENS"), "TANSR_MAX_TOKENS", true),
        };
        if (ReadContract(read) == SessionContract.Sdk2OffloadV1 && options.ResumeSessionId == null)
            options.RequestId = Empty(read("TANSR_SESSION_REQUEST_ID")) ?? throw new InvalidOperationException("sdk2_requires_original_TANSR_SESSION_REQUEST_ID");
        var maxUsd = Empty(read("TANSR_MAX_USD"));
        if (maxUsd != null)
        {
            if (!decimal.TryParse(maxUsd, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var usd) || usd <= 0)
                throw new InvalidOperationException("invalid_TANSR_MAX_USD");
            options.MaxUsd = usd;
        }
        return options;
    }
    internal static SessionContract ReadContract(Func<string, string?>? environment = null)
    {
        var value = Empty((environment ?? Environment.GetEnvironmentVariable)("TANSR_SESSION_CONTRACT"));
        return value == null || value == "sdk1" ? SessionContract.Sdk1 : value == "sdk2-offload-v1" ? SessionContract.Sdk2OffloadV1 : throw new InvalidOperationException("invalid_TANSR_SESSION_CONTRACT");
    }
    internal static IReadOnlyList<string> ReadLocalHostModuleArguments(Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;
        var module = Empty(read("TANSR_LOCAL_SERVE_HOST_MODULE"));
        var digest = Empty(read("TANSR_LOCAL_SERVE_HOST_MODULE_SHA256"));
        if (module == null && digest == null) return Array.Empty<string>();
        if (module == null || digest == null) throw new InvalidOperationException("local_serve_host_module_requires_path_and_sha256");
        if (!System.IO.Path.IsPathRooted(module) ||
            !string.Equals(System.IO.Path.GetFullPath(module), module, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(System.IO.Path.GetExtension(module), ".cjs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("local_serve_host_module_requires_absolute_cjs_path");
        if (digest.Length != 64 || digest.Any(value => !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F'))))
            throw new InvalidOperationException("local_serve_host_module_requires_sha256");
        // Developer-owned startup configuration only; never populated from a model/tool request.
        return new[] { "--host-module", module, "--host-module-sha256", digest.ToLowerInvariant() };
    }
    private static long? Integer(string? text, string name, bool positive)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < (positive ? 1 : 0))
            throw new InvalidOperationException("invalid_" + name);
        return value;
    }
    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}
