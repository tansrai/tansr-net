using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Terminal;
using Tansr.Sdk.Tests.Execution;

namespace Tansr.Sdk.Tests.Terminal;

public sealed class TerminalCandidateFourTests
{
    public static IEnumerable<object[]> AddedStructuralVectors() => TerminalCandidateContractTests.StructuralVectors()
        .Where(row => ((string)row[1]).StartsWith("Memory", StringComparison.Ordinal) || ((string)row[1]).StartsWith("Background", StringComparison.Ordinal) || (string)row[1] == "ExecutionState");
    [Theory]
    [MemberData(nameof(AddedStructuralVectors))]
    public void ServeOwnedCandidateAdditionsPreservePinnedStructuralVectors(string id, string definition, string json, bool valid)
    {
        Assert.NotEmpty(id); using var value = JsonDocument.Parse(json);
        if (valid) TerminalCandidateContract.ValidateStructure(definition, value.RootElement);
        else Assert.Throws<WireProtocolException>(() => TerminalCandidateContract.ValidateStructure(definition, value.RootElement));
    }
    [Theory]
    [InlineData("completed", false, false)]
    [InlineData("pending", true, false)]
    [InlineData("completed", true, true)]
    public async Task NarrowExecutionReadCannotInventCompletionOrReplaceOriginalOperation(string status, bool receipt, bool foreign)
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        var operation = foreign ? ExecutionFixture.Operation(id: "foreign-operation") : adapter.ExistingOperation;
        var execution = ExecutionFixture.Set(ExecutionFixture.Status(operation, receipt ? ExecutionFixture.Receipt(operation) : null), "status", JsonSerializer.SerializeToElement(status));
        adapter.ExecutionState = JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", session = bound.Binding.Session, execution });
        await Assert.ThrowsAsync<WireProtocolException>(() => bound.Client.GetExecutionStateAsync(bound.Binding, adapter.ExistingOperation));
    }
    [Fact]
    public async Task NarrowExecutionReadPreservesOriginalUnknownAsUnknown()
    {
        var adapter = new TerminalTestAdapter(); var bound = await adapter.BindAsync();
        var execution = ExecutionFixture.Status(adapter.ExistingOperation, ExecutionFixture.Receipt(adapter.ExistingOperation, "unknown"));
        adapter.ExecutionState = JsonSerializer.SerializeToElement(new { contract = "terminal-services-v1", session = bound.Binding.Session, execution });
        var value = await bound.Client.GetExecutionStateAsync(bound.Binding, adapter.ExistingOperation);
        Assert.Equal("unknown", value.GetProperty("execution").GetProperty("status").GetString());
    }
}
