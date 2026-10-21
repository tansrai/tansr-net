namespace Tansr.Sdk.Protocol;

/// <summary>已锁原合同；不代表未会签的 terminal-services-v1 已支持。</summary>
public static class WireContract
{
    public const string Protocol = "sdk2-ext-v1";
    public const string SchemaSha256 = "4c13275462d9f71fca7af32b46d8e70e05ac1fb1efc23d98484caf9236f6acc9";
    /// <summary>原字节与存储互操作的基线。后续会签增量按 contract/manifest.json 的逐文件 sourceRevision 锁定。</summary>
    public const string SourceRevision = "027de7e2d9b647374b7fe94cb0e4a7429a9195e2";
}
