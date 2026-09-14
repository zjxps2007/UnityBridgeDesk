using System.Text.Json;
using System.Text.Json.Nodes;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.Installation;

public sealed record PackageChange(string Project, string ManifestPath, string BeforeHash, string? PreviousSource,
    string AppliedSource, string ConnectorHash, string ConnectorVersion, string BackupDirectory);
public sealed class PackageProvisioner(string dataRoot)
{
    private const string Package = LocalInspector.ConnectorPackageName;
    public async Task<PackageChange> PrepareAsync(string project, BridgeReleaseRef release, string recordDirectory, CancellationToken ct)
    {
        if (release.ConnectorSource is null || release.ConnectorSha256 is null)
            throw new InvalidOperationException("적용할 로컬 Connector 폴더를 릴리스에 지정하세요.");
        var inspector = new LocalInspector();
        var observed = await inspector.InspectArtifactAsync(release.ConnectorSource, ArtifactKind.ConnectorFolder);
        if (observed.Status != InspectionStatus.Available || !string.Equals(observed.Sha256, release.ConnectorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Connector 내용이 등록 이후 바뀌었습니다.");
        string stored = Path.Combine(dataRoot, "packages", release.ConnectorSha256.ToLowerInvariant());
        if (!Directory.Exists(stored))
        {
            string temp = stored + "." + Guid.NewGuid().ToString("N");
            await ProjectFiles.CopyFolderAsync(release.ConnectorSource, temp, ct);
            var copied = await inspector.InspectArtifactAsync(temp, ArtifactKind.ConnectorFolder);
            if (copied.Sha256 != observed.Sha256) throw new InvalidDataException("복사한 패키지 해시가 다릅니다.");
            Directory.Move(temp, stored);
        }
        var existing = await inspector.InspectArtifactAsync(stored, ArtifactKind.ConnectorFolder);
        if (existing.Sha256 != observed.Sha256) throw new InvalidDataException("보관한 패키지가 변경되었습니다.");
        string manifest = Path.Combine(project, "Packages", "manifest.json");
        var node = ReadManifest(manifest);
        string? previous = node["dependencies"]![Package]?.GetValue<string>();
        Directory.CreateDirectory(recordDirectory);
        File.Copy(manifest, Path.Combine(recordDirectory, "manifest.before.json"), false);
        string packageLock = Path.Combine(project, "Packages", "packages-lock.json");
        if (File.Exists(packageLock)) File.Copy(packageLock, Path.Combine(recordDirectory, "packages-lock.before.json"), false);
        var change = new PackageChange(project, manifest, ProjectFiles.HashFile(manifest), previous,
            "file:" + stored.Replace('\\','/'), observed.Sha256!, observed.DeclaredVersion!, recordDirectory);
        await File.WriteAllTextAsync(Path.Combine(recordDirectory, "change.json"), JsonSerializer.Serialize(change, new JsonSerializerOptions { WriteIndented = true }), ct);
        return change;
    }
    public static bool Apply(PackageChange change)
    {
        var node = ReadManifest(change.ManifestPath);
        if (ProjectFiles.HashFile(change.ManifestPath) != change.BeforeHash) throw new IOException("검토 후 manifest가 바뀌었습니다. 다시 확인하세요.");
        if (node["dependencies"]![Package]?.GetValue<string>() == change.AppliedSource) return false;
        node["dependencies"]![Package] = change.AppliedSource;
        Replace(change.ManifestPath, node);
        return true;
    }
    public static bool Rollback(PackageChange change)
    {
        var node = ReadManifest(change.ManifestPath);
        string? current = node["dependencies"]![Package]?.GetValue<string>();
        if (current == change.PreviousSource) return true;
        if (current != change.AppliedSource) return false; // Preserve another actor's replacement.
        var deps = node["dependencies"]!.AsObject();
        if (change.PreviousSource is null) deps.Remove(Package); else deps[Package] = change.PreviousSource;
        Replace(change.ManifestPath, node);
        return true; // Unity re-resolves the lock; unrelated dependency changes are preserved.
    }
    private static JsonObject ReadManifest(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("manifest 크기 한도 초과");
        var node = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new InvalidDataException("manifest 형식 오류");
        if (node["dependencies"] is not JsonObject) throw new InvalidDataException("dependencies가 없습니다.");
        return node;
    }
    private static void Replace(string file, JsonObject node)
    {
        string temp = file + ".desk-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, node, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temp, file, true);
    }
}
