using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityBridgeDesk.Infrastructure.Execution;

namespace UnityBridgeDesk.Infrastructure.SpeedBench;

// A blank project does not include the Test Framework that the Go Connector's
// TestRunner assembly references. Provision it without changing upstream source.
public static class GoUnityDependencies
{
    public const string TestFramework = "com.unity.test-framework";
    private static bool PackageName(string name) => Regex.IsMatch(name, @"^com\.unity\.[a-z0-9.-]+$");
    private static bool VersionName(string version) => Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$");
    public static async Task<OfficialUnityPackage[]> Prepare(string connector, string destination, string? editor,
        HttpClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal) { [TestFramework] = "1.1.33" };
        using (var package = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(connector, "package.json"), ct)))
            if (package.RootElement.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject()) versions[dependency.Name] = dependency.Value.GetString()!;
        var builtIns = new Dictionary<string, (string Version, string Folder)>(StringComparer.Ordinal);
        string? builtInRoot = editor is null ? null : Path.Combine(Path.GetDirectoryName(editor)!, "Data", "Resources", "PackageManager", "BuiltInPackages");
        if (builtInRoot is not null && Directory.Exists(builtInRoot))
            foreach (string folder in Directory.EnumerateDirectories(builtInRoot))
            {
                string file = Path.Combine(folder, "package.json"); if (!File.Exists(file)) continue; SpeedFiles.Regular(file);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
                string name = json.RootElement.GetProperty("name").GetString()!, version = json.RootElement.GetProperty("version").GetString()!;
                if (PackageName(name) && VersionName(version)) builtIns[name] = (version, folder);
            }
        string Select(string name, string minimum)
        {
            if (!PackageName(name) || !VersionName(minimum)) throw new InvalidDataException("Go 의존 패키지 이름·버전이 올바르지 않습니다: " + name);
            if (!builtIns.TryGetValue(name, out var bundled)) return minimum;
            if (Version.Parse(bundled.Version.Split('-')[0]) < Version.Parse(minimum.Split('-')[0]))
                throw new InvalidDataException("선택한 Editor의 내장 패키지가 요구 버전보다 오래되었습니다: " + name);
            return bundled.Version;
        }
        foreach (string name in versions.Keys.ToArray()) versions[name] = Select(name, versions[name]);
        var pending = new Queue<string>(versions.Keys); var packages = new List<OfficialUnityPackage>();
        while (pending.TryDequeue(out string? name))
        {
            if (versions.Count > 64) throw new InvalidDataException("Go 의존 패키지 수 제한을 넘었습니다.");
            if (name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue;
            string version = versions[name], folder = Path.Combine(destination, name), archiveHash = "", sha1 = "", source = "registry";
            progress?.Report($"Go 실행 환경 · {name} {version} 준비");
            if (builtIns.TryGetValue(name, out var bundled))
            {
                source = "editor-bundled";
                await ProjectFiles.CopyFolderAsync(bundled.Folder, folder, ct);
            }
            else
            {
                using var response = await client.GetAsync("https://packages.unity.com/" + name, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode(); await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var memory = new MemoryStream(); await SpeedFiles.CopyBounded(input, memory, 16 * 1024 * 1024, ct);
                using var catalog = JsonDocument.Parse(memory.ToArray());
                if (!catalog.RootElement.GetProperty("versions").TryGetProperty(version, out var package)) throw new IOException("Go 의존 패키지 버전을 찾지 못했습니다: " + name + " " + version);
                var dist = package.GetProperty("dist"); string url = dist.GetProperty("tarball").GetString()!;
                sha1 = dist.GetProperty("shasum").GetString()!.ToLowerInvariant();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "https" || address.Host != "download.packages.unity.com" || !Regex.IsMatch(sha1, "^[a-f0-9]{40}$"))
                    throw new InvalidDataException("Go 의존 패키지의 Unity 레지스트리 주소·해시가 다릅니다.");
                Directory.CreateDirectory(destination); string archive = Path.Combine(destination, name + ".tgz"); SpeedFiles.Regular(archive);
                using var download = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); download.EnsureSuccessStatusCode();
                await using (var body = await download.Content.ReadAsStreamAsync(ct))
                await using (var file = new FileStream(archive, FileMode.CreateNew)) await SpeedFiles.CopyBounded(body, file, 256L * 1024 * 1024, ct);
                await using (var file = File.OpenRead(archive))
                    if (Convert.ToHexStringLower(await SHA1.HashDataAsync(file, ct)) != sha1) throw new InvalidDataException("Go 의존 패키지의 게시 해시가 다릅니다: " + name);
                archiveHash = await SpeedFiles.Hash(archive, ct);
                await OfficialUnityRepository.Extract(archive, folder, ct); File.Delete(archive);
            }
            using var installed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "package.json"), ct));
            if (installed.RootElement.GetProperty("name").GetString() != name || installed.RootElement.GetProperty("version").GetString() != version)
                throw new InvalidDataException("Go 의존 패키지 이름·버전 불일치: " + name);
            packages.Add(new(name, version, folder, await CliDistribution.TreeHash(folder, ct), archiveHash, sha1, source));
            if (installed.RootElement.TryGetProperty("dependencies", out var required))
                foreach (var dependency in required.EnumerateObject())
                {
                    string dependencyVersion = Select(dependency.Name, dependency.Value.GetString()!);
                    if (dependency.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue;
                    if (versions.TryGetValue(dependency.Name, out string? selected))
                    { if (selected != dependencyVersion) throw new InvalidDataException("Go 의존 패키지 버전 충돌: " + dependency.Name); }
                    else { versions[dependency.Name] = dependencyVersion; pending.Enqueue(dependency.Name); }
                }
        }
        return packages.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }
    public static async Task<bool> Valid(OfficialUnityPackage[]? packages, CancellationToken ct)
    {
        if (packages is not { Length: > 0 and <= 64 } || !packages.Any(p => p.Name == TestFramework) || packages.Select(p => p.Name).Distinct().Count() != packages.Length) return false;
        foreach (var package in packages)
        {
            if (!PackageName(package.Name) || !VersionName(package.Version) || await CliDistribution.TreeHash(package.Folder, ct) != package.TreeSha256) return false;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package.Folder, "package.json"), ct));
            if (json.RootElement.GetProperty("name").GetString() != package.Name || json.RootElement.GetProperty("version").GetString() != package.Version) return false;
            if (json.RootElement.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (dependency.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue;
                    string? minimum = dependency.Value.GetString();
                    var selected = packages.SingleOrDefault(p => p.Name == dependency.Name);
                    if (selected is null || minimum is null || !VersionName(minimum) || !VersionName(selected.Version) ||
                        Version.Parse(selected.Version.Split('-')[0]) < Version.Parse(minimum.Split('-')[0])) return false;
                }
        }
        return true;
    }
}
