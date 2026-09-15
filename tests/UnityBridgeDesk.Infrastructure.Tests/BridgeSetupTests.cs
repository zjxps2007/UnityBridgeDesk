using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class BridgeSetupTests
{
    private sealed class DownloadHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Payloads { get; } = [];
        public List<string> Requests { get; } = [];
        public Func<CancellationToken, Task>? BeforeResponse { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString(); Requests.Add(url);
            if (BeforeResponse is not null) await BeforeResponse(ct);
            ct.ThrowIfCancellationRequested();
            return new(Status) { Content = new ByteArrayContent(Payloads.GetValueOrDefault(url) ?? []) };
        }
    }
    private sealed record Fixture(string Root, DownloadHandler Handler, BridgeSetupRelease[] Releases, LocalCandidate[] Candidates);
    private static async Task<Fixture> CreateFixture()
    {
        string root = SampleData.TestDirectory(); var handler = new DownloadHandler();
        var definitions = new List<BridgeSetupRelease>(); var candidates = new List<LocalCandidate>();
        foreach (string version in new[] { "0.2.0", "0.2.1" })
        {
            string source = Path.Combine(root, "source", version); Directory.CreateDirectory(source);
            string cli = Path.Combine(source, "unity-bridge.exe");
            // Distinct, inspectable PE payloads; never executed by the setup service.
            byte[] executable = [..await File.ReadAllBytesAsync(Environment.ProcessPath!), ..Encoding.UTF8.GetBytes(version)];
            await File.WriteAllBytesAsync(cli, executable);
            string connector = Path.Combine(source, "unity-bridge-connector"); Directory.CreateDirectory(connector);
            await File.WriteAllTextAsync(Path.Combine(connector, "package.json"), JsonSerializer.Serialize(new { name = LocalInspector.ConnectorPackageName, version }));
            await File.WriteAllTextAsync(Path.Combine(connector, "fixture.cs"), "// source " + version);
            var observed = await new LocalInspector().InspectArtifactAsync(connector, ArtifactKind.ConnectorFolder);
            var definition = new BridgeSetupRelease(version, Convert.ToHexStringLower(SHA256.HashData(executable)), observed.Sha256!, new string(version.EndsWith('0') ? 'a' : 'b', 40));
            definitions.Add(definition);
            handler.Payloads[definition.CliUrl.ToString()] = executable;
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (string file in Directory.GetFiles(connector))
                { using var stream = zip.CreateEntry("UnityBridge-commit/unity-bridge-connector/" + Path.GetFileName(file)).Open(); stream.Write(File.ReadAllBytes(file)); }
            handler.Payloads[definition.SourceUrl.ToString()] = memory.ToArray();
            candidates.Add(new(DiscoveryKind.BridgeCli, cli, "misleading folder name", "fixture"));
            candidates.Add(new(DiscoveryKind.Connector, connector, version, "fixture", version));
        }
        return new(root, handler, definitions.ToArray(), candidates.ToArray());
    }
    private static BridgeEnvironmentSetup Service(Fixture fixture, HttpClient client) => new(Path.Combine(fixture.Root, "data"), client, fixture.Releases);

    [TestMethod]
    public async Task FreshSetupDownloadsVerifiesRegistersBothVersionsAndReusesThemOffline()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler);
        var setup = Service(f, client); var prepared = await setup.PrepareAsync([]);
        Assert.AreEqual(4, f.Handler.Requests.Count); Assert.AreEqual(2, prepared.Count);
        Assert.AreNotEqual(Path.GetDirectoryName(prepared[0].CliPath), Path.GetDirectoryName(prepared[1].CliPath));
        using var catalog = new CatalogService(Path.Combine(f.Root, "data")); await catalog.LoadAsync();
        Assert.IsTrue((await catalog.UsePreparedReleasesAsync(prepared)).Success);
        var ids = catalog.Document.Releases.Select(x => x.Id).ToArray();
        f.Handler.Status = HttpStatusCode.ServiceUnavailable;
        var reused = await setup.PrepareAsync([]);
        Assert.IsTrue((await catalog.UsePreparedReleasesAsync(reused)).Success);
        Assert.AreEqual(4, f.Handler.Requests.Count);
        CollectionAssert.AreEqual(ids, catalog.Document.Releases.Select(x => x.Id).ToArray());
        CollectionAssert.AreEqual(prepared.Select(x => x.CliPath).ToArray(), reused.Select(x => x.CliPath).ToArray());
        Assert.IsTrue(catalog.Document.Artifacts.All(x => x.MatchesRegistration && x.Path.StartsWith(Path.Combine(f.Root, "data", "releases"))));
    }
    [TestMethod]
    public async Task LocalDiscoveryIsMatchedByContentAndCopiedSoOriginalsCanMove()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler); f.Handler.Status = HttpStatusCode.Forbidden;
        var setup = Service(f, client);
        var prepared = await setup.PrepareAsync(f.Candidates.Reverse());
        Assert.AreEqual(0, f.Handler.Requests.Count);
        foreach (var item in f.Candidates.Where(x => x.Kind == DiscoveryKind.BridgeCli)) File.Delete(item.Path);
        var again = await setup.PrepareAsync([]);
        CollectionAssert.AreEqual(prepared.Select(x => x.CliPath).ToArray(), again.Select(x => x.CliPath).ToArray());
    }
    [TestMethod]
    public async Task CorruptManagedFilesArePreservedAndReplacedInANewVerifiedFolder()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler); var setup = Service(f, client);
        var first = await setup.PrepareAsync(f.Candidates);
        await File.WriteAllTextAsync(first[0].CliPath, "changed");
        var next = await setup.PrepareAsync(f.Candidates);
        Assert.AreNotEqual(first[0].CliPath, next[0].CliPath);
        Assert.AreEqual("changed", await File.ReadAllTextAsync(first[0].CliPath));
        Assert.AreEqual(first[1].CliPath, next[1].CliPath);
        Assert.AreEqual(0, f.Handler.Requests.Count);
    }
    [TestMethod]
    public async Task WrongDownloadDigestCannotPublishOrRegisterTheFailedVersion()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler);
        f.Handler.Payloads[f.Releases[0].CliUrl.ToString()] = Encoding.UTF8.GetBytes("incorrect exe");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Service(f, client).PrepareAsync([]));
        string root = Path.Combine(f.Root, "data", "releases");
        Assert.AreEqual(0, Directory.GetDirectories(root, ".preparing-*").Length);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(root, "0.2.0")).Length);
    }
    [TestMethod]
    public async Task CancellationCleansStagingAndRetryCompletesNormally()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler); using var ct = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.BeforeResponse = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var task = Service(f, client).PrepareAsync([], cancellationToken: ct.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); ct.Cancel();
        try { await task; Assert.Fail("Cancellation was ignored."); } catch (OperationCanceledException) { }
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(f.Root, "data", "releases"), ".preparing-*").Length);
        f.Handler.BeforeResponse = null;
        Assert.AreEqual(2, (await Service(f, client).PrepareAsync([])).Count);
    }
    [TestMethod]
    public async Task SecondVersionFailureKeepsFirstVerifiedDownloadForRetry()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler);
        var valid = f.Handler.Payloads[f.Releases[1].SourceUrl.ToString()];
        f.Handler.Payloads[f.Releases[1].SourceUrl.ToString()] = [];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Service(f, client).PrepareAsync([]));
        f.Handler.Payloads[f.Releases[1].SourceUrl.ToString()] = valid;
        Assert.AreEqual(2, (await Service(f, client).PrepareAsync([])).Count);
        Assert.AreEqual(1, f.Handler.Requests.Count(x => x == f.Releases[0].CliUrl.ToString()));
    }
    [TestMethod]
    public async Task RegistrationRejectsEntireSetIfAnyPreparedFileChanged()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler);
        var prepared = await Service(f, client).PrepareAsync(f.Candidates);
        await File.WriteAllTextAsync(Path.Combine(prepared[1].ConnectorPath, "fixture.cs"), "changed");
        using var catalog = new CatalogService(Path.Combine(f.Root, "data")); await catalog.LoadAsync();
        Assert.IsFalse((await catalog.UsePreparedReleasesAsync(prepared)).Success);
        Assert.AreEqual(0, catalog.Document.Artifacts.Length); Assert.AreEqual(0, catalog.Document.Releases.Length);
    }
    [TestMethod]
    public async Task ArchiveTraversalIsRejectedBeforeWritingOutsideThePackage()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("repo/unity-bridge-connector/package.json");
            using var writer = new StreamWriter(zip.CreateEntry("repo/unity-bridge-connector/../../escaped.txt").Open()); writer.Write("bad");
        }
        f.Handler.Payloads[f.Releases[0].SourceUrl.ToString()] = memory.ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Service(f, client).PrepareAsync([]));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(f.Root, "data"), "escaped.txt", SearchOption.AllDirectories).Length);
    }
    [TestMethod]
    public async Task MissingInternetDoesNotCreateAReadyVersionAndAllowsRetry()
    {
        var f = await CreateFixture(); using var client = new HttpClient(f.Handler); f.Handler.Status = HttpStatusCode.Forbidden;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Service(f, client).PrepareAsync([]));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(f.Root, "data", "releases", "0.2.0")).Length);
        f.Handler.Status = HttpStatusCode.OK;
        Assert.AreEqual(2, (await Service(f, client).PrepareAsync([])).Count);
    }
}
