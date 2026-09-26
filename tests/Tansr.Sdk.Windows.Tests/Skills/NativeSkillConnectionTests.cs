using System.Text;
using System.Text.Json;
using Tansr.Examples;
using Tansr.Sdk.Windows.Skills;

namespace Tansr.Sdk.Windows.Tests.Skills;

[Collection("Native MCP published candidate")]
public sealed class NativeSkillConnectionTests
{
    [Fact]
    public async Task SharedExampleLoadsBothExplicitSkillSourcesAndRejectsChangedOrRevokedMaterial()
    {
        string[] names = ["TANSR_SKILL_INLINE", "TANSR_SKILL_DIRECTORY", "TANSR_SKILL_FILE"];
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var directory = Path.Combine(Path.GetTempPath(), "tansr-native-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); NativeSkillConnection? connection = null;
        try
        {
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "LOCAL_SKILL_中文", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable(names[0], "INLINE_SKILL_🙂"); Environment.SetEnvironmentVariable(names[1], directory); Environment.SetEnvironmentVariable(names[2], null);
            connection = await NativeSkillConnection.OpenConfiguredAsync(CancellationToken.None); Assert.NotNull(connection);
            var binding = Assert.Single(connection.Bindings); Assert.True(binding.Declaration.GetProperty("readOnly").GetBoolean());
            async Task<string> Read(string name) => (await binding.ExecuteAsync(JsonSerializer.SerializeToElement(new { name }), CancellationToken.None))
                .GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Equal("INLINE_SKILL_🙂", await Read("inline-guide")); Assert.Equal("LOCAL_SKILL_中文", await Read("device-guide"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Read("../../unapproved"));
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), "changed", new UTF8Encoding(false));
            Assert.Equal("resource_changed", (await Assert.ThrowsAsync<WindowsSkillException>(() => Read("device-guide"))).Code);
            await connection.RevokeAsync();
            Assert.Equal("trust_revoked", (await Assert.ThrowsAsync<WindowsSkillException>(() => Read("inline-guide"))).Code);
        }
        finally
        {
            try { if (connection != null) await connection.CloseAsync(); }
            finally { for (int index = 0; index < names.Length; index++) Environment.SetEnvironmentVariable(names[index], previous[index]); Directory.Delete(directory, true); }
        }
    }
}
