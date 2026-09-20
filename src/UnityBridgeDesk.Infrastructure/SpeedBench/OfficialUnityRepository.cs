using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

// Downloads portable artifacts only. Does not run an installer, change PATH, or edit a user project.
public sealed class OfficialUnityRepository(string storageRoot, HttpClient? http = null)
{
    public const string Cdn = "https://public-cdn.cloud.unity3d.com/hub/prod/cli/";
    private static readonly HttpClient shared = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient client = http ?? shared;
    private static bool VersionName(string value) => Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$");
    private static bool PackageName(string value) => Regex.IsMatch(value, @"^com\.unity\.[a-z0-9.-]+$");
    public static void ValidateVersions(string? cliVersion, string? pipelineVersion)
    {
        if (cliVersion is not null && !VersionName(cliVersion) || pipelineVersion is not null && !VersionName(pipelineVersion))
            throw new ArgumentException("공식 CLI와 Pipeline의 정확한 버전 번호가 필요합니다.");
    }
    public async Task<SpeedRelease> Prepare(string? cliVersion, string? pipelineVersion, IProgress<string>? progress, CancellationToken ct, string? editorPath = null)
    {
        ValidateVersions(cliVersion, pipelineVersion);
        using var manifest = await Json(Cdn + (cliVersion is null ? "latest-beta.json" : cliVersion + "/latest.json"), ct);
        string actualCli = manifest.RootElement.GetProperty("version").GetString()!;
        if (!VersionName(actualCli) || cliVersion is not null && cliVersion != actualCli) throw new InvalidDataException("공식 CLI 버전 불일치.");
        using var catalog = await Json("https://packages.unity.com/com.unity.pipeline", ct);
        pipelineVersion ??= catalog.RootElement.GetProperty("dist-tags").GetProperty("latest").GetString()!;
        if (!VersionName(pipelineVersion)) throw new InvalidDataException("Pipeline 버전 형식이 다릅니다.");
        if (!catalog.RootElement.GetProperty("versions").TryGetProperty(pipelineVersion, out _))
            throw new ArgumentException("게시된 Pipeline 버전을 찾지 못했습니다: " + pipelineVersion + " · 버전을 비우면 자동으로 확인합니다.");
        string? editorVersion = editorPath is null ? null : Catalog.LocalDiscovery.EditorVersion(editorPath);
        string shelf = Path.Combine(Path.GetFullPath(storageRoot), actualCli + "_" + pipelineVersion + (editorVersion is null ? "" : "_" + editorVersion));
        SpeedFiles.Regular(shelf);
        if (Directory.Exists(shelf)) foreach (string saved in Directory.EnumerateFiles(shelf, "release.json", SearchOption.AllDirectories))
        {
            try
            {
                var cached = await SpeedFiles.Read<SpeedRelease>(saved, ct);
                if (cached.OfficialUnity?.CliVersion == actualCli && cached.OfficialUnity.PipelineVersion == pipelineVersion && cached.OfficialUnity.EditorVersion == editorVersion && await Valid(cached, ct)) return cached;
            }
            catch (Exception e) when (e is IOException or JsonException) { /* Keep incomplete cached artifacts; prepare a new immutable set. */ }
        }
        string root = Path.Combine(shelf, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var binary = manifest.RootElement.GetProperty("binaries").GetProperty("win32-x64");
        string filename = binary.GetProperty("filename").GetString()!, digest = binary.GetProperty("sha256").GetString()!.ToLowerInvariant();
        if (filename != "unity-windows-x64.exe" || !Regex.IsMatch(digest, "^[0-9a-f]{64}$")) throw new InvalidDataException("공식 Windows CLI 배포 형식이 다릅니다.");
        string cli = Path.Combine(root, "unity.exe"), url = Cdn + actualCli + "/" + filename;
        progress?.Report($"공식 Unity CLI {actualCli} · Pipeline {pipelineVersion} 준비");
        await Download(url, cli, 256L * 1024 * 1024, ct);
        if (await SpeedFiles.Hash(cli, ct) != digest) throw new InvalidDataException("공식 CLI 게시 해시가 일치하지 않습니다.");
        var packages = new List<OfficialUnityPackage>();
        var versions = new Dictionary<string, string> { ["com.unity.pipeline"] = pipelineVersion };
        var builtIns = new Dictionary<string, (string Version, string Folder)>();
        string? builtInRoot = editorPath is null ? null : Path.Combine(Path.GetDirectoryName(editorPath)!, "Data", "Resources", "PackageManager", "BuiltInPackages");
        if (builtInRoot is not null && Directory.Exists(builtInRoot)) foreach (string builtIn in Directory.EnumerateDirectories(builtInRoot))
        {
            string info = Path.Combine(builtIn, "package.json"); if (!File.Exists(info)) continue;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(info, ct));
            string name = json.RootElement.GetProperty("name").GetString()!, version = json.RootElement.GetProperty("version").GetString()!;
            if (PackageName(name) && VersionName(version) && name != "com.unity.pipeline" && !name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) builtIns[name] = (version, builtIn);
        }
        var pending = new Queue<string>(); pending.Enqueue("com.unity.pipeline");
        while (pending.TryDequeue(out string? name))
        {
            if (versions.Count > 64) throw new InvalidDataException("Pipeline 의존 패키지 수 제한을 넘었습니다.");
            string folder = Path.Combine(root, "packages", name), archiveHash = "", sha1 = "", source = "registry";
            if (builtIns.TryGetValue(name, out var builtInPackage))
            {
                if (Version.Parse(builtInPackage.Version.Split('-')[0]) < Version.Parse(versions[name].Split('-')[0]))
                    throw new InvalidDataException("Editor 내장 패키지가 Pipeline 최소 요구보다 오래되었습니다: " + name);
                versions[name] = builtInPackage.Version; source = "editor-bundled";
                await ProjectFiles.CopyFolderAsync(builtInPackage.Folder, folder, ct);
            }
            else
            {
            using var metadata = name == "com.unity.pipeline" ? JsonDocument.Parse(catalog.RootElement.GetRawText()) : await Json("https://packages.unity.com/" + name, ct);
            var package = metadata.RootElement.GetProperty("versions").GetProperty(versions[name]);
            var dist = package.GetProperty("dist"); string archiveUrl = dist.GetProperty("tarball").GetString()!;
            if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var address) || address.Scheme != "https" || address.Host != "download.packages.unity.com")
                throw new InvalidDataException("공식 Unity 패키지 다운로드 주소가 아닙니다.");
            string archive = Path.Combine(root, name + ".tgz");
            await Download(archiveUrl, archive, 256L * 1024 * 1024, ct);
            sha1 = dist.GetProperty("shasum").GetString()!.ToLowerInvariant();
            await using (var input = File.OpenRead(archive))
                if (Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ct)) != sha1) throw new InvalidDataException("Unity 패키지 게시 해시 불일치: " + name);
            await Extract(archive, folder, ct);
            archiveHash = await SpeedFiles.Hash(archive, ct);
            }
            using var installed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "package.json"), ct));
            if (installed.RootElement.GetProperty("name").GetString() != name || installed.RootElement.GetProperty("version").GetString() != versions[name])
                throw new InvalidDataException("Unity 패키지 이름·버전 불일치: " + name);
            packages.Add(new(name, versions[name], folder, await CliDistribution.TreeHash(folder, ct), archiveHash, sha1, source));
            if (installed.RootElement.TryGetProperty("dependencies", out var dependencies)) foreach (var dependency in dependencies.EnumerateObject())
            {
                string version = dependency.Value.GetString()!;
                if (!PackageName(dependency.Name) || !VersionName(version)) throw new InvalidDataException("지원하지 않는 패키지 의존 형식: " + dependency.Name);
                if (dependency.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue; // Pinned by the selected Editor.
                if (builtIns.TryGetValue(dependency.Name, out var bundled))
                {
                    if (Version.Parse(bundled.Version.Split('-')[0]) < Version.Parse(version.Split('-')[0]))
                        throw new InvalidDataException("Editor 내장 패키지가 Pipeline 최소 요구보다 오래되었습니다: " + dependency.Name);
                    version = bundled.Version;
                }
                if (versions.TryGetValue(dependency.Name, out string? selected))
                { if (selected != version) throw new InvalidDataException("명시적인 패키지 버전 충돌: " + dependency.Name); }
                else { versions.Add(dependency.Name, version); pending.Enqueue(dependency.Name); }
            }
        }
        var pipeline = packages.Single(p => p.Name == "com.unity.pipeline");
        var result = new SpeedRelease($"Unity CLI {actualCli} + Pipeline {pipelineVersion}", pipelineVersion, actualCli, cli, pipeline.Folder,
            digest, pipeline.TreeSha256, url, "sha256:" + digest, AssetSha256: digest,
            OfficialUnity: new(actualCli, pipelineVersion, packages.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(), editorVersion));
        await SpeedFiles.Write(Path.Combine(root, "release.json"), result, ct);
        return result;
    }
    public static async Task<bool> Valid(SpeedRelease release, CancellationToken ct)
    {
        if (release.OfficialUnity is not { } tool || tool.Packages is null || tool.Packages.Length is < 1 or > 64 || !VersionName(tool.CliVersion) ||
            !VersionName(tool.PipelineVersion) || tool.Packages.Select(p => p.Name).Distinct().Count() != tool.Packages.Length) return false;
        try
        {
            if (await SpeedFiles.Hash(release.CliPath, ct) != release.CliSha256 || release.PublisherDigest != "sha256:" + release.CliSha256) return false;
            foreach (var p in tool.Packages)
            {
                if (!PackageName(p.Name) || !VersionName(p.Version) || await CliDistribution.TreeHash(p.Folder, ct) != p.TreeSha256) return false;
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(p.Folder, "package.json"), ct));
                if (json.RootElement.GetProperty("name").GetString() != p.Name || json.RootElement.GetProperty("version").GetString() != p.Version) return false;
            }
            var pipeline = tool.Packages.SingleOrDefault(p => p.Name == "com.unity.pipeline");
            return pipeline is not null && pipeline.Version == release.Version && release.Version == tool.PipelineVersion &&
                pipeline.TreeSha256 == release.ConnectorSha256 && Bridge.InstanceDiscovery.SamePath(pipeline.Folder, release.ConnectorPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    public static async Task Extract(string archive, string folder, CancellationToken ct)
    {
        string root = Path.GetFullPath(folder); SpeedFiles.Regular(root); Directory.CreateDirectory(root);
        await using var file = File.OpenRead(archive); await using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(zip); long bytes = 0; int count = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            string relative = entry.Name.Replace('\\', '/');
            if (relative is "package" or "package/") continue;
            if (!relative.StartsWith("package/", StringComparison.Ordinal) || relative.Split('/').Any(p => p is ".." or ".") || relative.Contains(':'))
                throw new InvalidDataException("패키지 압축 경로가 올바르지 않습니다.");
            string target = Path.GetFullPath(Path.Combine(root, relative[8..]));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                ++count > 20000 || (bytes += entry.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("패키지 압축 범위를 넘었습니다.");
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("패키지 링크·특수 파일은 허용하지 않습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); SpeedFiles.Regular(target);
            await using var output = new FileStream(target, FileMode.CreateNew);
            if (entry.DataStream is not null) await SpeedFiles.CopyBounded(entry.DataStream, output, entry.Length, ct);
        }
    }
    private async Task<JsonDocument> Json(string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct); using var data = new MemoryStream();
        await SpeedFiles.CopyBounded(source, data, 16 * 1024 * 1024, ct); return JsonDocument.Parse(data.ToArray());
    }
    private async Task Download(string url, string path, long limit, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct); await using var output = new FileStream(path, FileMode.CreateNew);
        await SpeedFiles.CopyBounded(source, output, limit, ct);
    }
}
