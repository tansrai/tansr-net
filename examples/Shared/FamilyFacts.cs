using Tansr.Sdk.Api;
using Tansr.Sdk.Client;

namespace Tansr.Examples;

// D19（UAPI-01）：/api 失败的公开面是统一码 + RetryAction，先按 error.Code 分支；这里的几个流程（inputs 回执、工具回执、
// 配置提交）跟随各自族的状态机，需要族事实时从 UnifiedApiException.Detail 读（无族事实为 null，与 Node ApiError.domainCode 同形）；
// 门面未包装的族直通信封（夹具与残余路径）族事实即其自身 Code / StatusCode / RetryAction。不再使用已弃用的 Domain* 桥接。
internal static class FamilyFacts
{
    internal static string? Code(TansrHttpException error) => error is UnifiedApiException unified ? unified.Detail.DomainCode : error.Code;
    internal static int? Status(TansrHttpException error) => error is UnifiedApiException unified ? unified.Detail.DomainStatus : error.StatusCode;
    // domainRetryAction 在场取其值（可为 null）；缺席时统一 retryAction 即族动作的映射值。
    internal static string? RetryAction(TansrHttpException error) =>
        error is UnifiedApiException unified ? (unified.Detail.HasDomainRetryAction ? unified.Detail.DomainRetryAction : unified.RetryAction) : error.RetryAction;
}
