using System.Text.Json;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;

// Explicit local diagnostic. Source is read-only; all mutations stay in newly created test copies.
internal static class DiagnosticIntegration
{
    public static async Task<int> RunAsync(string configuration)
    {
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(configuration));var config=document.RootElement;
        string source=config.GetProperty("project").GetString()!,data=config.GetProperty("dataRoot").GetString()!,editor=config.GetProperty("editor").GetString()!;
        string baseline=await ProjectFiles.FingerprintAsync(source);var inspected=await new LocalInspector().InspectProjectAsync(source);
        var releases=new List<BridgeReleaseRef>();
        foreach(var r in config.GetProperty("releases").EnumerateArray())
        {
            string cli=r.GetProperty("cli").GetString()!,connector=r.GetProperty("connector").GetString()!;
            var artifact=await new LocalInspector().InspectArtifactAsync(connector,ArtifactKind.ConnectorFolder);
            releases.Add(new(ReleaseId.New(),r.GetProperty("label").GetString()!,cli,ProjectFiles.HashFile(cli),null,connector,artifact.Sha256,artifact.DeclaredVersion){ComparisonAxis=ComparisonAxis.CliAndConnector,ConnectorHashScheme="connector-tree-v1"});
        }
        string root=Path.Combine(data,"integration",Guid.NewGuid().ToString("N")),project=Path.Combine(root,"project");
        Directory.CreateDirectory(root);await ProjectFiles.CloneAsync(source,project,baseline,CancellationToken.None);
        var processes=new WorkerRunner(Environment.ProcessPath!);var runtime=new DeskRuntime(data,processes,new DiagnosticAi(processes));
        runtime.Changed+=notice=>Console.WriteLine(JsonSerializer.Serialize(notice));
        string auth=Path.Combine(root,"synthetic-auth");Directory.CreateDirectory(auth);await File.WriteAllTextAsync(Path.Combine(auth,"auth.json"),"{\"fixture\":true}");
        var ai=new AiOptions(Environment.ProcessPath!,"SYNTHETIC-FIXTURE","medium",auth,600,100,null,true);
        var bench=new BenchOptions(["F01","F04"],["A01"],Repeats:1,InnerCalls:2,TimeoutSeconds:600,PrepareTimeoutSeconds:600);
        var runs=new List<object>();bool all=true;
        async Task Run(ToolKind tool,string target,BenchmarkModes modes,BridgeReleaseRef release,bool install=false)
        {
            var draft=new RunDraft{Tool=tool,Project=new(ProjectId.New(),"P12 시험 복제본",target,inspected.EditorVersion),Modes=modes};draft.Releases.Add(release);
            if(tool==ToolKind.AiWork || modes.HasFlag(BenchmarkModes.AiCreation))draft.AiProfile=new(AiProfileId.New(),"SyntheticFixture",ai.Model,ai.Reasoning,null);
            var plan=draft.Freeze();await runtime.StartAsync(plan,new(editor,bench,tool==ToolKind.Installation?null:ai,AiTasks.Instructions("A01")[0],install));
            string directory=Path.Combine(data,"runs",plan.RunId.Value.ToString("N"));
            var status=await new AtomicJsonStore<RunStatus>(Path.Combine(directory,"status.json"),_=>{}).LoadAsync();
            var trials=HistoryStore.ReadTrials(directory);all &= status.Value?.Status=="완료";
            await HistoryStore.ExportAsync(directory,Path.Combine(root,"exports",plan.RunId.Value.ToString("N")));
            runs.Add(new{tool,plan.RunId,directory,status=status.Value,trials});
        }
        await Run(ToolKind.Installation,project,BenchmarkModes.None,releases[0],true);
        if(all)await Run(ToolKind.AiWork,project,BenchmarkModes.None,releases[0]);
        // A separate pristine baseline proves combined mode doesn't reuse the ordinary AI workspace.
        await Run(ToolKind.Benchmark,source,BenchmarkModes.FixedCommands|BenchmarkModes.AiCreation,releases[0]);
        bool unchanged=baseline==await ProjectFiles.FingerprintAsync(source);
        await DeskRuntime.WriteDocument(Path.Combine(root,"integration.json"),new{baseline,originalUnchanged=unchanged,runs,allPassed=all&&unchanged,actualGenerativeAi=false},CancellationToken.None);
        Console.WriteLine("P12 evidence: "+Path.Combine(root,"integration.json"));return all&&unchanged?0:1;
    }
}
