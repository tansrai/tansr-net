using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Tansr.Sdk.Protocol;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Storage;

namespace Tansr.Sdk.Windows.Tests.Storage;

public sealed class TerminalPersistenceAuthenticationTreeTests
{
    [Fact]
    public void CommonPatriciaVectorsMatchNodeAndAllOtherMedia()
    {
        var tree = new Tree(); string zero = new('0', 64), quarter = "4" + new string('0', 63), half = "8" + new string('0', 63);
        string first = tree.Put(null, zero, Digest("zero")); Assert.Equal("b675b073fdc915247ebc9249e0b900fda256000dee06702341167da1a1a8b0e7", first);
        string second = tree.Put(first, quarter, Digest("quarter")); Assert.Equal("a1e56ed0d0361fa6bd2ca2d26618bab9f85fb94069cacdc89de569ebc6f7e7ae", second);
        string third = tree.Put(second, half, Digest("half")); Assert.Equal("9174d8e3d90006b200750ea1392b0e51f937ae183f95e4b350e134d76d30deac", third);
        int writes = tree.Writes; Assert.Equal(third, tree.Put(third, quarter, Digest("quarter"))); Assert.Equal(writes, tree.Writes);
        Assert.Equal("8f0cdd52401026ed9debcdaa9666535c51e01fed45aaedb224227d130b791107", tree.Put(third, quarter, Digest("quarter-updated")));
    }

    [Fact]
    public void InsertUpdateAndAbsencePreserveOriginalRootsAndNoOpWrites()
    {
        var tree = new Tree(); var expected = new SortedDictionary<string, string>(StringComparer.Ordinal); string? root = null;
        Assert.Null(tree.Get(null, Digest("absent"))); Assert.Empty(tree.Entries(null));
        for (int index = 0; index < 64; index++)
        {
            string key = Digest("key-" + index), value = Digest("value-" + index);
            root = tree.Put(root, key, value); expected.Add(key, value);
        }
        Assert.Equal(expected.ToArray(), tree.Entries(root));
        foreach (var entry in expected) Assert.Equal(entry.Value, tree.Get(root, entry.Key));
        Assert.Null(tree.Get(root, Digest("absent")));
        var selected = expected.First(); int writes = tree.Writes;
        Assert.Equal(root, tree.Put(root, selected.Key, selected.Value)); Assert.Equal(writes, tree.Writes);
        string changed = tree.Put(root, selected.Key, Digest("changed"));
        Assert.Equal(selected.Value, tree.Get(root, selected.Key)); Assert.Equal(Digest("changed"), tree.Get(changed, selected.Key));
        Assert.NotEqual(root, changed);
    }

    [Fact]
    public void CompressedAbsenceAndSelectedReadDoNotInspectOtherSubtrees()
    {
        var tree = new Tree(); string zero = new('0', 64), low = "4" + new string('0', 63), high = "8" + new string('0', 63);
        string narrow = tree.Put(tree.Put(null, zero, Digest("zero")), low, Digest("low"));
        tree.Reads = 0; Assert.Null(tree.Get(narrow, high)); Assert.Equal(1, tree.Reads);
        string root = tree.Put(narrow, high, Digest("high"));
        var descriptor = WireJson.DecodeControl(tree.Nodes[root]); string untouched = descriptor.GetProperty("right").GetString()!;
        tree.Nodes[untouched][0] ^= 1;
        Assert.Equal(Digest("zero"), tree.Get(root, zero));
        AssertIntegrity(() => tree.Get(root, high)); AssertIntegrity(() => tree.Entries(root));
        AssertIntegrity(() => tree.Put(root, high, Digest("replacement")));
    }

    [Fact]
    public void FullWidthPathHasAtMost257ReadsAndUpdatesWithoutScanningLeaves()
    {
        var tree = new Tree(); string zero = new('0', 64); string root = tree.Put(null, zero, Digest("zero"));
        for (int bit = 255; bit >= 0; bit--) root = tree.Put(root, SingleBit(bit), Digest("bit-" + bit));
        tree.Reads = 0; Assert.Equal(Digest("zero"), tree.Get(root, zero)); Assert.Equal(257, tree.Reads);
        tree.Reads = 0; int writes = tree.Writes;
        Assert.Equal(root, tree.Put(root, zero, Digest("zero"))); Assert.Equal(257, tree.Reads); Assert.Equal(writes, tree.Writes);
        tree.Reads = 0; string changed = tree.Put(root, zero, Digest("new-zero")); Assert.Equal(257, tree.Reads);
        Assert.Equal(Digest("new-zero"), tree.Get(changed, zero)); Assert.Equal(Digest("zero"), tree.Get(root, zero));
        Assert.Equal(257, tree.Entries(changed).Length);
    }

    [Fact]
    public void RejectsWrongHashNoncanonicalShapeOversizeAndInvalidDigests()
    {
        var tree = new Tree(); string key = new('0', 64), value = Digest("value");
        string valid = tree.Put(null, key, value); byte[] original = tree.Nodes[valid].ToArray();
        tree.Nodes[valid][0] ^= 1; AssertIntegrity(() => tree.Get(valid, key)); tree.Nodes[valid] = original;
        foreach (byte[] bytes in new[]
        {
            Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(original)),
            Canon(new { kind = "leaf", key, value, extra = true }),
            Canon(new { kind = "leaf", key, value = "wrong" }),
            Canon(new { kind = "unknown", key, value }),
            Enumerable.Repeat((byte)' ', 513).ToArray(),
            Canon(new { kind = "branch", key, bit = 256, left = valid, right = Digest("right") })
        })
        {
            string hash = tree.Add(bytes); AssertIntegrity(() => tree.Get(hash, key)); AssertIntegrity(() => tree.Put(hash, key, value));
        }
        AssertIntegrity(() => tree.Get("bad-root", key)); AssertIntegrity(() => tree.Get(null, "bad-key"));
        AssertIntegrity(() => tree.Put(null, key, value.ToUpperInvariant()));
    }

    [Fact]
    public void RejectsWrongChildDirectionPrefixAndNonIncreasingBranchBits()
    {
        var tree = new Tree(); string zero = new('0', 64), quarter = "4" + new string('0', 63), half = "8" + new string('0', 63);
        string left = tree.Put(null, zero, Digest("left")), middle = tree.Put(null, quarter, Digest("middle")), right = tree.Put(null, half, Digest("right"));
        string swapped = tree.Add(Canon(new { kind = "branch", key = zero, bit = 0, left = right, right = left }));
        AssertIntegrity(() => tree.Get(swapped, zero)); AssertIntegrity(() => tree.Entries(swapped));
        string wrongPrefix = tree.Add(Canon(new { kind = "branch", key = zero, bit = 1, left = right, right = middle }));
        AssertIntegrity(() => tree.Get(wrongPrefix, zero)); AssertIntegrity(() => tree.Entries(wrongPrefix));
        string child = tree.Add(Canon(new { kind = "branch", key = zero, bit = 0, left, right }));
        string repeatedBit = tree.Add(Canon(new { kind = "branch", key = zero, bit = 0, left = child, right }));
        AssertIntegrity(() => tree.Get(repeatedBit, zero)); AssertIntegrity(() => tree.Put(repeatedBit, zero, Digest("change")));
        string duplicateChild = tree.Add(Canon(new { kind = "branch", key = zero, bit = 0, left, right = left }));
        AssertIntegrity(() => tree.Get(duplicateChild, zero));
        tree.Nodes.Remove(left); AssertIntegrity(() => tree.Get(child, zero));
    }

    private static void AssertIntegrity(Action action) => Assert.Equal("integrity_mismatch", Assert.Throws<StorageException>(action).Code);
    private static string Digest(string text) => WireJson.Sha256(Encoding.UTF8.GetBytes(text));
    private static byte[] Canon(object value) => WireJson.EncodeControl(WireJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string SingleBit(int bit)
    {
        var text = new string('0', 64).ToCharArray(); text[bit / 4] = "0123456789abcdef"[8 >> (bit % 4)]; return new string(text);
    }

    // 独立算法测试通过反射访问 internal 类，不扩大产品公开面或改共享工程文件。
    private sealed class Tree
    {
        private static readonly Type Type = typeof(SqliteTerminalPersistenceStore).Assembly.GetType("Tansr.Sdk.Windows.Storage.TerminalPersistenceAuthenticationTree", true)!;
        private readonly object _tree;
        internal readonly Dictionary<string, byte[]> Nodes = new(StringComparer.Ordinal);
        internal int Reads, Writes;
        internal Tree()
        {
            Func<string, byte[]> read = hash =>
            { Reads++; if (!Nodes.TryGetValue(hash, out var bytes)) throw new StorageException("integrity_mismatch"); return bytes.ToArray(); };
            Action<string, byte[]> write = (hash, bytes) =>
            {
                Writes++; Assert.Equal(hash, WireJson.Sha256(bytes));
                if (Nodes.TryGetValue(hash, out var existing)) Assert.Equal(existing, bytes); else Nodes.Add(hash, bytes.ToArray());
            };
            _tree = Activator.CreateInstance(Type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { read, write }, null)!;
        }
        internal string Add(byte[] bytes) { string hash = WireJson.Sha256(bytes); Nodes[hash] = bytes.ToArray(); return hash; }
        internal string? Get(string? root, string key) => (string?)Call("Get", root, key);
        internal string Put(string? root, string key, string value) => (string)Call("Put", root, key, value)!;
        internal KeyValuePair<string, string>[] Entries(string? root) => ((IEnumerable<KeyValuePair<string, string>>)Call("Entries", root)!).ToArray();
        private object? Call(string method, params object?[] arguments)
        {
            try { return Type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_tree, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
