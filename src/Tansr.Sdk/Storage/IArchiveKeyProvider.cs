namespace Tansr.Sdk.Storage;

/// <summary>可信宿主提供的档案密钥；返回独立的 32 字节副本，由消费者在操作结束时清零。</summary>
public interface IArchiveKeyProvider
{
    string KeyId { get; }
    byte[] ReadKey();
}
