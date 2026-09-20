using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class OfficialUnityTests
{
    [TestMethod]
    public async Task OfficialWorkspaceDownloadsFreshAfterEachCleanupAndKeepsResults()
    {
        string data = SampleData.TestDirectory(), report = Path.Combine(data, "speed", "reports", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.WriteAllText(report, "keep");
        var registry = new RegistryFixture(); using var http = new HttpClient(registry);
        string? previous = null;
        for (int i = 0; i < 2; i++)
        {
            await using var space = await OfficialToolWorkspace.Create(data, null, default);
            var release = await space.Prepare(new("1.0.0-beta.10", "0.7.0-exp.1"), null, null, default, http);
            Assert.IsTrue(space.Contains(release)); Assert.AreNotEqual(previous, release.CliPath);
            Assert.IsTrue(release.CliPath.StartsWith(Path.Combine(data, "speed", "official-work") + Path.DirectorySeparatorChar));
            File.WriteAllText(Path.Combine(space.Root, "temp", "residue.dmp"), "previous residue");
            await space.DisposeAsync(); Assert.IsFalse(File.Exists(release.CliPath)); Assert.IsFalse(Directory.Exists(space.Root));
            previous = release.CliPath;
        }
        Assert.AreEqual(2, registry.BinaryDownloads); Assert.AreEqual("keep", File.ReadAllText(report));
    }
    [TestMethod]
    public async Task OfficialWorkspaceCleansPartialDownloadOnErrorOrCancellation()
    {
        foreach (bool cancel in new[] { false, true })
        {
            string data = SampleData.TestDirectory(); var space = await OfficialToolWorkspace.Create(data, null, default);
            using var http = new HttpClient(new RegistryFixture { CorruptBinary = !cancel, CancelBinary = cancel });
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => space.Prepare(new("1.0.0-beta.10", "0.7.0-exp.1"), null, null, default, http));
            else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => space.Prepare(new("1.0.0-beta.10", "0.7.0-exp.1"), null, null, default, http));
            Assert.IsFalse(Directory.Exists(space.Root));
        }
    }
    [TestMethod]
    public async Task OfficialWorkspaceRecoversDeadOwnerButNeverAnActiveOrUnknownFolder()
    {
        string data = SampleData.TestDirectory(); var first = await OfficialToolWorkspace.Create(data, null, default);
        await Assert.ThrowsExactlyAsync<IOException>(() => OfficialToolWorkspace.Create(data, null, default));
        string marker = Path.Combine(first.Root, ".local-owner.json");
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(marker);
        await SpeedFiles.Write(marker, owner with { Creator = new(int.MaxValue, DateTimeOffset.UnixEpoch) });
        await using var second = await OfficialToolWorkspace.Create(data, null, default);
        Assert.IsFalse(Directory.Exists(first.Root)); await second.DisposeAsync();
        string unknown = Path.Combine(LocalWorkspace.Parent(data, "official-work"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unknown");
        await Assert.ThrowsExactlyAsync<IOException>(() => OfficialToolWorkspace.Create(data, null, default));
        Assert.IsTrue(File.Exists(Path.Combine(unknown, "keep.txt")));
    }
    [TestMethod]
    public async Task OfficialWorkspaceRejectsCrossKindDeletionAndRetriesLockedFiles()
    {
        string data = SampleData.TestDirectory(); var space = await OfficialToolWorkspace.Create(data, null, default);
        var owner = await SpeedFiles.Read<LocalWorkspaceOwner>(Path.Combine(space.Root, ".local-owner.json"));
        await Assert.ThrowsExactlyAsync<IOException>(() => LocalWorkspace.Delete(data, space.Root, owner.TrialId, owner.Token));
        string file = Path.Combine(space.Root, "temp", "locked.exe");
        using (var handle = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => space.DisposeAsync().AsTask());
        Assert.IsTrue(File.Exists(Path.Combine(space.Root, ".local-owner.json")));
        await space.DisposeAsync(); Assert.IsFalse(Directory.Exists(space.Root));
    }
    [TestMethod]
    public void OfficialCliProfilePathsArePerTrialAndDoNotChangeTheParentEnvironment()
    {
        string original = Environment.GetEnvironmentVariable("APPDATA")!;
        string a = SampleData.TestDirectory(), b = SampleData.TestDirectory();
        var env = OfficialUnityCli.EnvironmentFor(a);
        foreach (string key in new[] { "APPDATA", "LOCALAPPDATA", "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "TEMP", "TMP" })
        { Assert.IsTrue(env[key].StartsWith(a + Path.DirectorySeparatorChar)); Assert.AreNotEqual(env[key], OfficialUnityCli.EnvironmentFor(b)[key]); }
        Assert.AreEqual(original, Environment.GetEnvironmentVariable("APPDATA"));
    }
    [TestMethod]
    public void ReportDistinguishesToolCleanupFromSuccessfulTrialCleanup()
    {
        var run = SpeedReportSample.Create() with { Status = "completed", OfficialCleanup = new("failed", "owned-folder", "locked") };
        var report = new SpeedReport(run);
        Assert.Contains("정리 미확인", report.Overview); Assert.Contains("locked", report.Memo()); Assert.Contains("locked", report.Issues());
        Assert.Contains("정리 미확인", report.State);
        report = new SpeedReport(run with { OfficialCleanup = new("deleted", "owned-folder") });
        Assert.Contains("삭제 확인", report.Text());
    }
    [TestMethod]
    public void DesktopSelectionKeepsLegacySettingsAndChecksCombinedTargetLimit()
    {
        var old = JsonSerializer.Deserialize<LocalSpeedSettings>("""{"SelectedTags":["v0.2.1","v0.2.0"]}""", SpeedProtocol.Json)!;
        Assert.IsNull(old.OfficialUnity);
        var configured = old with { OfficialUnity = new("1.0.0-beta.10", "0.7.0-exp.1"), BaselineTag = SpeedBenchWorkflow.OfficialLabel };
        var restored = JsonSerializer.Deserialize<LocalSpeedSettings>(JsonSerializer.Serialize(configured, SpeedProtocol.Json), SpeedProtocol.Json)!;
        Assert.AreEqual(configured.OfficialUnity, restored.OfficialUnity); Assert.AreEqual(configured.BaselineTag, restored.BaselineTag);
        SpeedBenchWorkflow.ValidateSelection(1, true); SpeedBenchWorkflow.ValidateSelection(7, true); SpeedBenchWorkflow.ValidateSelection(8, false);
        foreach (var (count, official) in new[] { (0, true), (8, true), (1, false), (9, false) })
            Assert.ThrowsExactly<ArgumentException>(() => SpeedBenchWorkflow.ValidateSelection(count, official));
        new OfficialUnitySelection().Validate();
        Assert.ThrowsExactly<ArgumentException>(() => new OfficialUnitySelection("not-a-version").Validate());
    }
    [TestMethod]
    public async Task UnknownPipelineVersionIsAnActionableInputError()
    {
        var repository = new OfficialUnityRepository(SampleData.TestDirectory(), new HttpClient(new RegistryFixture()));
        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => repository.Prepare("1.0.0-beta.10", "99.0.0", null, default));
        Assert.Contains("99.0.0", error.Message);
    }
    [TestMethod]
    public void CommandsKeepQuotedInputAsSingleArgumentsAndPinTarget()
    {
        string project = @"C:\실험 공백\project", json = """{"action":"echo","nonce":"a\"b"}""";
        string[] args = OfficialUnityCli.Arguments(project, 45, "desk_probe", json);
        Assert.AreEqual(project, args[4]); Assert.AreEqual("45", args[6]); Assert.AreEqual(json, args[^1]);
        Assert.DoesNotContain("--detach", args);
        Assert.AreEqual("eval_file", OfficialUnityCli.Arguments(project, 45, "desk_probe", json, "C:/한글 공백/exec-script.cs")[^2]);
        Assert.AreEqual("return 42;", OfficialUnityCli.Arguments(project, 45, "exec", """{"code":"return 42;"}""")[^1]);
        Assert.AreEqual("bench-incompatible", Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.Arguments(project, 45, "get_editor_state", "{}")).Kind);
        Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.Arguments(project, 45, "exec", """{"code":"return 42;","unknown":true}"""));
    }
    [TestMethod]
    public void OfficialResponseMustProveSuccessAndSameNonceProcessAndProject()
    {
        var response = Reply("""{"success":true,"data":{"result":{"nonce":"n","pid":5,"projectPath":"C:/project","value":42}}}""");
        var data = OfficialUnityCli.ReadData(response);
        Assert.AreEqual(42, SpeedFailure.ValidateIdentity(data, "n", 5, "C:/project").GetInt32());
        foreach (var input in new[] { ("wrong", 5, "C:/project"), ("n", 6, "C:/project"), ("n", 5, "C:/other") })
            Assert.AreEqual("response-mismatch", Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ValidateIdentity(data, input.Item1, input.Item2, input.Item3)).Kind);
        Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(response with { ExitCode = 1 }));
        Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(Reply("""{"success":true,"data":{"success":false,"result":42}}""")));
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(Reply("{}"))).Kind);
        Assert.AreEqual("timeout", Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(response with { Outcome = ProcessOutcome.TimedOut })).Kind);
    }
    [TestMethod]
    public void EvalChecksInnerSuccessAndUnwrapsOnlyItsKnownEnvelope()
    {
        var response = Reply("""{"success":true,"data":{"success":true,"result":{"command":"eval","success":true,"result":{"nonce":"n","pid":5,"projectPath":"C:/project","value":42}}}}""");
        Assert.AreEqual(42, SpeedFailure.ValidateIdentity(OfficialUnityCli.ReadData(response, true), "n", 5, "C:/project").GetInt32());
        Assert.AreEqual("eval", OfficialUnityCli.ReadData(response).GetProperty("command").GetString());
        Assert.AreEqual(42, OfficialUnityCli.ReadData(Reply("""{"success":true,"data":{"result":{"command":"eval","success":true,"result":42}}}"""), true).GetInt32());
        Assert.AreEqual(42, OfficialUnityCli.ReadData(Reply("""{"success":true,"data":{"result":{"success":true,"result":42,"output":null,"diagnostics":[]}}}"""), true).GetInt32());
        foreach (var (error, kind) in new[] { ("Compilation Failed", "compile-error"), ("Execution Failed", "runtime-error") })
            Assert.AreEqual(kind, Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(Reply(JsonSerializer.Serialize(new
                { success = true, data = new { result = new { command = "eval", success = false, error, result = 42 } } })), true)).Kind);
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() => OfficialUnityCli.ReadData(Reply("""{"success":true,"data":{"result":42}}"""), true)).Kind);
    }
    [TestMethod]
    public void OldResultsKeepBridgeDefaultsAndNewResultsKeepBothPinnedVersions()
    {
        var old = JsonSerializer.Deserialize<SpeedRelease>("""{"Tag":"v0.2.1","Version":"0.2.1","Commit":"c","CliPath":"c","ConnectorPath":"p","CliSha256":"h","ConnectorSha256":"h","CliUrl":"u"}""", SpeedProtocol.Json)!;
        Assert.IsNull(old.OfficialUnity);
        var official = old with { OfficialUnity = new("1.0.0-beta.10", "0.7.0-exp.1", []) };
        var restored = JsonSerializer.Deserialize<SpeedRelease>(JsonSerializer.Serialize(official, SpeedProtocol.Json), SpeedProtocol.Json)!;
        Assert.AreEqual("1.0.0-beta.10", restored.OfficialUnity!.CliVersion); Assert.AreEqual("0.7.0-exp.1", restored.OfficialUnity.PipelineVersion);
        foreach (var command in SpeedStress.CommonCommands) _ = OfficialUnityCli.Arguments("C:/project", 30, command.Command, command.ParametersJson);
        Assert.AreEqual("desk_probe", SpeedStress.CommonCommands.Single(c => c.Id == "state").Command);
    }
    [TestMethod]
    public async Task PortablePreparationPinsAllDependenciesAndRejectsTamperedCache()
    {
        string root = SampleData.TestDirectory(); var handler = new RegistryFixture();
        var repository = new OfficialUnityRepository(root, new HttpClient(handler));
        var release = await repository.Prepare("1.0.0-beta.10", "0.7.0-exp.1", null, default);
        Assert.AreEqual(2, release.OfficialUnity!.Packages.Length);
        Assert.IsTrue(await ReleaseRepository.Valid(release, default));
        var again = await repository.Prepare("1.0.0-beta.10", "0.7.0-exp.1", null, default);
        Assert.AreEqual(release.CliPath, again.CliPath);
        string trial = Path.Combine(root, "trial"), fixture = Path.Combine(root, "fixture.txt"); File.WriteAllText(fixture, "shared fixture");
        await LocalSpeedCoordinator.Prepare(trial, release, fixture, default);
        Assert.IsTrue(File.Exists(Path.Combine(trial, "release", "cli", "unity.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(trial, "release", "cli", "unity-bridge.exe")));
        foreach (var package in release.OfficialUnity.Packages)
            Assert.AreEqual(package.TreeSha256, await CliDistribution.TreeHash(Path.Combine(trial, "release", "packages", package.Name), default));
        File.AppendAllText(Path.Combine(release.OfficialUnity.Packages[0].Folder, "package.json"), " ");
        Assert.IsFalse(await ReleaseRepository.Valid(release, default));
    }
    [TestMethod]
    public async Task EditorDependenciesArePinnedWithoutReplacingRequestedPipeline()
    {
        string root = SampleData.TestDirectory(), editor = Path.Combine(root, "Editor", "Unity.exe");
        string builtIns = Path.Combine(root, "Editor", "Data", "Resources", "PackageManager", "BuiltInPackages");
        Directory.CreateDirectory(builtIns); File.WriteAllText(editor, "test editor");
        foreach (var (name, version) in new[] { ("com.unity.test-framework", "1.6.0"), ("com.unity.pipeline", "0.9.0") })
        {
            string folder = Path.Combine(builtIns, name); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(new { name, version }));
        }
        var release = await new OfficialUnityRepository(root, new HttpClient(new RegistryFixture())).Prepare("1.0.0-beta.10", "0.7.0-exp.1", null, default, editor);
        var dependency = release.OfficialUnity!.Packages.Single(p => p.Name == "com.unity.test-framework");
        Assert.AreEqual("1.6.0", dependency.Version); Assert.AreEqual("editor-bundled", dependency.Source);
        Assert.AreEqual("0.7.0-exp.1", release.OfficialUnity.Packages.Single(p => p.Name == "com.unity.pipeline").Version);
        Assert.IsTrue(await ReleaseRepository.Valid(release, default));
    }
    [TestMethod]
    public void PipelineDiscoveryRejectsStaleWrongProcessAndChangedPort()
    {
        string root = SampleData.TestDirectory(), folder = Path.Combine(root, "Library", "Pipeline");
        Directory.CreateDirectory(folder);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var target = new BridgeTarget(root, process.MainModule!.FileName, "unused", "unused", process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()), "0.7.0-exp.1");
        File.WriteAllText(Path.Combine(folder, ".unity-pipeline-port"), JsonSerializer.Serialize(new { projectPath = root, pid = process.Id, port = 54321, token = "do-not-export" }));
        void Ready(int pid, long timestamp) => File.WriteAllText(Path.Combine(root, "Library", "desk-pipeline-ready.json"),
            JsonSerializer.Serialize(new { projectPath = root, pid, timestamp, connectorVersion = "0.7.0-exp.1", unityVersion = "6000.3.23f1", compileErrors = false, state = "ready" }));
        var discovery = new PipelineDiscovery(); Ready(process.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var observed = discovery.Find(target); Assert.AreEqual(process.Id, observed.Pid);
        Assert.DoesNotContain("do-not-export", JsonSerializer.Serialize(observed));
        Assert.AreEqual(BridgeFailure.PortChanged, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target, 54320)).Failure);
        Assert.AreEqual(BridgeFailure.WrongResponse, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target with { RequiredConnectorVersion = "0.1.0" })).Failure);
        Ready(process.Id + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.AreEqual(BridgeFailure.WrongProcess, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target)).Failure);
        Ready(process.Id, DateTimeOffset.UtcNow.AddSeconds(-20).ToUnixTimeMilliseconds());
        Assert.AreEqual(BridgeFailure.StaleInstance, Assert.ThrowsExactly<BridgeException>(() => discovery.Find(target)).Failure);
    }
    [TestMethod]
    public async Task ArtifactHashMismatchStopsBeforeAnythingCanBeExecuted()
    {
        var repository = new OfficialUnityRepository(SampleData.TestDirectory(), new HttpClient(new RegistryFixture { CorruptBinary = true }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => repository.Prepare("1.0.0-beta.10", "0.7.0-exp.1", null, default));
    }
    [TestMethod]
    public async Task ExtractionRejectsTraversalAndSymbolicLinks()
    {
        foreach (bool link in new[] { false, true })
        {
            string root = SampleData.TestDirectory(), archive = Path.Combine(root, "bad.tgz");
            using (var file = File.Create(archive)) using (var zip = new GZipStream(file, CompressionLevel.Fastest)) using (var tar = new TarWriter(zip))
            {
                var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, link ? "package/link" : "package/../../escape.txt");
                if (link) entry.LinkName = "../../escape"; else entry.DataStream = new MemoryStream([1]);
                tar.WriteEntry(entry);
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfficialUnityRepository.Extract(archive, Path.Combine(root, "out"), default));
            Assert.IsFalse(File.Exists(Path.Combine(root, "escape.txt")));
        }
    }
    private static ProcessResult Reply(string output) => new(ProcessOutcome.Exited, 0, output, "", 1, 5, DateTimeOffset.UtcNow);
    private sealed class RegistryFixture : HttpMessageHandler
    {
        public bool CorruptBinary { get; init; }
        public bool CancelBinary { get; init; }
        public int BinaryDownloads;
        private static readonly byte[] Binary = Encoding.UTF8.GetBytes("test fixture, not executable");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            byte[] response;
            if (url.EndsWith("latest.json", StringComparison.Ordinal)) response = JsonSerializer.SerializeToUtf8Bytes(new { version = "1.0.0-beta.10", binaries = new Dictionary<string, object>
                { ["win32-x64"] = new { filename = "unity-windows-x64.exe", sha256 = Convert.ToHexStringLower(SHA256.HashData(Binary)) } } });
            else if (url.EndsWith(".exe", StringComparison.Ordinal))
            { BinaryDownloads++; if (CancelBinary) throw new OperationCanceledException(); response = CorruptBinary ? [0] : Binary; }
            else
            {
                string name = url.Contains("com.unity.pipeline", StringComparison.Ordinal) ? "com.unity.pipeline" : "com.unity.test-framework";
                string version = name == "com.unity.pipeline" ? "0.7.0-exp.1" : "1.1.33";
                var dependencies = name == "com.unity.pipeline" ? new Dictionary<string, string> { ["com.unity.test-framework"] = "1.1.33", ["com.unity.modules.uielements"] = "1.0.0" } : [];
                byte[] package = JsonSerializer.SerializeToUtf8Bytes(new { name, version, dependencies });
                using var bytes = new MemoryStream();
                using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true)) using (var tar = new TarWriter(gzip, leaveOpen: true))
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/package.json") { DataStream = new MemoryStream(package), ModificationTime = DateTimeOffset.UnixEpoch });
                byte[] archive = bytes.ToArray();
                response = url.EndsWith(".tgz", StringComparison.Ordinal) ? archive : JsonSerializer.SerializeToUtf8Bytes(new { versions = new Dictionary<string, object> { [version] = new { dist = new
                    { tarball = "https://download.packages.unity.com/" + name + "/-/" + name + "-" + version + ".tgz", shasum = Convert.ToHexStringLower(SHA1.HashData(archive)) } } } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) });
        }
    }
}
