using System.Text.Json;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    private static string Project(string parent, string name)
    {
        string path=Path.Combine(parent,name);
        foreach(string folder in new[]{"Assets","Packages","ProjectSettings"})Directory.CreateDirectory(Path.Combine(path,folder));
        File.WriteAllText(Path.Combine(path,"ProjectSettings","ProjectVersion.txt"),"m_EditorVersion: 6000.3.23f1\n");
        return path;
    }
    private static string Connector(string parent)
    {
        string path=Path.Combine(parent,"unity-bridge-connector");Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"package.json"),JsonSerializer.Serialize(new{name=LocalInspector.ConnectorPackageName,version="0.2.1"}));
        File.WriteAllText(Path.Combine(path,"sample.txt"),"fixture");return path;
    }
    private static LocalDiscovery Scanner(string root,string path="")=>new(new(
        Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),path));

    [TestMethod]
    public async Task FindsHubKeyedAndArrayProjectsAndExplicitReleaseFolderWithoutLaunchingAnything()
    {
        string root=SampleData.TestDirectory(), project=Project(root,"다른 위치 프로젝트"), hub=Path.Combine(root,"roaming","UnityHub");Directory.CreateDirectory(hub);
        File.WriteAllText(Path.Combine(hub,"projects-v1.json"),JsonSerializer.Serialize(new Dictionary<string,object>{{project,new{title="sample"}}}));
        string bundle=Path.Combine(root,"임의 위치","0.2.1");Directory.CreateDirectory(bundle);
        string cli=Path.Combine(bundle,"unity-bridge-windows-amd64.exe");File.WriteAllText(cli,"discovery must not execute this fixture");
        string connector=Connector(bundle);
        var first=await Scanner(root).ScanAsync(CatalogDocument.Empty,[Path.GetDirectoryName(bundle)!]);
        Assert.IsTrue(first.Candidates.Any(x=>x.Kind==DiscoveryKind.Project&&x.Path==project&&x.Source=="Unity Hub"));
        Assert.IsTrue(first.Candidates.Any(x=>x.Kind==DiscoveryKind.BridgeCli&&x.Path==cli));
        Assert.IsTrue(first.Candidates.Any(x=>x.Kind==DiscoveryKind.Connector&&x.Path==connector&&x.Version=="0.2.1"));
        File.WriteAllText(Path.Combine(hub,"projects-v1.json"),JsonSerializer.Serialize(new[]{new{path=project}}));
        var second=await Scanner(root).ScanAsync(CatalogDocument.Empty);
        Assert.AreEqual(project,second.Candidates.Single(x=>x.Kind==DiscoveryKind.Project).Path);
    }
    [TestMethod]
    public async Task DoesNotCrawlUnityLibraryAndSkipsMalformedMetadataButKeepsOtherCandidates()
    {
        string root=SampleData.TestDirectory(), project=Project(root,"project");
        string library=Path.Combine(project,"Library");Directory.CreateDirectory(library);Connector(library);
        string bad=Path.Combine(root,"bad");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,"package.json"),"{broken");
        string path=Path.Combine(root,"path");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"codex.exe"),"fixture");
        var result=await Scanner(root,path).ScanAsync(CatalogDocument.Empty,[project,bad]);
        Assert.IsFalse(result.Candidates.Any(x=>x.Kind==DiscoveryKind.Connector));
        Assert.AreEqual(1,result.UnreadableLocations);
        Assert.IsTrue(result.Candidates.Any(x=>x.Kind==DiscoveryKind.AiExecutable));
    }
    [TestMethod]
    public async Task DeduplicatesSavedPathsAndOnlyChecksPresenceOfAuthenticationFile()
    {
        string root=SampleData.TestDirectory(), home=Path.Combine(root,"home",".codex");Directory.CreateDirectory(home);
        string auth=Path.Combine(home,"auth.json");File.WriteAllText(auth,"not JSON and should never be parsed");
        using var locked=new FileStream(auth,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var result=await Scanner(root).ScanAsync(CatalogDocument.Empty,rememberedPaths:[home,home.ToUpperInvariant()]);
        Assert.AreEqual(1,result.Candidates.Count(x=>x.Kind==DiscoveryKind.AuthenticationHome));
        Assert.AreEqual(0,result.UnreadableLocations);
    }
    [TestMethod]
    public void EditorMatchingRequiresExactProjectVersionAndNeverPicksLatestOrUnknown()
    {
        LocalCandidate[] editors=[new(DiscoveryKind.Editor,"old","old","fixture","6000.3.5f2"),new(DiscoveryKind.Editor,"match","match","fixture","6000.3.23f1"),new(DiscoveryKind.Editor,"latest","latest","fixture","6000.5.4f1")];
        Assert.AreEqual("match",LocalDiscovery.MatchEditor(editors,"6000.3.23f1")?.Path);
        Assert.IsNull(LocalDiscovery.MatchEditor(editors,"6000.3.23f2"));
        Assert.IsNull(LocalDiscovery.MatchEditor(editors,null));
    }
    [TestMethod]
    public async Task RememberedPathsMergeAcrossToolsAndProjectSelectionDoesNotDuplicate()
    {
        string root=SampleData.TestDirectory(), project=Project(root,"project"), data=Path.Combine(root,"data");
        var a=new LocalSetupStore(data);var b=new LocalSetupStore(data);
        Assert.IsTrue(await a.RememberAsync(folder:root,key:"editor:fixture",path:Path.Combine(root,"Unity.exe")));
        Assert.IsTrue(await b.RememberAsync(folder:root.ToUpperInvariant(),key:"codex",path:Path.Combine(root,"codex.exe")));
        var loaded=await a.LoadAsync();Assert.AreEqual(2,loaded.Paths.Count);Assert.AreEqual(1,loaded.Folders.Length);
        using var catalog=new CatalogService(data);await catalog.LoadAsync();
        Assert.IsTrue((await catalog.UseDiscoveredProjectAsync(project)).Success);
        var id=catalog.Document.SelectedProject;
        Assert.IsTrue((await catalog.UseDiscoveredProjectAsync(project)).Success);
        Assert.AreEqual(1,catalog.Document.Projects.Length);Assert.AreEqual(id,catalog.Document.SelectedProject);
        Assert.IsFalse((await catalog.UseDiscoveredProjectAsync(Path.Combine(root,"missing"))).Success);
        Assert.AreEqual(id,catalog.Document.SelectedProject);
    }
    [TestMethod]
    public async Task ReleaseImportIsAtomicReusesHashesAndRelocatesWithoutReplacingIdentity()
    {
        string root=SampleData.TestDirectory(), bundle=Path.Combine(root,"bundle");Directory.CreateDirectory(bundle);
        string cli=Path.Combine(bundle,"unity-bridge.exe");File.Copy(Environment.ProcessPath!,cli);
        string connector=Connector(bundle);
        using var catalog=new CatalogService(Path.Combine(root,"data"));await catalog.LoadAsync();
        Assert.IsFalse((await catalog.UseDiscoveredReleaseAsync(cli,Path.Combine(root,"missing"),"test")).Success);
        Assert.AreEqual(0,catalog.Document.Artifacts.Length);
        Assert.IsTrue((await catalog.UseDiscoveredReleaseAsync(cli,connector,"test")).Success);
        var id=catalog.Document.SelectedRelease;
        Assert.IsTrue((await catalog.UseDiscoveredReleaseAsync(cli,connector,"test")).Success);
        Assert.AreEqual(2,catalog.Document.Artifacts.Length);Assert.AreEqual(1,catalog.Document.Releases.Length);
        string moved=Path.Combine(root,"다른 cli.exe");File.Copy(cli,moved);
        Assert.IsTrue((await catalog.UseDiscoveredReleaseAsync(moved,connector,"test")).Success);
        Assert.AreEqual(id,catalog.Document.SelectedRelease);
        Assert.AreEqual(moved,catalog.Document.Artifacts.Single(x=>x.Kind==ArtifactKind.CliExecutable).Path);
    }
    [TestMethod]
    public async Task FindsDesktopAndNpmNativeProgramsAndCustomHomeWithoutPathOrCredentialReads()
    {
        string root=SampleData.TestDirectory();
        string desktop=Path.Combine(root,"local","OpenAI","Codex","bin","build-id","codex.exe");
        string npm=Path.Combine(root,"roaming","npm","node_modules","@openai","codex-win32-x64","vendor","x86_64-pc-windows-msvc","codex","codex.exe");
        string custom=Path.Combine(root,"custom-home");Directory.CreateDirectory(custom);
        foreach(string path in new[]{desktop,npm}){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"never run this fixture");}
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(desktop)!,"codex-command-runner.exe"),"helper is not the CLI");
        File.WriteAllText(Path.Combine(custom,"auth.json"),"must not be read");
        using var locked=new FileStream(Path.Combine(custom,"auth.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var scanner=new LocalDiscovery(new(Path.Combine(root,"home"),Path.Combine(root,"roaming"),Path.Combine(root,"local"),Path.Combine(root,"programs"),Path.Combine(root,"app"),"",custom));
        var result=await scanner.ScanAsync(CatalogDocument.Empty);
        CollectionAssert.AreEquivalent(new[]{desktop,npm},result.Candidates.Where(x=>x.Kind==DiscoveryKind.AiExecutable).Select(x=>x.Path).ToArray());
        Assert.AreEqual(custom,result.Candidates.Single(x=>x.Kind==DiscoveryKind.AuthenticationHome).Path);
        Assert.AreEqual(0,result.UnreadableLocations);
    }
    [TestMethod]
    public async Task RemembersExplicitlyNamedCodexExecutableAndRejectsMissingOrScriptLocations()
    {
        string root=SampleData.TestDirectory(), renamed=Path.Combine(root,"my-ai.exe"), script=Path.Combine(root,"launcher.cmd");
        File.WriteAllText(renamed,"fixture");File.WriteAllText(script,"fixture");
        var first=await Scanner(root).ScanAsync(CatalogDocument.Empty,configuredPaths:new Dictionary<string,string>{{"codex",renamed}});
        Assert.AreEqual(renamed,first.Candidates.Single(x=>x.Kind==DiscoveryKind.AiExecutable).Path);
        foreach(string invalid in new[]{script,Path.Combine(root,"missing.exe")})
        {
            var result=await Scanner(root).ScanAsync(CatalogDocument.Empty,configuredPaths:new Dictionary<string,string>{{"codex",invalid}});
            Assert.AreEqual(0,result.Candidates.Count(x=>x.Kind==DiscoveryKind.AiExecutable));
        }
    }
    [TestMethod]
    public async Task CancellationStopsDiscovery()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(()=>Scanner(SampleData.TestDirectory()).ScanAsync(CatalogDocument.Empty,cancellationToken:cancellation.Token));
    }
}
