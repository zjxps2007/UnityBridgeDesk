using System.IO.Compression;
using System.Formats.Tar;
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
public sealed class GoUnityTests
{
    [TestMethod]
    public async Task ReleaseIsPinnedVerifiedAndTemporaryIncludingPrepareOnly()
    {
        string root = SampleData.TestDirectory(); var handler = new ReleaseFixture(); using var http = new HttpClient(handler);
        var space = await GoToolWorkspace.Create(root, null, default);
        var release = await space.Prepare(new(), null, default, http);
        Assert.AreEqual("Go unity-cli v0.4.1", release.Tag); Assert.AreEqual(new string('a', 40), release.Commit);
        Assert.IsTrue(space.Contains(release)); Assert.IsTrue(await ReleaseRepository.Valid(release, default));
        Assert.AreEqual(3, release.GoUnity!.Packages!.Length);
        Assert.IsTrue(release.GoUnity.Packages.Any(p => p.Name == GoUnityDependencies.TestFramework));
        Assert.AreEqual(release, await space.Prepare(new(), null, default, http)); Assert.AreEqual(1, handler.BinaryDownloads);
        string trial = Path.Combine(root, "copy"), fixture = Path.Combine(root, "fixture.txt"); File.WriteAllText(fixture, "fixture");
        await LocalSpeedCoordinator.Prepare(trial, release, fixture, default);
        Assert.AreEqual(release.CliSha256, await SpeedFiles.Hash(Path.Combine(trial, "release", "cli", "unity-cli.exe")));
        foreach (var package in release.GoUnity.Packages)
            Assert.AreEqual(package.TreeSha256, await CliDistribution.TreeHash(Path.Combine(trial, "release", "packages", package.Name), default));
        Assert.IsFalse(await GoUnityDependencies.Valid(release.GoUnity.Packages.Where(p => p.Name != "com.unity.ext.nunit").ToArray(), default));
        string dependencyFile = Path.Combine(release.GoUnity.Packages[0].Folder, "package.json"), original = File.ReadAllText(dependencyFile);
        File.AppendAllText(dependencyFile, " "); Assert.IsFalse(await ReleaseRepository.Valid(release, default)); File.WriteAllText(dependencyFile, original);
        File.AppendAllText(Path.Combine(release.ConnectorPath, "package.json"), " "); Assert.IsFalse(await ReleaseRepository.Valid(release, default));
        await space.DisposeAsync(); Assert.IsFalse(Directory.Exists(space.Root));
        await using var fresh = await GoToolWorkspace.Create(root, null, default);
        var next = await fresh.Prepare(new(), null, default, http); Assert.AreNotEqual(release.CliPath, next.CliPath); Assert.AreEqual(2, handler.BinaryDownloads);
    }
    [TestMethod]
    public async Task BlankProjectGetsEditorTestFrameworkAndItsTransitiveDependencies()
    {
        string root = SampleData.TestDirectory(), editor = Path.Combine(root, "Editor", "Unity.exe");
        string builtIns = Path.Combine(root, "Editor", "Data", "Resources", "PackageManager", "BuiltInPackages");
        Directory.CreateDirectory(builtIns); File.WriteAllText(editor, "test editor");
        foreach (var (name, version) in new[] { (GoUnityDependencies.TestFramework, "1.6.0"), ("com.unity.ext.nunit", "2.0.5") })
        {
            string folder = Path.Combine(builtIns, name); Directory.CreateDirectory(folder);
            var dependencies = name == GoUnityDependencies.TestFramework ? new Dictionary<string, string> { ["com.unity.ext.nunit"] = "2.0.3", ["com.unity.modules.imgui"] = "1.0.0" } : [];
            File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(new { name, version, dependencies }));
        }
        using var http = new HttpClient(new ReleaseFixture()); await using var space = await GoToolWorkspace.Create(root, null, default);
        var release = await space.Prepare(new(), null, default, http, editor);
        var packages = release.GoUnity!.Packages!;
        Assert.AreEqual(3, packages.Length);
        Assert.AreEqual("1.6.0", packages.Single(p => p.Name == GoUnityDependencies.TestFramework).Version);
        Assert.AreEqual("2.0.5", packages.Single(p => p.Name == "com.unity.ext.nunit").Version);
        Assert.AreEqual(2, packages.Count(p => p.Source == "editor-bundled"));
        Assert.IsTrue(await ReleaseRepository.Valid(release, default));
    }
    [TestMethod]
    public async Task BadPublisherHashAndArchiveTraversalCannotLeaveAnInstall()
    {
        foreach (var fixture in new[] { new ReleaseFixture { BadHash = true }, new ReleaseFixture { Traversal = true }, new ReleaseFixture { WrongPackage = true } })
        {
            string root = SampleData.TestDirectory(); await using var space = await GoToolWorkspace.Create(root, null, default);
            using var http = new HttpClient(fixture);
            if (fixture.Traversal) await Assert.ThrowsExactlyAsync<InvalidDataException>(() => space.Prepare(new(), null, default, http));
            else await Assert.ThrowsAsync<IOException>(() => space.Prepare(new(), null, default, http));
            Assert.IsFalse(Directory.Exists(space.Root)); Assert.IsFalse(File.Exists(Path.Combine(root, "escape.txt")));
        }
    }
    [TestMethod]
    public void ArgumentsUseMillisecondsAndPreserveInputWithoutShellEscaping()
    {
        string project = "C:/한글 공백/project", json = """{"action":"echo","nonce":"quoted\"value"}""";
        var args = GoUnityCli.Arguments(project, 30, "desk_probe", json);
        CollectionAssert.AreEqual(new[] { "--project", project, "--timeout", "30000", "desk_probe", "--params", json }, args);
        CollectionAssert.AreEqual(new[] { "--project", project, "--timeout", "30000", "exec" }, GoUnityCli.Arguments(project, 30, "desk_probe", "{}", true));
        foreach (var command in SpeedStress.CommonCommands) _ = GoUnityCli.Arguments(project, 30, command.Command, command.ParametersJson);
        Assert.ThrowsExactly<SpeedMeasurementException>(() => GoUnityCli.Arguments(project, 30, "get_editor_state", "{}"));
        Assert.ThrowsExactly<SpeedMeasurementException>(() => GoUnityCli.Arguments(project, 30, "exec", """{"code":"return 42;","allow-async":true}"""));
    }
    [TestMethod]
    public void NativeDataCannotHideExitErrorsOrWrongTargets()
    {
        var response = Reply("""{"nonce":"n","projectPath":"C:/project","pid":42,"value":42}""");
        var data = GoUnityCli.ReadData(response); Assert.AreEqual(42, SpeedFailure.ValidateIdentity(data, "n", 42, "C:/project").GetInt32());
        Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ValidateIdentity(data, "other", 42, "C:/project"));
        Assert.ThrowsExactly<SpeedMeasurementException>(() => SpeedFailure.ValidateIdentity(data, "n", 43, "C:/project"));
        Assert.AreEqual(42, GoUnityCli.ReadData(Reply("42")).GetInt32());
        Assert.AreEqual("invalid-response", Assert.ThrowsExactly<SpeedMeasurementException>(() => GoUnityCli.ReadData(Reply(""))).Kind);
        Assert.AreEqual("timeout", Assert.ThrowsExactly<SpeedMeasurementException>(() => GoUnityCli.ReadData(response with { Outcome = ProcessOutcome.TimedOut })).Kind);
        foreach (var (error, kind) in new[] { ("Error: Compile error: x", "compile-error"), ("Error: Runtime error: x", "runtime-error"), ("no Unity instances running", "instance-unavailable") })
            Assert.AreEqual(kind, Assert.ThrowsExactly<SpeedMeasurementException>(() => GoUnityCli.ReadData(response with { ExitCode = 1, Error = error })).Kind);
    }
    [TestMethod]
    public async Task PrivateDiscoveryAndCacheOnlyContainVerifiedTarget()
    {
        string root = SampleData.TestDirectory(); var tool = new GoUnityToolchain("0.4.1", "0.4.1", "v0.4.1");
        var instance = new BridgeInstance("C:/project", 123, 8090, "ready", "6000.3.23f1", "0.4.1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, DateTimeOffset.UtcNow);
        string? original = Environment.GetEnvironmentVariable("USERPROFILE");
        await GoUnityCli.PrepareConnection(root, instance, tool, default);
        string profile = GoUnityCli.EnvironmentFor(root)["USERPROFILE"];
        Assert.AreEqual(original, Environment.GetEnvironmentVariable("USERPROFILE"));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, ".unity-cli", "instances", "desk.json")));
        Assert.AreEqual(instance.Port, json.RootElement.GetProperty("port").GetInt32());
        Assert.AreEqual(instance.ProjectPath, json.RootElement.GetProperty("projectPath").GetString());
        using var cache = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, ".unity-cli", "version-check.json")));
        Assert.IsTrue(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cache.RootElement.GetProperty("checked_at").GetInt64()) < 10);
        Assert.IsFalse(cache.RootElement.GetProperty("outdated").GetBoolean());
        await Assert.ThrowsExactlyAsync<SpeedMeasurementException>(() => GoUnityCli.PrepareConnection(root, instance with { ConnectorVersion = "other" }, tool, default));
    }
    [TestMethod]
    public async Task CleanupChecksProjectAndPidAndPreservesForeignRecords()
    {
        string root = SampleData.TestDirectory(), instances = Path.Combine(root, "instances"); Directory.CreateDirectory(instances);
        await SpeedFiles.Write(Path.Combine(root, "go-cli-environment.json"), new { });
        await SpeedFiles.Write(Path.Combine(root, "editor-process.json"), new LocalProcessIdentity(int.MaxValue, DateTimeOffset.UtcNow));
        foreach (string name in new[] { "owned.json", "owned.json.tmp" })
            await SpeedFiles.Write(Path.Combine(instances, name), new { projectPath = Path.Combine(root, "project"), pid = int.MaxValue });
        await SpeedFiles.Write(Path.Combine(instances, "foreign.json"), new { projectPath = "C:/personal", pid = int.MaxValue });
        await SpeedFiles.Write(Path.Combine(instances, "wrong-pid.json"), new { projectPath = Path.Combine(root, "project"), pid = 9 });
        File.WriteAllText(Path.Combine(instances, "version-check.json"), "keep");
        await GoUnityCli.CleanInstance(root, instances);
        Assert.IsFalse(File.Exists(Path.Combine(instances, "owned.json"))); Assert.IsFalse(File.Exists(Path.Combine(instances, "owned.json.tmp")));
        Assert.AreEqual(3, Directory.GetFiles(instances).Length);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        await SpeedFiles.Write(Path.Combine(root, "editor-process.json"), new LocalProcessIdentity(current.Id, current.StartTime.ToUniversalTime()));
        await Assert.ThrowsExactlyAsync<IOException>(() => GoUnityCli.CleanInstance(root, instances));
    }
    [TestMethod]
    public void GoSelectionsAndReportsDoNotCollideWithBridgeVersionOrLegacyResults()
    {
        new GoUnitySelection().Validate(); new GoUnitySelection("v0.4.1").Validate();
        Assert.ThrowsExactly<ArgumentException>(() => new GoUnitySelection("../0.4.1").Validate());
        SpeedBenchWorkflow.ValidateSelection(1, false, true); SpeedBenchWorkflow.ValidateSelection(0, true, true); SpeedBenchWorkflow.ValidateSelection(6, true, true);
        Assert.ThrowsExactly<ArgumentException>(() => SpeedBenchWorkflow.ValidateSelection(0, false, true));
        Assert.ThrowsExactly<ArgumentException>(() => SpeedBenchWorkflow.ValidateSelection(7, true, true));
        var legacy = JsonSerializer.Deserialize<LocalSpeedSettings>("{}", SpeedProtocol.Json)!; Assert.IsNull(legacy.GoUnity);
        var saved = JsonSerializer.Deserialize<LocalSpeedSettings>(JsonSerializer.Serialize(legacy with { GoUnity = new(), BaselineTag = SpeedBenchWorkflow.GoLabel }), SpeedProtocol.Json)!;
        Assert.AreEqual(new GoUnitySelection(), saved.GoUnity);
        var run = SpeedReportSample.Create() with { Status = "completed", GoCleanup = new("failed", "owned", "locked") };
        Assert.Contains("Go", new SpeedReport(run).State); Assert.Contains("locked", new SpeedReport(run).Memo());
        var go = run.Releases[0] with { GoUnity = new("0.4.1", "0.4.1", "v0.4.1") };
        Assert.Contains("표준입력", new SpeedReport(run with { Releases = [go, ..run.Releases.Skip(1)] }).Memo());
    }
    private static ProcessResult Reply(string output) => new(ProcessOutcome.Exited, 0, output, "", 10, 1, DateTimeOffset.UtcNow);
    private sealed class ReleaseFixture : HttpMessageHandler
    {
        public bool BadHash, Traversal, WrongPackage;
        public int BinaryDownloads;
        private static readonly byte[] Binary = Encoding.UTF8.GetBytes("MZsynthetic-never-executed");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri; byte[] bytes;
            if (url.EndsWith("/releases/tags/v0.4.1", StringComparison.Ordinal)) bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                tag_name = "v0.4.1", draft = false, assets = new[] { new { name = "unity-cli-windows-amd64.exe", browser_download_url = GoUnityRepository.Repository + "/releases/download/v0.4.1/unity-cli-windows-amd64.exe",
                    digest = "sha256:" + (BadHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(Binary))) } }
            });
            else if (url.EndsWith("/commits/v0.4.1", StringComparison.Ordinal)) bytes = JsonSerializer.SerializeToUtf8Bytes(new { sha = new string('a', 40) });
            else if (url.EndsWith(".exe", StringComparison.Ordinal)) { BinaryDownloads++; bytes = Binary; }
            else if (url.StartsWith("https://codeload.github.com/youngwoocho02/unity-cli/zip/", StringComparison.Ordinal))
            {
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("source/unity-connector/package.json").Open()))
                        writer.Write(JsonSerializer.Serialize(new { name = WrongPackage ? "foreign" : GoUnityRepository.PackageName, version = "0.4.1", dependencies = new Dictionary<string, string> { ["com.unity.nuget.newtonsoft-json"] = "3.2.1" } }));
                    if (Traversal) { using var writer = new StreamWriter(zip.CreateEntry("source/unity-connector/../../escape.txt").Open()); writer.Write("reject"); }
                }
                bytes = memory.ToArray();
            }
            else if (request.RequestUri.Host is "packages.unity.com" or "download.packages.unity.com")
            {
                string name = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
                string version = name switch { "com.unity.test-framework" => "1.1.33", "com.unity.ext.nunit" => "1.0.6", "com.unity.nuget.newtonsoft-json" => "3.2.1", _ => throw new InvalidOperationException(name) };
                var dependencies = name == GoUnityDependencies.TestFramework ? new Dictionary<string, string> { ["com.unity.ext.nunit"] = "1.0.6" } : [];
                using var memory = new MemoryStream();
                using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) using (var tar = new TarWriter(gzip, leaveOpen: true))
                    tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/package.json") { DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { name, version, dependencies })), ModificationTime = DateTimeOffset.UnixEpoch });
                byte[] archive = memory.ToArray();
                bytes = url.EndsWith(".tgz", StringComparison.Ordinal) ? archive : JsonSerializer.SerializeToUtf8Bytes(new { versions = new Dictionary<string, object> { [version] = new { dist = new { tarball = "https://download.packages.unity.com/" + name + "/-/" + name + "-" + version + ".tgz", shasum = Convert.ToHexStringLower(SHA1.HashData(archive)) } } } });
            }
            else throw new InvalidOperationException("Unexpected URL " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
