using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class SpeedBenchTests
{
    [TestMethod]
    public void PlanUsesIndependentConditionBlocksBalancedOrderAndRecordedSeed()
    {
        var options = new SpeedOptions(Repeats: 6, Seed: 42, Experiments: ["F01", "F02", "F03"]);
        var first = SpeedProtocol.Schedule(options, ["v0.2.0", "v0.2.1"]);
        var second = SpeedProtocol.Schedule(options, ["v0.2.0", "v0.2.1"]);
        Assert.AreEqual(96, first.Length); Assert.AreEqual(48, first.Select(t => t.Block).Distinct().Count());
        CollectionAssert.AreEqual(first.Select(t => (t.Block, t.Tag, t.Variant)).ToArray(), second.Select(t => (t.Block, t.Tag, t.Variant)).ToArray());
        foreach (var condition in first.GroupBy(t => (t.Experiment, t.Variant)))
        {
            Assert.AreEqual(3, condition.GroupBy(t => t.Block).Count(g => g.First().Tag == "v0.2.0"));
            Assert.IsTrue(condition.GroupBy(t => t.Block).All(g => g.Count() == 2));
        }
        Assert.ThrowsExactly<ArgumentException>(() => SpeedProtocol.Schedule(options, ["same", "same"]));
        Assert.ThrowsExactly<ArgumentException>(() => new SpeedOptions(Calls: 0).Validate());
    }
    [TestMethod]
    public async Task GuestEntryRefusesHostExecutionBeforeCreatingFiles()
    {
        string root = SampleData.TestDirectory(), result = Path.Combine(root, "result.json");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SpeedGuest.RunFile(Path.Combine(root, "request.json"), result));
        Assert.IsFalse(File.Exists(result));
    }
    [TestMethod]
    public async Task ClockCollectsOutputsAndPreservesExitTimeoutCancellationAndHashFailures()
    {
        string root = SampleData.TestDirectory();
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessCommand Command(string script, int limit = 1024 * 1024) => new(Guid.NewGuid(), shell,
            ["-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], root, OutputLimit: limit);
        var runner = new TimedProcessRunner();
        var ok = await runner.RunAsync(Command("Start-Sleep -Milliseconds 100; [Console]::Write('finished'); [Console]::Error.Write('diagnostic')"), TimeSpan.FromSeconds(15));
        Assert.AreEqual(ProcessOutcome.Exited, ok.Outcome); Assert.AreEqual(0, ok.ExitCode); Assert.AreEqual("finished", ok.Output); Assert.Contains("diagnostic", ok.Error);
        Assert.IsTrue(ok.ElapsedMilliseconds >= 100); Assert.IsTrue(runner.LastCompletedTimestamp > runner.LastStartedTimestamp);
        Assert.AreEqual(7, (await runner.RunAsync(Command("exit 7"), TimeSpan.FromSeconds(15))).ExitCode);
        Assert.AreEqual(ProcessOutcome.TimedOut, (await runner.RunAsync(Command("Start-Sleep -Seconds 20"), TimeSpan.FromMilliseconds(300))).Outcome);
        Assert.AreEqual(ProcessOutcome.OutputLimit, (await runner.RunAsync(Command("[Console]::Write(('x' * 50000))", 1024), TimeSpan.FromSeconds(15))).Outcome);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.RunAsync(Command("exit 0"), TimeSpan.FromSeconds(5), cancellationToken: cancelled.Token));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunAsync(Command("exit 0") with { ExpectedSha256 = new string('0', 64) }, TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task ArchivesRejectTraversalDuplicatesAndSymlinks()
    {
        foreach (string attack in new[] { "../escape.txt", "a/../../escape.txt", "x:stream", "a\\..\\escape.txt" })
        {
            string root = SampleData.TestDirectory(), zip = Path.Combine(root, "input.zip");
            using (var file = ZipFile.Open(zip, ZipArchiveMode.Create)) using (var writer = new StreamWriter(file.CreateEntry(attack).Open())) writer.Write("bad");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SpeedFiles.Extract(zip, Path.Combine(root, "out")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "escape.txt")));
        }
        string duplicateRoot = SampleData.TestDirectory(), duplicateZip = Path.Combine(duplicateRoot, "input.zip");
        using (var file = ZipFile.Open(duplicateZip, ZipArchiveMode.Create)) { file.CreateEntry("A.txt"); file.CreateEntry("a.txt"); }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SpeedFiles.Extract(duplicateZip, Path.Combine(duplicateRoot, "out")));
        string linkZip = Path.Combine(duplicateRoot, "link.zip");
        using (var file = ZipFile.Open(linkZip, ZipArchiveMode.Create))
        {
            var entry = file.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("../outside");
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SpeedFiles.Extract(linkZip, Path.Combine(duplicateRoot, "link-out")));
    }
    private sealed class FakeHost : IHostCommands
    {
        public required VmProfile Profile { get; init; }
        public List<string[]> Calls { get; } = [];
        public bool WrongOwner, FailRestore, FailResultNonce;
        public CancellationTokenSource? CancelAfterStart;
        public string State = "poweroff";
        private string cable = "on";
        private GuestRequest? request;
        private TaskCompletionSource? granted;
        public async Task<HostReply> Run(string executable, string[] args, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls.Add(args);
            if (args[0] == "--version") return new(0, Profile.ManagerVersion, "");
            if (args[0] == "clonevm") return new(1, "", "clone boundary reached without creating a VM");
            if (args[0] == "list") return new(0, "", "");
            if (args[0] == "getextradata") return new(0, "Value: " + (WrongOwner ? "foreign" : Profile.OwnershipToken), "");
            if (args[0] == "showmediuminfo") return new(0, "Type=\"normal (base)\"", "");
            if (args[0] == "showvminfo")
            {
                var info = new Dictionary<string, string>(Profile.Settings) { ["CfgFile"] = Path.Combine(Profile.VmDirectory, "machine.vbox"),
                    ["VMState"] = State, ["CurrentSnapshotUUID"] = Profile.SnapshotId, ["cableconnected1"] = cable };
                return new(0, string.Join('\n', info.Select(p => p.Key + "=\"" + p.Value.Replace("\\", "\\\\") + "\"")), "");
            }
            if (args[0] == "snapshot") { if (FailRestore) return new(1, "", "restore injected failure"); State = "poweroff"; cable = "on"; return new(0, "", ""); }
            if (args[0] == "startvm") { State = "running"; CancelAfterStart?.Cancel(); return new(0, "", ""); }
            if (args[0] == "controlvm") { if (args[2] == "setlinkstate1") cable = "off"; else State = "poweroff"; return new(0, "", ""); }
            if (args[0] == "guestcontrol" && args[2] == "copyto")
            {
                string file = args[^1];
                if (Path.GetFileName(file) == "input.zip")
                {
                    using var zip = ZipFile.OpenRead(file); using var reader = new StreamReader(zip.GetEntry("request.json")!.Open());
                    request = JsonSerializer.Deserialize<GuestRequest>(await reader.ReadToEndAsync(), SpeedProtocol.Json)!;
                }
                else { Assert.AreEqual("off", cable); granted!.TrySetResult(); }
                return new(0, "", "");
            }
            if (args[0] == "guestcontrol" && args[2] == "copyfrom")
            {
                Assert.IsNotNull(request);
                if (args[^2].EndsWith("ready.json")) await SpeedFiles.Write(args[^1], new GuestReady(request.RunId, request.Trial.Id, request.GuestNonce, request.UnityVersion + "_test"), ct);
                else
                {
                    int n = request.Trial.Variant == "first" ? 1 : request.Options.Calls;
                    var response = Result(request, Enumerable.Range(0, n).Select(i => new SpeedSample(i, request.Trial.Tag == "v0.2.0" ? 100 : 80, 100)).ToArray());
                    if (FailResultNonce) response = response with { GuestNonce = "wrong" };
                    await SpeedFiles.Write(args[^1], response, ct);
                }
                return new(0, "", "");
            }
            if (args.Contains("--vm-trial")) { granted = new(TaskCreationOptions.RunContinuationsAsynchronously); await granted.Task.WaitAsync(ct); return new(0, "", ""); }
            return new(0, "", "");
        }
    }
    private static GuestResult Result(GuestRequest request, SpeedSample[] samples) => new(request.RunId, request.Trial.Id, request.GuestNonce,
        SpeedProtocol.Schema, "success", null, 1000, samples.Average(s => s.Milliseconds), 1, samples, 123, "synthetic-test-guest",
        request.UnityVersion + "_test", request.CliSha256, request.ConnectorSha256, request.FixtureSha256, 10000000, [],
        CliTreeSha256: request.CliTreeSha256, ReportedConnectorVersion: request.ExpectedReportedConnectorVersion ?? request.ConnectorVersion);
    private static async Task<(string Root, VmProfile Profile, SpeedRelease[] Releases, string Worker)> Fixture()
    {
        string root = SampleData.TestDirectory(), vm = Path.Combine(root, "vm"), worker = Path.Combine(root, "worker"); Directory.CreateDirectory(vm); Directory.CreateDirectory(Path.Combine(worker, "Assets"));
        await File.WriteAllTextAsync(Path.Combine(worker, "UnityBridgeDesk.Worker.exe"), "never executed");
        await File.WriteAllTextAsync(Path.Combine(worker, "coreclr.dll"), "fixture"); await File.WriteAllTextAsync(Path.Combine(worker, "Assets", "DeskProbe.cs.txt"), "fixture");
        string disk = Path.Combine(vm, "base.vdi"); await File.WriteAllTextAsync(disk, "disk fixture");
        await File.WriteAllTextAsync(Path.Combine(vm, "machine.vbox"), "<VirtualBox><MediaRegistry><HardDisks><HardDisk location=\"base.vdi\" /></HardDisks></MediaRegistry></VirtualBox>");
        string[] keys = ["cpus", "memory", "ostype", "firmware", "chipset", "vram", "accelerate3d", "clipboard", "draganddrop", "nic1", "nic2", "nic3", "nic4", "nic5", "nic6", "nic7", "nic8", "cableconnected1"];
        var settings = keys.ToDictionary(k => k, k => k == "nic1" ? "nat" : k.StartsWith("nic") ? "none" : k == "cableconnected1" ? "on" : "fixed");
        var profile = new VmProfile(Environment.ProcessPath!, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString("N"), vm, settings,
            new() { [disk] = await SpeedFiles.Hash(disk) }, "7.2-test");
        var releases = new List<SpeedRelease>();
        foreach (var version in new[] { "0.2.0", "0.2.1" })
        {
            string dir = Path.Combine(root, version), connector = Path.Combine(dir, "connector"); Directory.CreateDirectory(connector);
            string cli = Path.Combine(dir, "cli.exe"); File.Copy(Environment.ProcessPath!, cli);
            await File.WriteAllTextAsync(Path.Combine(connector, "package.json"), JsonSerializer.Serialize(new { name = LocalInspector.ConnectorPackageName, version }));
            var inspected = await new LocalInspector().InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder);
            releases.Add(new("v" + version, version, new string('a', 40), cli, connector, await SpeedFiles.Hash(cli), inspected.Sha256!, "fixture", null));
        }
        return (root, profile, releases.ToArray(), worker);
    }
    [TestMethod]
    public async Task NonWindowsVmIsRejectedBeforeClone()
    {
        var f = await Fixture();
        var settings = new Dictionary<string, string>(f.Profile.Settings) { ["ostype"] = "Ubuntu_64" };
        var fake = new FakeHost { Profile = f.Profile with { Settings = settings } };
        await Assert.ThrowsExactlyAsync<IOException>(() => new VirtualBoxHost(f.Profile.ManagerPath, fake)
            .CloneBaseline(f.Profile.VmId, f.Root, null, CancellationToken.None));
        Assert.IsFalse(fake.Calls.Any(a => a[0] is "clonevm" or "modifyvm" or "snapshot"));
        foreach (string supported in new[] { "Windows 11 (64-bit)", "Windows10_64" })
        {
            settings["ostype"] = supported;
            var windows = new FakeHost { Profile = f.Profile with { Settings = settings } };
            await Assert.ThrowsExactlyAsync<IOException>(() => new VirtualBoxHost(f.Profile.ManagerPath, windows)
                .CloneBaseline(f.Profile.VmId, f.Root, null, CancellationToken.None));
            Assert.IsTrue(windows.Calls.Any(a => a[0] == "clonevm"));
        }
    }
    [TestMethod]
    public async Task CoordinatorRestoresEveryTrialDisablesNetworkBeforeGrantAndExportsNoPassword()
    {
        var f = await Fixture(); var fake = new FakeHost { Profile = f.Profile }; var host = new VirtualBoxHost(f.Profile.ManagerPath, fake);
        var run = await new SpeedCoordinator(f.Root, host).Run(f.Profile, "guest", "fixture-password", "6000.3.23f1", f.Releases, new(), f.Worker, null, CancellationToken.None);
        Assert.AreEqual(8, run.Results.Length); Assert.IsTrue(run.Results.All(r => r.Status == "success" && r.ResetVerified));
        Assert.AreEqual(16, fake.Calls.Count(a => a[0] == "snapshot")); Assert.AreEqual(8, fake.Calls.Count(a => a.Contains("setlinkstate1")));
        Assert.IsFalse(fake.Calls.SelectMany(a => a).Contains("fixture-password"));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(f.Root, "speed"), "*.secret").Length);
        Assert.IsTrue(SpeedAnalysis.Pairs(run).All(p => Math.Abs(p.ReductionPercent!.Value - 20) < .001));
        Assert.IsTrue(SpeedAnalysis.Csv(run).Contains("sampleMs"));
    }
    [TestMethod]
    public async Task ForeignVmAndRestoreFailureCannotFallBackToHostOrContinueTrials()
    {
        var f = await Fixture(); var foreign = new FakeHost { Profile = f.Profile, WrongOwner = true };
        await Assert.ThrowsExactlyAsync<IOException>(() => new VirtualBoxHost(f.Profile.ManagerPath, foreign).Restore(f.Profile, CancellationToken.None));
        Assert.IsFalse(foreign.Calls.Any(a => a[0] is "snapshot" or "controlvm" or "startvm"));
        var broken = new FakeHost { Profile = f.Profile, FailRestore = true };
        var run = await new SpeedCoordinator(f.Root, new(f.Profile.ManagerPath, broken)).Run(f.Profile, "guest", "pw", "6000.3.23f1", f.Releases, new(), f.Worker, null, CancellationToken.None);
        Assert.AreEqual("isolation-failed", run.Status); Assert.AreEqual(1, run.Results.Length); Assert.IsFalse(broken.Calls.Any(a => a[0] == "startvm"));
    }
    [TestMethod]
    public async Task ForeignResultsAreRejectedAndNeverEnterSpeedSummary()
    {
        var f = await Fixture(); var fake = new FakeHost { Profile = f.Profile, FailResultNonce = true };
        var run = await new SpeedCoordinator(f.Root, new(f.Profile.ManagerPath, fake)).Run(f.Profile, "guest", "pw", "6000.3.23f1", f.Releases, new(Repeats: 1), f.Worker, null, CancellationToken.None);
        Assert.IsTrue(run.Results.All(r => r.Status == "failed")); Assert.IsTrue(SpeedAnalysis.Summaries(run).All(s => s.Success == 0 && s.Mean is null));
        Assert.IsTrue(SpeedAnalysis.Pairs(run).All(p => p.CompleteBlocks == 0));
        var request = new GuestRequest(Guid.NewGuid(), SpeedProtocol.Schedule(new(), ["a", "b"])[0], new(), "6000.3.23f1", f.Profile.VmId, f.Profile.SnapshotId,
            "cli", "connector", "0.2.0", "fixture", "nonce");
        Assert.ThrowsExactly<IOException>(() => SpeedCoordinator.ValidateResult(request, Result(request, [new(0, 100, 1)]) with { WorkMs = 1 }));
    }
    [TestMethod]
    public async Task BaselineTamperingStopsBeforeStartingAnyVm()
    {
        var f = await Fixture(); var fake = new FakeHost { Profile = f.Profile }; File.AppendAllText(f.Profile.BaselineFiles.Keys.Single(), "tampered");
        await Assert.ThrowsExactlyAsync<IOException>(() => new VirtualBoxHost(f.Profile.ManagerPath, fake).VerifyBaseline(f.Profile, CancellationToken.None));
        Assert.IsFalse(fake.Calls.Any(a => a[0] is "snapshot" or "startvm"));
    }
    [TestMethod]
    public async Task CancellationStillPowersOffAndRestoresOwnedVmAndErasesTemporaryPassword()
    {
        var f = await Fixture(); using var cancel = new CancellationTokenSource(); var fake = new FakeHost { Profile = f.Profile, CancelAfterStart = cancel };
        var run = await new SpeedCoordinator(f.Root, new(f.Profile.ManagerPath, fake)).Run(f.Profile, "guest", "secret", "6000.3.23f1", f.Releases, new(), f.Worker, null, cancel.Token);
        Assert.AreEqual("cancelled", run.Status); Assert.AreEqual(1, run.Results.Length); Assert.IsTrue(run.Results[0].ResetVerified);
        Assert.AreEqual("poweroff", fake.State); Assert.AreEqual(2, fake.Calls.Count(a => a[0] == "snapshot"));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(f.Root, "speed"), "*.secret").Length);
    }
    private sealed class ApiHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Responses { get; } = [];
        public int Requests;
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; string url = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(!Offline && Responses.ContainsKey(url) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new ByteArrayContent(Responses.GetValueOrDefault(url) ?? []) });
        }
    }
    [TestMethod]
    public async Task ReleaseLookupSupportsNewVersionsPinsCommitAndReusesVerifiedCacheOffline()
    {
        string root = SampleData.TestDirectory(); var api = new ApiHandler(); using var http = new HttpClient(api);
        string cli = "https://github.com/zjxps2007/UnityBridge/releases/download/v0.3.0/unity-bridge-windows-amd64.exe";
        string commit = new('a', 40); byte[] executable = await File.ReadAllBytesAsync(Environment.ProcessPath!);
        string digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(executable));
        api.Responses["https://api.github.com/repos/zjxps2007/UnityBridge/releases?per_page=100&page=1"] = JsonSerializer.SerializeToUtf8Bytes(new[] {
            new { id = 33, tag_name = "v0.3.0", name = "New release", prerelease = false, draft = false, html_url = "https://github.com/zjxps2007/UnityBridge/releases/tag/v0.3.0",
                assets = new[] { new { name = "unity-bridge-windows-amd64.exe", browser_download_url = cli, digest } } } });
        api.Responses["https://api.github.com/repos/zjxps2007/UnityBridge/commits/v0.3.0"] = JsonSerializer.SerializeToUtf8Bytes(new { sha = commit });
        api.Responses[cli] = executable;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("repo/unity-bridge-connector/package.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { name = LocalInspector.ConnectorPackageName, version = "0.3.0" }));
            api.Responses["https://codeload.github.com/zjxps2007/UnityBridge/zip/" + commit] = memory.ToArray();
        }
        var repository = new ReleaseRepository(root, http); var choice = (await repository.List(CancellationToken.None)).Single();
        var prepared = await repository.Prepare(choice, null, CancellationToken.None); Assert.AreEqual(commit, prepared.Commit); Assert.AreEqual("0.3.0", prepared.Version);
        int requests = api.Requests; api.Offline = true;
        Assert.AreEqual(choice.Tag, (await repository.CachedList()).Single().Tag);
        Assert.AreEqual(prepared.CliPath, (await repository.Prepare(choice, null, CancellationToken.None)).CliPath); Assert.AreEqual(requests, api.Requests);
        await File.WriteAllTextAsync(prepared.CliPath, "changed");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => repository.Prepare(choice, null, CancellationToken.None));
        Assert.AreEqual("changed", await File.ReadAllTextAsync(prepared.CliPath));
    }
}
