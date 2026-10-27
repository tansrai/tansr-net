using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;

namespace Tansr.Sdk.Windows.Storage;

// 不可变节点由调用者在原事务内保存；本类不缓存跨调用的根或节点。
internal sealed class TerminalPersistenceAuthenticationTree
{
    internal const int MaximumNodeBytes = 512;
    private const int MaximumPathNodes = 257;
    private readonly Func<string, byte[]> _readNode;
    private readonly Action<string, byte[]> _writeNode;

    internal TerminalPersistenceAuthenticationTree(Func<string, byte[]> readNode, Action<string, byte[]> writeNode)
    {
        _readNode = readNode ?? throw new ArgumentNullException(nameof(readNode));
        _writeNode = writeNode ?? throw new ArgumentNullException(nameof(writeNode));
    }

    internal string? Get(string? root, string keyDigest)
    {
        RequireDigest(keyDigest);
        if (root == null) return null;
        var path = new HashSet<string>(StringComparer.Ordinal);
        Node? parent = null; bool right = false; string current = root;
        while (true)
        {
            var node = Read(current, parent, right, path);
            if (node.Value != null) return node.Key == keyDigest ? node.Value : null;
            // 认证的压缩前缀不相容时，该子树不能含有所选键。
            if (Difference(node.Key, keyDigest) < node.Bit) return null;
            right = IsRight(keyDigest, node.Bit); parent = node;
            current = right ? node.Right! : node.Left!;
        }
    }

    internal string Put(string? root, string keyDigest, string valueDigest)
    {
        RequireDigest(keyDigest); RequireDigest(valueDigest);
        var replacement = new Node(keyDigest, valueDigest);
        if (root == null) return Write(replacement);
        var path = new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<Step>();
        Node? parent = null; bool right = false; string current = root;
        string replacementHash;
        while (true)
        {
            var node = Read(current, parent, right, path);
            int different = Difference(node.Key, keyDigest);
            if (node.Value != null && different == 256)
            {
                if (node.Value == valueDigest) return root;
                replacementHash = Write(replacement); break;
            }
            if (node.Value == null && different >= node.Bit)
            {
                right = IsRight(keyDigest, node.Bit); steps.Add(new Step(node, right)); parent = node;
                current = right ? node.Right! : node.Left!; continue;
            }
            string leaf = Write(replacement);
            bool newRight = IsRight(keyDigest, different);
            replacement = new Node(newRight ? node.Key : keyDigest, different, newRight ? current : leaf, newRight ? leaf : current);
            replacementHash = Write(replacement); break;
        }
        for (int index = steps.Count - 1; index >= 0; index--)
        {
            var step = steps[index];
            replacement = new Node(step.Right ? step.Node.Key : replacement.Key, step.Node.Bit,
                step.Right ? step.Node.Left! : replacementHash, step.Right ? replacementHash : step.Node.Right!);
            replacementHash = Write(replacement);
        }
        return replacementHash;
    }

    // 显式全审计使用；只保留当前路径，内存不随永久叶子总数增长。
    internal IEnumerable<KeyValuePair<string, string>> Entries(string? root)
    {
        if (root == null) yield break;
        foreach (var entry in Visit(root, null, false, new HashSet<string>(StringComparer.Ordinal))) yield return entry;
    }

    private IEnumerable<KeyValuePair<string, string>> Visit(string hash, Node? parent, bool right, HashSet<string> path)
    {
        var node = Read(hash, parent, right, path);
        try
        {
            if (node.Value != null) yield return new KeyValuePair<string, string>(node.Key, node.Value);
            else
            {
                foreach (var entry in Visit(node.Left!, node, false, path)) yield return entry;
                foreach (var entry in Visit(node.Right!, node, true, path)) yield return entry;
            }
        }
        finally { path.Remove(hash); }
    }

    private Node Read(string hash, Node? parent, bool right, HashSet<string> path)
    {
        RequireDigest(hash); Require(path.Count < MaximumPathNodes && path.Add(hash));
        byte[] bytes = _readNode(hash);
        Require(bytes != null && bytes.Length > 0 && bytes.Length <= MaximumNodeBytes);
        Require(WireJson.Sha256(bytes!) == hash);
        Node node;
        try
        {
            var value = WireJson.DecodeControl(bytes!, MaximumNodeBytes);
            Require(value.ValueKind == JsonValueKind.Object);
            string? kind = value.GetProperty("kind").GetString();
            if (kind == "leaf")
            {
                Fields(value, "kind", "key", "value");
                node = new Node(Digest(value, "key"), Digest(value, "value"));
            }
            else
            {
                Require(kind == "branch"); Fields(value, "kind", "key", "bit", "left", "right");
                int bit = value.GetProperty("bit").GetInt32(); Require(bit >= 0 && bit <= 255);
                node = new Node(Digest(value, "key"), bit, Digest(value, "left"), Digest(value, "right"));
                Require(node.Left != node.Right);
            }
        }
        catch (Exception error) when (error is WireProtocolException || error is InvalidOperationException ||
            error is FormatException || error is OverflowException || error is KeyNotFoundException)
        { throw new StorageException("integrity_mismatch"); }
        if (parent != null)
        {
            Require(node.Value != null || node.Bit > parent.Bit);
            Require(Difference(node.Key, parent.Key) >= parent.Bit && IsRight(node.Key, parent.Bit) == right);
        }
        return node;
    }

    private string Write(Node node)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteString("kind", node.Value == null ? "branch" : "leaf"); writer.WriteString("key", node.Key);
            if (node.Value != null) writer.WriteString("value", node.Value);
            else { writer.WriteNumber("bit", node.Bit); writer.WriteString("left", node.Left); writer.WriteString("right", node.Right); }
            writer.WriteEndObject();
        }
        var bytes = WireJson.EncodeControl(WireJson.Parse(memory.ToArray(), MaximumNodeBytes), MaximumNodeBytes);
        string hash = WireJson.Sha256(bytes); _writeNode(hash, bytes); return hash;
    }

    private static int Difference(string left, string right)
    {
        for (int index = 0; index < 64; index++)
        {
            int different = Hex(left[index]) ^ Hex(right[index]);
            if (different == 0) continue;
            for (int bit = 0; bit < 4; bit++) if ((different & (8 >> bit)) != 0) return index * 4 + bit;
        }
        return 256;
    }
    private static bool IsRight(string key, int bit) => (Hex(key[bit / 4]) & (8 >> (bit % 4))) != 0;
    private static int Hex(char value) => value <= '9' ? value - '0' : value - 'a' + 10;
    private static string Digest(JsonElement value, string name)
    { string? digest = value.GetProperty(name).GetString(); RequireDigest(digest); return digest!; }
    private static void RequireDigest(string? digest)
        => Require(digest != null && digest.Length == 64 && digest.All(value => value >= '0' && value <= '9' || value >= 'a' && value <= 'f'));
    private static void Fields(JsonElement value, params string[] names)
        => Require(value.EnumerateObject().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)));
    private static void Require(bool condition) { if (!condition) throw new StorageException("integrity_mismatch"); }

    private sealed class Node
    {
        internal readonly string Key;
        internal readonly string? Value, Left, Right;
        internal readonly int Bit = -1;
        internal Node(string key, string value) { Key = key; Value = value; }
        internal Node(string key, int bit, string left, string right) { Key = key; Bit = bit; Left = left; Right = right; }
    }
    private sealed class Step
    {
        internal readonly Node Node;
        internal readonly bool Right;
        internal Step(Node node, bool right) { Node = node; Right = right; }
    }
}
