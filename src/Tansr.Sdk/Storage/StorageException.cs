namespace Tansr.Sdk.Storage;

/// <summary>本地存储错误，不携带正文、密钥、路径或原始异常消息。</summary>
public sealed class StorageException : Exception
{
    public string Code { get; }
    public StorageException(string code) : base("Tansr storage: " + code) => Code = code;
}
