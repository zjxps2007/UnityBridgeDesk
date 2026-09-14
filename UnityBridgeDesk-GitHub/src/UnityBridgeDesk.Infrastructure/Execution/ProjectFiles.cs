using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnityBridgeDesk.Infrastructure.Execution;

public static class ProjectFiles
{
    public static void RequireSelfContainedPackages(string root)
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"Packages","manifest.json")));
        foreach(var dependency in manifest.RootElement.GetProperty("dependencies").EnumerateObject())
            if(dependency.Name!="com.zjxps2007.unity-bridge-connector" && dependency.Value.GetString() is { } source && source.StartsWith("file:",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("벤치 기준 프로젝트의 다른 로컬 file: 패키지는 독립 복제할 수 없습니다. Packages 안의 임베디드 패키지 또는 고정 버전으로 준비하세요: "+dependency.Name);
    }
    public static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static IEnumerable<string> Files(string root)
    {
        var queue = new Queue<string>(); queue.Enqueue(Path.GetFullPath(root));
        int entries = 0;
        while (queue.TryDequeue(out var path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("링크된 폴더·파일은 복제하지 않습니다: " + path);
            foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                if (++entries > 200000) throw new IOException("복제 항목 수 한도를 초과했습니다.");
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("링크된 항목: " + child);
                if ((attributes & FileAttributes.Directory) != 0) { if (Path.GetFileName(child) != ".git") queue.Enqueue(child); }
                else yield return child;
            }
        }
    }
    public static async Task<string> FingerprintAsync(string root, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var section in new[] { "Assets", "Packages", "ProjectSettings" })
        foreach (string file in Files(Path.Combine(root, section)).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\','/') + "\0"));
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            hash.AppendData(await SHA256.HashDataAsync(stream, ct));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static async Task<Dictionary<string,string>> InventoryAsync(string root,CancellationToken ct)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string section in new[]{"Assets","Packages","ProjectSettings"})
        foreach(string file in Files(Path.Combine(root,section)))
        {
            await using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
            result[Path.GetRelativePath(root,file).Replace('\\','/')]=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct)).ToLowerInvariant();
        }
        return result;
    }
    public static async Task CopyFolderAsync(string source, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination)) throw new IOException("복제 대상 폴더가 이미 있습니다.");
        Directory.CreateDirectory(destination);
        foreach (string file in Files(source))
        {
            ct.ThrowIfCancellationRequested();
            string output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(target, ct);
        }
    }
    public static async Task CloneAsync(string source, string destination, string expectedHash, CancellationToken ct)
    {
        if (Directory.Exists(destination)) throw new IOException("시행 폴더 재사용은 허용되지 않습니다.");
        foreach (string section in new[] { "Assets", "Packages", "ProjectSettings" })
            await CopyFolderAsync(Path.Combine(source, section), Path.Combine(destination, section), ct);
        if (await FingerprintAsync(destination, ct) != expectedHash || await FingerprintAsync(source, ct) != expectedHash)
            throw new InvalidDataException("복제 중 기준 프로젝트가 바뀌었습니다. 이번 계획을 다시 고정하세요.");
    }
    public static void DeleteOwnedFolder(string managedRoot, string folder, string token)
    {
        string root = Path.GetFullPath(managedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(folder);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, ".desk-owner")) ||
            File.ReadAllText(Path.Combine(target, ".desk-owner")) != token)
            throw new IOException("소유권·관리 경로를 확인할 수 없어 보관했습니다.");
        _ = Files(target).ToArray(); // Reject reparse points throughout before recursive deletion.
        Directory.Delete(target, true);
    }
}
