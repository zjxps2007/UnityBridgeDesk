using System.Security.Cryptography;
using System.Text.Json;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class CatalogTests
{
    private static string Project(string root, string name = "한글 프로젝트")
    {
        string path = Path.Combine(root, name);
        foreach (string folder in new[] { "Assets", "Packages", "ProjectSettings" }) Directory.CreateDirectory(Path.Combine(path, folder));
        File.WriteAllText(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.42f1\nm_EditorVersionWithRevision: ignored\n");
        File.WriteAllText(Path.Combine(path, "Packages", "manifest.json"), "{\"dependencies\":{}}");
        return path;
    }
    private static string Connector(string root, string name = "커넥터 0.2.0")
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "package.json"), "{\"name\":\"com.zjxps2007.unity-bridge-connector\",\"version\":\"0.2.0\",\"repository\":{\"url\":\"https://example.invalid/test-only\"}}");
        File.WriteAllText(Path.Combine(path, "README.md"), "Synthetic fixture; never installed.");
        return path;
    }
    private static string Cli(string root, string name = "시험 CLI.exe")
    {
        var path = Path.Combine(root, name);
        File.Copy(Environment.ProcessPath!, path); // Existing PE bytes only; never execute this copy.
        return path;
    }
    private static async Task<CatalogService> Open(string root)
    { var service = new CatalogService(Path.Combine(root, "desk-data")); await service.LoadAsync(); return service; }
    private static void Ok(CatalogResult result) => Assert.IsTrue(result.Success, result.Message);

    [TestMethod]
    public async Task KoreanProjectRegistersRestoresSelectionAndRemovalLeavesEveryFile()
    {
        var root = SampleData.TestDirectory(); var path = Project(root);
        var manifest = Path.Combine(path, "Packages", "manifest.json"); var before = File.ReadAllBytes(manifest);
        ProjectId id;
        using (var catalog = await Open(root))
        {
            Ok(await catalog.RegisterProjectAsync(path));
            var item = catalog.Document.Projects.Single(); id = item.Project.Id;
            Assert.AreEqual("6000.0.42f1", item.Project.UnityBuild);
            Assert.AreEqual(PackageState.NotDeclared, item.Observation.PackageState);
            Ok(await catalog.SelectProjectAsync(id));
            Assert.IsFalse((await catalog.RegisterProjectAsync(path + Path.DirectorySeparatorChar)).Success);
        }
        using var restored = await Open(root);
        Assert.AreEqual(id, restored.Document.SelectedProject);
        Assert.AreEqual(path, restored.Document.Projects.Single().Project.RootPath);
        Ok(await restored.RemoveProjectAsync(id));
        Assert.IsNull(restored.Document.SelectedProject); Assert.IsTrue(Directory.Exists(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(manifest));
    }

    [TestMethod]
    public async Task MissingInvalidAndUnreadableProjectMetadataRemainExplicit()
    {
        var root = SampleData.TestDirectory(); using var catalog = await Open(root);
        Ok(await catalog.RegisterProjectAsync(Path.Combine(root, "없는 경로")));
        Ok(await catalog.RegisterProjectAsync(root));
        var path = Project(root); File.WriteAllText(Path.Combine(path, "Packages", "manifest.json"), "broken");
        Ok(await catalog.RegisterProjectAsync(path));
        Assert.AreEqual(InspectionStatus.Missing, catalog.Document.Projects[0].Observation.Status);
        Assert.AreEqual(InspectionStatus.Invalid, catalog.Document.Projects[1].Observation.Status);
        Assert.AreEqual(PackageState.Unknown, catalog.Document.Projects[2].Observation.PackageState);
        Assert.AreEqual(InspectionStatus.Available, catalog.Document.Projects[2].Observation.Status);
        Assert.IsFalse((await catalog.RegisterProjectAsync("relative folder")).Success);
    }

    [TestMethod]
    public async Task ManifestAndLockCommitDoNotPretendPackageIsLoadedOrUrlIsVersion()
    {
        var root = SampleData.TestDirectory(); var path = Project(root);
        string source = "https://example.invalid/UnityBridge.git?path=/unity-bridge-connector#v0.2.1";
        File.WriteAllText(Path.Combine(path, "Packages", "manifest.json"), JsonSerializer.Serialize(new { dependencies = new Dictionary<string, string> { [LocalInspector.ConnectorPackageName] = source } }));
        File.WriteAllText(Path.Combine(path, "Packages", "packages-lock.json"), JsonSerializer.Serialize(new { dependencies = new Dictionary<string, object> { [LocalInspector.ConnectorPackageName] = new { version = source, source = "git", hash = new string('a', 40) } } }));
        var observed = await new LocalInspector().InspectProjectAsync(path);
        Assert.AreEqual(PackageState.LockRecorded, observed.PackageState);
        Assert.AreEqual(new string('a', 40), observed.LockCommit); Assert.AreEqual(source, observed.PackageSource);
        Assert.IsNull(observed.PackageVersion);
        var embedded = Path.Combine(path, "Packages", LocalInspector.ConnectorPackageName); Directory.CreateDirectory(embedded);
        File.WriteAllText(Path.Combine(embedded, "package.json"), "{\"name\":\"com.zjxps2007.unity-bridge-connector\",\"version\":\"0.2.0\"}");
        observed = await new LocalInspector().InspectProjectAsync(path);
        Assert.AreEqual(PackageState.Embedded, observed.PackageState); Assert.AreEqual("0.2.0", observed.PackageVersion);
        Assert.IsNull(observed.LockCommit);
    }

    [TestMethod]
    public async Task DuplicateBytesAtAnotherPathAreRejectedAndMovedArtifactKeepsIdentity()
    {
        var root = SampleData.TestDirectory(); var path = Cli(root); var copy = Path.Combine(root, "이동한 CLI.exe"); File.Copy(path, copy);
        using var catalog = await Open(root);
        Ok(await catalog.RegisterArtifactAsync(path, ArtifactKind.CliExecutable, "0.2.1 표시 이름"));
        var original = catalog.Document.Artifacts.Single();
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), original.Registered.Sha256);
        Assert.IsFalse((await catalog.RegisterArtifactAsync(copy, ArtifactKind.CliExecutable)).Success);
        File.Delete(path); Ok(await catalog.RefreshArtifactAsync(original.Id));
        Assert.AreEqual(InspectionStatus.Missing, catalog.Document.Artifacts.Single().Current.Status);
        Ok(await catalog.RelocateArtifactAsync(original.Id, copy));
        Assert.AreEqual(original.Id, catalog.Document.Artifacts.Single().Id);
        Assert.AreEqual(original.Registered, catalog.Document.Artifacts.Single().Registered);
        Assert.IsTrue(catalog.Document.Artifacts.Single().MatchesRegistration);
    }

    [TestMethod]
    public async Task ChangedBytesPreserveOriginalEvidenceAndNeedNewReleaseIdentity()
    {
        var root = SampleData.TestDirectory(); var path = Cli(root); using var catalog = await Open(root);
        Ok(await catalog.RegisterArtifactAsync(path, ArtifactKind.CliExecutable));
        var original = catalog.Document.Artifacts.Single();
        Ok(await catalog.AddReleaseAsync("버전 A", ComparisonAxis.Unset, original.Id, null));
        File.AppendAllText(path, "changed overlay"); Ok(await catalog.RefreshArtifactAsync(original.Id));
        Assert.AreEqual(InspectionStatus.Changed, catalog.Document.Artifacts.Single().Current.Status);
        Assert.AreEqual(original.Registered.Sha256, catalog.Document.Artifacts.Single().Registered.Sha256);
        Assert.IsFalse((await catalog.RelocateArtifactAsync(original.Id, path)).Success);
        Assert.IsFalse((await catalog.AddReleaseAsync("버전 B", ComparisonAxis.CliOnly, original.Id, null)).Success);
        Ok(await catalog.RegisterArtifactAsync(path, ArtifactKind.CliExecutable, "변경 파일"));
        Assert.AreEqual(2, catalog.Document.Artifacts.Length);
        Assert.AreNotEqual(original.Id, catalog.Document.Artifacts[1].Id);
        Assert.IsFalse((await catalog.RemoveArtifactAsync(original.Id)).Success);
        Ok(await catalog.RemoveReleaseAsync(catalog.Document.Releases.Single().Id));
        Ok(await catalog.RemoveArtifactAsync(original.Id)); Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task ConnectorTreeHashCoversContentsPathsAndExcludesGitMetadata()
    {
        var root = SampleData.TestDirectory(); var path = Connector(root); var copy = Connector(root, "복사 폴더");
        var inspector = new LocalInspector(); var a = await inspector.InspectArtifactAsync(path, ArtifactKind.ConnectorFolder);
        var b = await inspector.InspectArtifactAsync(copy, ArtifactKind.ConnectorFolder);
        Assert.AreEqual(a.Sha256, b.Sha256); Assert.AreEqual("0.2.0", a.DeclaredVersion);
        Directory.CreateDirectory(Path.Combine(path, ".git")); File.WriteAllText(Path.Combine(path, ".git", "HEAD"), new string('b', 40));
        var c = await inspector.InspectArtifactAsync(path, ArtifactKind.ConnectorFolder);
        Assert.AreEqual(a.Sha256, c.Sha256); Assert.AreEqual(new string('b', 40), c.GitHead);
        File.Move(Path.Combine(copy, "README.md"), Path.Combine(copy, "renamed.md"));
        Assert.AreNotEqual(a.Sha256, (await inspector.InspectArtifactAsync(copy, ArtifactKind.ConnectorFolder)).Sha256);
        File.AppendAllText(Path.Combine(path, "README.md"), "content changed");
        Assert.AreNotEqual(a.Sha256, (await inspector.InspectArtifactAsync(path, ArtifactKind.ConnectorFolder)).Sha256);
    }

    [TestMethod]
    public async Task InvalidExeWrongPackageAndLockedFilesHaveNoUsableHash()
    {
        var root = SampleData.TestDirectory(); var fake = Path.Combine(root, "fake.exe"); File.WriteAllText(fake, "do not execute");
        var wrongPackage = Connector(root); File.WriteAllText(Path.Combine(wrongPackage, "package.json"), "{\"name\":\"other.package\",\"version\":\"0.2.1\"}");
        var inspector = new LocalInspector();
        Assert.AreEqual(InspectionStatus.Invalid, (await inspector.InspectArtifactAsync(fake, ArtifactKind.CliExecutable)).Status);
        Assert.AreEqual(InspectionStatus.Invalid, (await inspector.InspectArtifactAsync(wrongPackage, ArtifactKind.ConnectorFolder)).Status);
        Assert.AreEqual(InspectionStatus.Invalid, (await inspector.InspectArtifactAsync(root, ArtifactKind.ConnectorFolder)).Status);
        var cli = Cli(root); using var locked = new FileStream(cli, FileMode.Open, FileAccess.Read, FileShare.None);
        var unavailable = await inspector.InspectArtifactAsync(cli, ArtifactKind.CliExecutable);
        Assert.AreEqual(InspectionStatus.Unavailable, unavailable.Status); Assert.IsNull(unavailable.Sha256);
    }

    [TestMethod]
    public async Task ThreeToolsFreezeSameIdentitiesWhileCatalogSelectionAndPathsChange()
    {
        var root = SampleData.TestDirectory(); var project = Project(root); var second = Project(root, "다른 프로젝트");
        using var catalog = await Open(root);
        Ok(await catalog.RegisterProjectAsync(project)); Ok(await catalog.SelectProjectAsync(catalog.Document.Projects.Single().Project.Id));
        Ok(await catalog.RegisterArtifactAsync(Cli(root), ArtifactKind.CliExecutable, "0.2.1"));
        Ok(await catalog.RegisterArtifactAsync(Connector(root), ArtifactKind.ConnectorFolder, "0.2.1 표시"));
        var cli = catalog.Document.Artifacts[0]; var connector = catalog.Document.Artifacts[1];
        Assert.IsFalse((await catalog.AddReleaseAsync("잘못된 조합", ComparisonAxis.CliAndConnector, cli.Id, null)).Success);
        Ok(await catalog.AddReleaseAsync("릴리스 0.2.1", ComparisonAxis.CliAndConnector, cli.Id, connector.Id));
        Ok(await catalog.SelectReleaseAsync(catalog.Document.Releases.Single().Id));
        var frozen = new[] { ToolKind.Installation, ToolKind.AiWork, ToolKind.Benchmark }.Select(tool => catalog.Document.CreateDraft(tool,
            tool == ToolKind.Benchmark ? BenchmarkModes.FixedCommands : BenchmarkModes.None).Freeze()).ToArray();
        Assert.AreEqual(1, frozen.Select(x => x.Project.Id).Distinct().Count()); Assert.AreEqual(1, frozen.Select(x => x.Releases[0].Id).Distinct().Count());
        Assert.AreEqual(ComparisonAxis.CliAndConnector, frozen[0].Releases[0].ComparisonAxis);
        Assert.AreEqual("connector-tree-v1", frozen[0].Releases[0].ConnectorHashScheme);
        Assert.AreEqual("https://example.invalid/test-only", frozen[0].Releases[0].ConnectorDeclaredSource);
        Assert.IsNull(frozen[0].Releases[0].ObservedCliVersion);
        Assert.AreEqual("0.2.0", frozen[0].Releases[0].ObservedConnectorVersion);
        Ok(await catalog.RegisterProjectAsync(second)); Ok(await catalog.SelectProjectAsync(catalog.Document.Projects[1].Project.Id));
        Ok(await catalog.RelocateProjectAsync(frozen[0].Project.Id, Project(root, "이동 경로")));
        Ok(await catalog.RemoveReleaseAsync(catalog.Document.SelectedRelease!.Value));
        foreach (var plan in frozen) { Assert.AreEqual(project, plan.Project.RootPath); Assert.AreEqual("릴리스 0.2.1", plan.Releases[0].Label); }
    }

    [TestMethod]
    public async Task RestartRestoresArtifactsReleasesAndUnsetAxisWithoutAssumingVersion()
    {
        // Original 01 release documents have none of the optional 03 snapshot fields.
        string legacy = JsonSerializer.Serialize(new { id = ReleaseId.New(), label = "0.2.0", cliPath = (string?)null,
            cliSha256 = (string?)null, observedCliVersion = (string?)null, connectorSource = (string?)null,
            connectorSha256 = (string?)null, observedConnectorVersion = (string?)null }, DeskJson.Options);
        var oldRelease = JsonSerializer.Deserialize<BridgeReleaseRef>(legacy, DeskJson.Options)!;
        oldRelease.Validate(); Assert.AreEqual(ComparisonAxis.Unset, oldRelease.ComparisonAxis); Assert.IsNull(oldRelease.ConnectorHashScheme);
        var root = SampleData.TestDirectory(); CatalogDocument saved;
        using (var catalog = await Open(root))
        {
            Ok(await catalog.RegisterArtifactAsync(Cli(root), ArtifactKind.CliExecutable, "0.2.1"));
            Ok(await catalog.AddReleaseAsync("0.2.1", ComparisonAxis.Unset, catalog.Document.Artifacts.Single().Id, null));
            Ok(await catalog.SelectReleaseAsync(catalog.Document.Releases.Single().Id)); saved = catalog.Document;
        }
        using var restored = await Open(root);
        Assert.AreEqual(ReadStatus.Current, restored.LoadStatus);
        Assert.AreEqual(saved.SelectedRelease, restored.Document.SelectedRelease);
        Assert.AreEqual(saved.Artifacts.Single(), restored.Document.Artifacts.Single());
        Assert.AreEqual(ComparisonAxis.Unset, restored.Document.Releases.Single().Axis);
    }

    [TestMethod]
    public async Task SecondDeskCannotOverwriteCatalogAndCanAcquireLeaseAfterFirstExits()
    {
        var root = SampleData.TestDirectory(); using var first = await Open(root); using var second = await Open(root);
        Assert.IsFalse(second.CanWrite);
        Assert.IsFalse((await second.RegisterProjectAsync(Project(root))).Success);
        Ok(await first.RegisterProjectAsync(Project(root, "첫 번째"))); first.Dispose();
        await second.LoadAsync(); Assert.IsTrue(second.CanWrite); Assert.HasCount(1, second.Document.Projects);
    }

    [TestMethod]
    public async Task SaveFailureDoesNotPublishUnsavedSelectionOrErasePreviousCatalog()
    {
        var root = SampleData.TestDirectory(); using var catalog = await Open(root);
        Ok(await catalog.RegisterProjectAsync(Project(root))); var previous = catalog.Document;
        string path = Path.Combine(root, "desk-data", "catalog", "catalog.json"); var before = File.ReadAllBytes(path);
        using var locked = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.IsFalse((await catalog.SelectProjectAsync(previous.Projects[0].Project.Id)).Success);
        Assert.AreSame(previous, catalog.Document); CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task CorruptCatalogRecoversBackupAndFutureFormatIsNeverOverwritten()
    {
        var root = SampleData.TestDirectory(); string path = Path.Combine(root, "desk-data", "catalog", "catalog.json");
        using (var catalog = await Open(root))
        { Ok(await catalog.RegisterProjectAsync(Project(root))); Ok(await catalog.SelectProjectAsync(catalog.Document.Projects[0].Project.Id)); }
        File.WriteAllText(path, "corrupt");
        using (var recovered = await Open(root)) { Assert.AreEqual(ReadStatus.RecoveredBackup, recovered.LoadStatus); Assert.HasCount(1, recovered.Document.Projects); }
        const string future = "{\"formatVersion\":99,\"data\":{}}"; File.WriteAllText(path, future);
        using (var newer = await Open(root))
        {
            Assert.AreEqual(ReadStatus.UnsupportedVersion, newer.LoadStatus); Assert.IsFalse(newer.CanWrite);
            Assert.IsFalse((await newer.RegisterProjectAsync(root)).Success); Assert.AreEqual(future, File.ReadAllText(path));
        }
        File.WriteAllText(path, "corrupt"); File.WriteAllText(path + ".bak", "corrupt backup");
        using var damaged = await Open(root); Assert.AreEqual(ReadStatus.Corrupt, damaged.LoadStatus); Assert.IsFalse(damaged.CanWrite);
    }

    [TestMethod]
    public async Task AmbiguousAndOversizedMetadataDoNotProduceVerifiedObservations()
    {
        var root = SampleData.TestDirectory(); var project = Project(root); var connector = Connector(root);
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{\"dependencies\":{},\"dependencies\":{}}");
        File.WriteAllText(Path.Combine(connector, "package.json"), "{\"name\":\"com.zjxps2007.unity-bridge-connector\",\"version\":\"0.2.0\",\"version\":\"0.2.1\"}");
        var inspector = new LocalInspector();
        Assert.AreEqual(PackageState.Unknown, (await inspector.InspectProjectAsync(project)).PackageState);
        Assert.AreEqual(InspectionStatus.Invalid, (await inspector.InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder)).Status);
        File.WriteAllText(Path.Combine(connector, "package.json"), new string(' ', 1024 * 1024 + 1));
        Assert.IsNull((await inspector.InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder)).Sha256);
    }
}
