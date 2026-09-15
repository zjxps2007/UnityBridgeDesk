using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class ReleaseBundleTests
{
    private const string Tag = "v0.2.2-rc.2", Version = "0.2.2-rc.2", Runtime = "_unity_bridge_runtime_0123456789abcdef0123456789abcdef";
    private const string Api = "https://api.github.com/repos/zjxps2007/UnityBridge/";
    private const string Asset = "https://github.com/zjxps2007/UnityBridge/releases/download/" + Tag + "/unity-bridge-windows-amd64.zip";
    private sealed class HttpFixture : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public int Requests; public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(!Offline && Files.ContainsKey(request.RequestUri!.ToString()) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                { Content = new ByteArrayContent(Files.GetValueOrDefault(request.RequestUri!.ToString()) ?? []) });
        }
    }
    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, contents) in files) { using var entry = zip.CreateEntry(name).Open(); entry.Write(contents); }
        return output.ToArray();
    }
    private static Dictionary<string, byte[]> Payload() => new()
    {
        ["unity-bridge/unity-bridge.exe"] = File.ReadAllBytes(Environment.ProcessPath!),
        ["unity-bridge/" + Runtime + "/python312.dll"] = Encoding.UTF8.GetBytes("synthetic runtime - never executed"),
        ["unity-bridge/" + Runtime + "/lib/module.dat"] = [1, 2, 3]
    };
    private static (string Root, HttpFixture Handler, HttpClient Client, ReleaseRepository Repository) Fixture(Dictionary<string, byte[]>? files = null, string? digest = null, string? packageVersion = null)
    {
        string root = SampleData.TestDirectory(), commit = new('b', 40); var handler = new HttpFixture(); var client = new HttpClient(handler);
        byte[] archive = Zip(files ?? Payload());
        string hash = digest ?? "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive));
        handler.Files[Api + "releases?per_page=100&page=1"] = JsonSerializer.SerializeToUtf8Bytes(new[] {
            new { id = 1, tag_name = Tag, name = "RC2", prerelease = true, draft = false, html_url = "https://github.com/zjxps2007/UnityBridge/releases/tag/" + Tag,
                assets = new[] { new { name = "unity-bridge-windows-amd64.exe", browser_download_url = Asset.Replace(".zip", ".exe"), digest = "sha256:" + new string('0', 64) },
                    new { name = "unity-bridge-windows-amd64.zip", browser_download_url = Asset, digest = hash } } } });
        handler.Files[Api + "commits/" + Tag] = JsonSerializer.SerializeToUtf8Bytes(new { sha = commit });
        handler.Files[Asset] = archive;
        handler.Files["https://codeload.github.com/zjxps2007/UnityBridge/zip/" + commit] = Zip(new() {
            ["repo/unity-bridge-connector/package.json"] = JsonSerializer.SerializeToUtf8Bytes(new { name = LocalInspector.ConnectorPackageName, version = packageVersion ?? Version }) });
        return (root, handler, client, new(root, client));
    }
    [TestMethod]
    public async Task RcZipPreferredAndEntireRuntimeTransferredAndReusedOffline()
    {
        var f = Fixture(); using var http = f.Client;
        var choice = (await f.Repository.List(CancellationToken.None)).Single();
        Assert.AreEqual(Asset, choice.CliUrl); Assert.IsTrue(choice.Prerelease); Assert.Contains("런타임", choice.Display);
        var release = await f.Repository.Prepare(choice, null, CancellationToken.None);
        Assert.IsNotNull(release.CliDirectory); Assert.AreNotEqual(release.CliSha256, release.AssetSha256);
        Assert.AreEqual(choice.PublisherDigest, "sha256:" + release.AssetSha256);
        Assert.IsTrue(await ReleaseRepository.Valid(release, CancellationToken.None));
        int calls = f.Handler.Requests; f.Handler.Offline = true;
        Assert.AreEqual(release.CliPath, (await f.Repository.Prepare(choice, null, CancellationToken.None)).CliPath);
        Assert.AreEqual(calls, f.Handler.Requests);
        string worker = Path.Combine(f.Root, "worker"); Directory.CreateDirectory(worker); await File.WriteAllTextAsync(Path.Combine(worker, "worker.txt"), "fixture");
        string request = Path.Combine(f.Root, "request.json"); await File.WriteAllTextAsync(request, "{}");
        string package = Path.Combine(f.Root, "input.zip"), guest = Path.Combine(f.Root, "guest");
        await SpeedCoordinator.Package(package, worker, release, request, CancellationToken.None);
        await SpeedFiles.Extract(package, guest);
        string received = Path.Combine(guest, "release", "cli");
        Assert.IsTrue(File.Exists(Path.Combine(received, Runtime, "lib", "module.dat")));
        Assert.AreEqual(release.CliTreeSha256, await CliDistribution.TreeHash(received, CancellationToken.None));
        Assert.AreEqual("0.2.2-rc.2", CliDistribution.ReportedConnectorVersion(release));
    }
    [TestMethod]
    public async Task AlteredOrMissingRuntimeRejectedAndRedownloadedWithoutOverlay()
    {
        var f = Fixture(); using var http = f.Client;
        var choice = (await f.Repository.List(CancellationToken.None)).Single();
        var original = await f.Repository.Prepare(choice, null, CancellationToken.None);
        string dll = Path.Combine(original.CliDirectory!, Runtime, "python312.dll");
        await File.WriteAllTextAsync(dll, "changed runtime");
        Assert.IsFalse(await ReleaseRepository.Valid(original, CancellationToken.None));
        f.Handler.Offline = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => f.Repository.Prepare(choice, null, CancellationToken.None));
        f.Handler.Offline = false;
        var replacement = await f.Repository.Prepare(choice, null, CancellationToken.None);
        Assert.AreNotEqual(original.CliDirectory, replacement.CliDirectory);
        Assert.AreEqual("changed runtime", await File.ReadAllTextAsync(dll));
        Assert.IsTrue(await ReleaseRepository.Valid(replacement, CancellationToken.None));
        File.Delete(Path.Combine(replacement.CliDirectory!, Runtime, "python312.dll"));
        Assert.IsFalse(await ReleaseRepository.Valid(replacement, CancellationToken.None));
    }
    [TestMethod]
    public async Task BadArchiveDigestRejectedBeforeExtractionOrCacheRegistration()
    {
        var f = Fixture(digest: "sha256:" + new string('0', 64)); using var http = f.Client;
        var choice = (await f.Repository.List(CancellationToken.None)).Single();
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Repository.Prepare(choice, null, CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "release.json", SearchOption.AllDirectories).Length);
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "python312.dll", SearchOption.AllDirectories).Length);
    }
    [TestMethod]
    public async Task MissingMixedAndEscapingBundleLayoutsNeverBecomeUsableReleases()
    {
        foreach (int scenario in new[] { 0, 1, 2, 3 })
        {
            var contents = Payload();
            if (scenario == 0) contents.Remove("unity-bridge/" + Runtime + "/python312.dll");
            if (scenario == 1) contents["unity-bridge/_unity_bridge_runtime_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/python312.dll"] = [9];
            if (scenario == 2) contents["unity-bridge/../../escape.txt"] = [9];
            if (scenario == 3) contents["outside.txt"] = [9];
            var f = Fixture(contents); using var http = f.Client;
            var choice = (await f.Repository.List(CancellationToken.None)).Single();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Repository.Prepare(choice, null, CancellationToken.None));
            Assert.AreEqual(0, Directory.GetFiles(f.Root, "release.json", SearchOption.AllDirectories).Length);
        }
    }
    [TestMethod]
    public async Task Rc1ReportingExceptionRequiresExactOfficialCommitAndDoesNotRelabelThePackage()
    {
        var f = Fixture(); using var http = f.Client;
        var release = await f.Repository.Prepare((await f.Repository.List(CancellationToken.None)).Single(), null, CancellationToken.None);
        var rc1 = release with { Tag = "v0.2.2-rc.1", Version = "0.2.2-rc.1", Commit = "74639d7b3f3adf550d58cc853b715f878153839f" };
        Assert.AreEqual("0.2.1", CliDistribution.ReportedConnectorVersion(rc1));
        Assert.AreEqual("0.2.2-rc.1", rc1.Version); Assert.IsNotNull(CliDistribution.CompatibilityNote(rc1));
        Assert.AreEqual(rc1.Version, CliDistribution.ReportedConnectorVersion(rc1 with { Commit = new string('a', 40) }));
        Assert.IsNull(CliDistribution.CompatibilityNote(release));
        Assert.IsFalse(CliDistribution.OfficialAsset(Tag, Asset.Replace(Tag, "v0.2.2-rc.1")));
    }
}
