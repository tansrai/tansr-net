using System.Net;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Api;
using Tansr.Sdk.Client;
using Tansr.Sdk.Tests.Api;

namespace Tansr.Sdk.Windows.Tests.Hosting;

public sealed class FamilyFactsTests
{
    [Fact]
    public void HumanDiagnosticsKeepDomainAndTraceWithoutCopyingOpenDetailOrServerMessage()
    {
        var error = Decode("{\"domainCode\":\"resume_unavailable\",\"domainStatus\":409,\"reason\":\"receipt_not_retained\",\"privateValue\":\"synthetic-secret\"}");
        var text = FamilyFacts.Display(error);
        Assert.Equal("conflict | domainCode=resume_unavailable | reason=receipt_not_retained | traceId=manual-trace", text);
        Assert.DoesNotContain("synthetic-secret", text);
        Assert.DoesNotContain("private-message", text);
        Assert.DoesNotContain("private-request", text);
        Assert.Equal("conflict", error.Code);
    }

    [Fact]
    public void MissingOptionalDetailsDoNotInventADomainOrReason()
    {
        Assert.Equal("conflict | traceId=manual-trace", FamilyFacts.Display(Decode(null)));
        Assert.Equal("network_error", FamilyFacts.Display(new TansrProtocolException("network_error")));
    }

    private static UnifiedApiException Decode(string? detail)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict);
        UnifiedStamp.Apply(response);
        response.Headers.TryAddWithoutValidation("x-request-id", "manual-trace");
        using var document = JsonDocument.Parse("{\"contract\":\"unified-v1\",\"traceId\":\"manual-trace\",\"requestId\":\"private-request\",\"code\":\"conflict\",\"status\":409,\"retryAction\":\"none\",\"message\":\"private-message\"" +
            (detail == null ? "" : ",\"detail\":" + detail) + "}");
        return Assert.Throws<UnifiedApiException>(() => UnifiedErrorEnvelope.TryThrow(response, document.RootElement, UnifiedResponseMeta.Read(response)));
    }
}
