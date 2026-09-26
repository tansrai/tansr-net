using System.IO;

namespace Tansr.Examples;

internal static class BoundedFiles
{
    internal static async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token = default)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, maximum + 1L - output.Length), token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > maximum) throw new InvalidOperationException("selected_file_too_large");
        }
    }
}
