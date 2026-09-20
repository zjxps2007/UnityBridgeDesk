using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

public static class CliDistribution
{
    // Same preference as the tag-specific Windows installer. No installer or PATH mutation is needed in the bench store.
    public static readonly string[] AssetNames = ["unity-bridge-windows-amd64.zip", "unity-bridge-windows-amd64.exe", "unity-bridge-windows-x64.exe"];
    public static bool IsBundle(string? url) => url?.EndsWith("/" + AssetNames[0], StringComparison.Ordinal) == true;
    public static bool OfficialAsset(string tag, string? url) => url is not null && AssetNames.Any(name =>
        url == "https://github.com/zjxps2007/UnityBridge/releases/download/" + Uri.EscapeDataString(tag) + "/" + name);
    public static string Description(string? url) => url is null ? "Windows CLI 없음" : IsBundle(url) ? "ZIP · 실행 파일+런타임" : "단일 EXE";
    public static async Task ExtractBundle(string archive, string directory, CancellationToken ct)
    {
        // Validate the whole archive, including entries outside the expected prefix, before extracting.
        using (var zip = ZipFile.OpenRead(archive))
            if (zip.Entries.Count == 0 || zip.Entries.Any(e => !e.FullName.StartsWith("unity-bridge/", StringComparison.Ordinal)))
                throw new InvalidDataException("CLI ZIP의 unity-bridge 폴더 구조를 확인하세요.");
        await SpeedFiles.Extract(archive, directory, "unity-bridge/", ct);
        ValidateLayout(directory, true);
    }
    public static void ValidateLayout(string directory, bool bundle)
    {
        SpeedFiles.Regular(directory);
        if (!File.Exists(Path.Combine(directory, "unity-bridge.exe"))) throw new InvalidDataException("CLI 실행 파일이 없습니다.");
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        var runtimes = entries.Where(Directory.Exists).ToArray();
        if (bundle)
        {
            if (entries.Length != 2 || runtimes.Length != 1 || !Regex.IsMatch(Path.GetFileName(runtimes[0]), "^_unity_bridge_runtime_[a-f0-9]{32}$") ||
                !Directory.EnumerateFiles(runtimes[0]).Any(f => Regex.IsMatch(Path.GetFileName(f), "^python[0-9]{2,3}\\.dll$", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("실행 파일 옆의 RC 런타임 폴더가 없거나 번들 구성이 올바르지 않습니다.");
        }
        else if (entries.Length != 1) throw new InvalidDataException("단일 EXE 릴리스에 다른 파일이 섞였습니다.");
        _ = Files(directory);
    }
    private static string[] Files(string root)
    {
        SpeedFiles.Regular(root); var files = new List<string>(); var directories = new Queue<string>(); directories.Enqueue(root); int count = 0;
        while (directories.TryDequeue(out var directory))
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++count > 15000) throw new InvalidDataException("CLI 파일 수 제한을 넘었습니다.");
            SpeedFiles.Regular(entry);
            if (Directory.Exists(entry)) directories.Enqueue(entry); else files.Add(entry);
        }
        return files.OrderBy(f => Path.GetRelativePath(root, f).Replace('\\', '/'), StringComparer.Ordinal).ToArray();
    }
    public static async Task<string> TreeHash(string root, CancellationToken ct)
    {
        var files = Files(root); long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("UnityBridgeDesk/cli-tree/v1\0"));
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] name = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/')), length = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(length, name.Length); hash.AppendData(length); hash.AppendData(name);
            await using var stream = File.OpenRead(file); total += stream.Length;
            if (total > 1024L * 1024 * 1024) throw new InvalidDataException("CLI 런타임 크기 제한을 넘었습니다.");
            BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length); hash.AppendData(length);
            hash.AppendData(await SHA256.HashDataAsync(stream, ct));
        }
        if (!files.SequenceEqual(Files(root))) throw new IOException("확인 중 CLI 파일 구성이 변경되었습니다.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static string ReportedConnectorVersion(SpeedRelease release) =>
        (release.SourceTag ?? release.Tag) == "v0.2.2-rc.1" && release.Version == "0.2.2-rc.1" && release.Commit == "74639d7b3f3adf550d58cc853b715f878153839f"
            ? "0.2.1" : release.Version;
    public static string? CompatibilityNote(SpeedRelease release) => ReportedConnectorVersion(release) == release.Version ? null :
        "RC1 공식 패키지의 보고 오류: 설치 버전은 0.2.2-rc.1, Connector 보고 값은 0.2.1입니다. 커밋과 패키지 해시로 설치본을 확인합니다.";
}
