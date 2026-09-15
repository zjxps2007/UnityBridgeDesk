using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class SpeedFiles
{
    public static async Task<string> Hash(string path, CancellationToken ct = default)
    {
        Regular(path); await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    public static void Regular(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크된 경로는 시험 입력으로 사용할 수 없습니다.");
    }
    public static string Child(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("압축 경로가 올바르지 않습니다.");
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("시험 폴더를 벗어난 경로입니다.");
        Regular(full); return full;
    }
    public static async Task CopyBounded(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        byte[] buffer = new byte[65536]; long count = 0; int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            count += n; if (count > limit) throw new IOException("파일 또는 출력 크기 제한을 넘었습니다.");
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }
    public static async Task Extract(string archive, string destination, string prefix = "", CancellationToken ct = default)
    {
        Regular(archive); Regular(destination); using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = entry.FullName[prefix.Length..].TrimEnd('/'); if (relative.Length == 0) continue;
            if (!seen.Add(relative) || seen.Count > 15000 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (total += entry.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("지원하지 않는 압축 내용입니다.");
            string path = Child(destination, relative);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open(); await using var output = new FileStream(path, FileMode.CreateNew);
            await CopyBounded(input, output, entry.Length, ct);
        }
    }
    public static async Task Write<T>(string path, T value, CancellationToken ct = default)
    {
        Regular(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, SpeedProtocol.Json), ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<T> Read<T>(string path, CancellationToken ct = default)
    {
        Regular(path); if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("결과 파일이 너무 큽니다.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), SpeedProtocol.Json) ?? throw new InvalidDataException("빈 자료입니다.");
    }
}
