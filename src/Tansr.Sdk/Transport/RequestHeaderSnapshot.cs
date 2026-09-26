using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tansr.Sdk.Transport;

// 仅可信应用装配可选的第二层认证；不能覆盖 SDK 管理的传输头或更改请求地址。
internal static class RequestHeaderSnapshot
{
    internal static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bytes = 0;
        if (source != null)
        {
            if (source.Count > 16) throw Invalid();
            foreach (var entry in source)
            {
                var name = entry.Key; var value = entry.Value;
                if (name == null || name.Length < 3 || name.Length > 64 || !name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase) || name.Equals("x-real-ip", StringComparison.OrdinalIgnoreCase) ||
                    value == null || value.Length == 0 || value.Length > 4096 || copy.ContainsKey(name)) throw Invalid();
                foreach (var c in name) if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) throw Invalid();
                foreach (var c in value) if (c < 32 || c > 126) throw Invalid();
                bytes += name.Length + value.Length;
                if (bytes > 16384) throw Invalid();
                copy.Add(name, value);
            }
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static ArgumentException Invalid() => new("Invalid additional request headers.", "AdditionalRequestHeaders");
}
