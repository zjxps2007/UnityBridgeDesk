using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Models;

namespace UnityBridgeDesk.Infrastructure.Catalog;

// Read-only inspection. No process, network, package extraction, or project writes.
public sealed class LocalInspector
{
    public const string ConnectorPackageName = "com.zjxps2007.unity-bridge-connector";
    private const long MaxArtifactBytes = 1024L * 1024 * 1024;
    private const int MaxJsonBytes = 1024 * 1024;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new ArgumentException("전체 경로를 입력하거나 찾아보기로 선택해 주세요.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    public Task<ProjectObservation> InspectProjectAsync(string path) => Task.Run(() => InspectProject(path));
    public Task<ArtifactObservation> InspectArtifactAsync(string path, ArtifactKind kind) => Task.Run(() => InspectArtifact(path, kind));

    private static ProjectObservation InspectProject(string path)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            RequireDirectory(path);
            foreach (var folder in new[] { "Assets", "Packages", "ProjectSettings" })
                if (!Directory.Exists(Path.Combine(path, folder)))
                    return new(InspectionStatus.Invalid, "Assets · Packages · ProjectSettings 폴더가 모두 필요합니다.", now, null, PackageState.Unknown, null, null, null);
            var versionFile = Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
            string? editor = File.Exists(versionFile) ? ReadText(versionFile).Split('\n')
                .FirstOrDefault(x => x.StartsWith("m_EditorVersion:", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() : null;
            if (string.IsNullOrWhiteSpace(editor)) editor = null;
            PackageState state = PackageState.Unknown;
            string? source = null, version = null, commit = null;
            string detail = editor is null ? "프로젝트 구조 확인 · Editor 버전 미확인" : "프로젝트 구조 및 Editor 선언 버전 확인";
            try
            {
                string manifest = Path.Combine(path, "Packages", "manifest.json");
                if (File.Exists(manifest))
                {
                    using var json = ReadJson(manifest);
                    if (!json.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException();
                    source = Property(dependencies, ConnectorPackageName);
                    state = source is null ? PackageState.NotDeclared : PackageState.Declared;
                }
                string embedded = Path.Combine(path, "Packages", ConnectorPackageName, "package.json");
                if (File.Exists(embedded))
                {
                    using var json = ReadJson(embedded);
                    if (Property(json.RootElement, "name") != ConnectorPackageName) throw new InvalidDataException();
                    version = Property(json.RootElement, "version"); state = PackageState.Embedded;
                }
                else if (source is not null)
                {
                    string locked = Path.Combine(path, "Packages", "packages-lock.json");
                    if (File.Exists(locked))
                    {
                        using var json = ReadJson(locked);
                        if (!json.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException();
                        if (dependencies.TryGetProperty(ConnectorPackageName, out var package))
                        {
                            // Git URLs in lock.version are a source reference, not a semantic package version.
                            if (Property(package, "source") == "registry") version = Property(package, "version");
                            commit = ValidCommit(Property(package, "hash")); state = PackageState.LockRecorded;
                        }
                    }
                }
            }
            catch (Exception error) when (IsReadFailure(error))
            { state = PackageState.Unknown; source = null; version = null; commit = null; detail += " · 패키지 정보 읽기 실패"; }
            return new(InspectionStatus.Available, detail, now, editor, state, source, version, commit);
        }
        catch (FileNotFoundException) { return new(InspectionStatus.Missing, "폴더가 없어요. 이동했다면 경로를 다시 지정해 주세요.", now, null, PackageState.Unknown, null, null, null); }
        catch (DirectoryNotFoundException) { return new(InspectionStatus.Missing, "폴더가 없어요. 이동했다면 경로를 다시 지정해 주세요.", now, null, PackageState.Unknown, null, null, null); }
        catch (InvalidDataException) { return new(InspectionStatus.Invalid, "프로젝트 폴더 또는 Editor 버전 파일의 형식을 확인해 주세요.", now, null, PackageState.Unknown, null, null, null); }
        catch (Exception error) when (IsReadFailure(error))
        { return new(InspectionStatus.Unavailable, "프로젝트를 읽지 못했어요. 권한·잠금·파일 크기를 확인해 주세요.", now, null, PackageState.Unknown, null, null, null); }
    }

    private static ArtifactObservation InspectArtifact(string path, ArtifactKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (kind == ArtifactKind.CliExecutable)
            {
                RejectLink(path);
                using var file = OpenRead(path);
                if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                string hash = Hash(file); file.Position = 0;
                using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
                var characteristics = pe.PEHeaders.CoffHeader.Characteristics;
                if (pe.PEHeaders.PEHeader is null || !characteristics.HasFlag(Characteristics.ExecutableImage) || characteristics.HasFlag(Characteristics.Dll))
                    throw new InvalidDataException();
                var info = FileVersionInfo.GetVersionInfo(path);
                string? version = string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion;
                return new(InspectionStatus.Available, "Windows 실행 파일·해시 확인 · Bridge 버전 및 동작은 미확인", now, hash, file.Length, version, null, null);
            }
            RequireDirectory(path); RejectLink(path);
            using var packageFile = OpenRead(Path.Combine(path, "package.json"));
            if (packageFile.Length > MaxJsonBytes) throw new InvalidDataException();
            using var metadata = JsonDocument.Parse(packageFile, new() { MaxDepth = 32 });
            RejectDuplicateMembers(metadata.RootElement);
            if (Property(metadata.RootElement, "name") != ConnectorPackageName ||
                string.IsNullOrWhiteSpace(Property(metadata.RootElement, "version"))) throw new InvalidDataException();
            var entries = Gather(path);
            var stamps = entries.Select(x => (Path: x, Length: new FileInfo(x).Length, Modified: File.GetLastWriteTimeUtc(x))).ToArray();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(Encoding.UTF8.GetBytes("UnityBridgeDesk/connector-tree/v1\0"));
            long total = 0;
            foreach (var entry in entries)
            {
                var relative = Encoding.UTF8.GetBytes(Path.GetRelativePath(path, entry).Replace('\\', '/'));
                byte[] length = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(length, relative.Length);
                digest.AppendData(length); digest.AppendData(relative);
                using var file = OpenRead(entry); total = checked(total + file.Length);
                if (total > MaxArtifactBytes) throw new InvalidDataException();
                BinaryPrimitives.WriteInt64LittleEndian(length, file.Length); digest.AppendData(length);
                digest.AppendData(SHA256.HashData(file));
            }
            // A changing tree is rejected when its membership changes during the observation.
            if (!entries.SequenceEqual(Gather(path)) || stamps.Any(x => new FileInfo(x.Path).Length != x.Length || File.GetLastWriteTimeUtc(x.Path) != x.Modified))
                throw new IOException("Tree changed.");
            string? source = metadata.RootElement.TryGetProperty("repository", out var repo)
                ? repo.ValueKind == JsonValueKind.String ? repo.GetString() : Property(repo, "url") : null;
            return new(InspectionStatus.Available, "Connector 폴더 내용 해시 확인 · package.json의 선언값", now,
                Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), total,
                Property(metadata.RootElement, "version"), source, ReadGitHead(path));
        }
        catch (FileNotFoundException) { return kind == ArtifactKind.ConnectorFolder && Directory.Exists(path)
            ? new(InspectionStatus.Invalid, "선택한 폴더에 package.json이 없어요. Connector 패키지 폴더를 선택해 주세요.", now, null, null, null, null, null) : Missing(now); }
        catch (DirectoryNotFoundException) { return Missing(now); }
        catch (Exception error) when (error is InvalidDataException or JsonException or BadImageFormatException)
        { return new(InspectionStatus.Invalid, "형식을 확인해 주세요. CLI는 Windows EXE, Connector는 package.json이 있는 패키지 폴더입니다. 링크·과대 파일은 지원하지 않아요.", now, null, null, null, null, null); }
        catch (Exception error) when (IsReadFailure(error) || error is System.ComponentModel.Win32Exception)
        { return new(InspectionStatus.Unavailable, "읽기 실패 · 권한, 잠금 또는 검사 중 파일 변경을 확인해 주세요.", now, null, null, null, null, null); }
    }

    private static ArtifactObservation Missing(DateTimeOffset now) => new(InspectionStatus.Missing,
        "파일 또는 패키지 폴더가 없어요. 이동했다면 경로를 다시 지정해 주세요.", now, null, null, null, null, null);
    private static bool IsReadFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static void RequireDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)) throw new InvalidDataException();
    }
    private static void RejectLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException();
    }
    private static FileStream OpenRead(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= MaxArtifactBytes) return file;
        file.Dispose(); throw new InvalidDataException();
    }
    private static string Hash(Stream file) => Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    private static string ReadText(string path)
    {
        using var file = OpenRead(path);
        if (file.Length > MaxJsonBytes) throw new InvalidDataException();
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    private static JsonDocument ReadJson(string path)
    {
        var document = JsonDocument.Parse(ReadText(path), new() { MaxDepth = 32 });
        try { RejectDuplicateMembers(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException(); RejectDuplicateMembers(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }
    private static string? Property(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (!parent.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        return value.GetString();
    }
    private static List<string> Gather(string root)
    {
        var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root); int visited = 0;
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++visited > 10000) throw new InvalidDataException();
                if (Path.GetFileName(entry) == ".git") continue;
                RejectLink(entry);
                if (Directory.Exists(entry)) pending.Push(entry); else result.Add(entry);
            }
        }
        result.Sort(StringComparer.Ordinal); return result;
    }
    private static string? ValidCommit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    private static string? ReadGitHead(string path)
    {
        // HEAD is only a repository hint, not a claim that the selected files match a clean commit.
        try
        {
            for (var folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
            {
                var git = Path.Combine(folder.FullName, ".git");
                if (File.Exists(git)) return null; // Worktree indirection is deliberately not followed here.
                if (!Directory.Exists(git)) continue;
                RejectLink(git);
                string head = ReadText(Path.Combine(git, "HEAD")).Trim();
                if (ValidCommit(head) is { } detached) return detached;
                if (!head.StartsWith("ref: refs/", StringComparison.Ordinal)) return null;
                string reference = head[5..];
                string target = Path.GetFullPath(Path.Combine(git, reference));
                if (!target.StartsWith(git + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(target)) return ValidCommit(ReadText(target).Trim());
                string packed = Path.Combine(git, "packed-refs");
                return File.Exists(packed) ? ReadText(packed).Split('\n').Select(x => x.Trim().Split(' ', 2))
                    .Where(x => x.Length == 2 && x[1] == reference).Select(x => ValidCommit(x[0])).FirstOrDefault() : null;
            }
        }
        catch (Exception error) when (IsReadFailure(error)) { }
        return null;
    }
}
