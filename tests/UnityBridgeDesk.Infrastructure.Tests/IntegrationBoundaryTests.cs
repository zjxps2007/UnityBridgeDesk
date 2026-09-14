using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class IntegrationBoundaryTests
{
    private static string Worker=>Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../src/UnityBridgeDesk.Worker/bin/Release/net10.0-windows/UnityBridgeDesk.Worker.exe"));
    [TestMethod] public async Task CancellingOneJobKeepsAnIndependentRunningJobAlive()
    {
        var root=SampleData.TestDirectory();var runner=new WorkerRunner(Worker);
        using var firstStop=new CancellationTokenSource();using var secondStop=new CancellationTokenSource();
        var firstPid=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);var secondPid=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProcessResult> Start(CancellationToken ct,TaskCompletionSource<int> pid)=>runner.RunAsync(new(Guid.NewGuid(),Worker,["--fixture","hang"],root),TimeSpan.FromSeconds(30),f=>{if(f.Kind=="started")pid.TrySetResult(JsonDocument.Parse(f.Text).RootElement.GetProperty("pid").GetInt32());},ct);
        var first=Start(firstStop.Token,firstPid);var second=Start(secondStop.Token,secondPid);
        try
        {
            await Task.WhenAll(firstPid.Task,secondPid.Task).WaitAsync(TimeSpan.FromSeconds(10));
            firstStop.Cancel();Assert.AreEqual(ProcessOutcome.Cancelled,(await first).Outcome);
            using var other=Process.GetProcessById(await secondPid.Task);Assert.IsFalse(other.HasExited);
            Assert.IsFalse(second.IsCompleted);
        }
        finally{firstStop.Cancel();secondStop.Cancel();await Task.WhenAll(first,second);}
    }
    [TestMethod][DataRow(false)][DataRow(true)] public async Task OrphanRecoveryRequiresExclusiveRuntimeAndExportsIncompleteValuesAsUnknown(bool legacy)
    {
        string root=SampleData.TestDirectory();var plan=SampleData.Draft().Freeze();var route=SampleData.Route(plan);var state=new ExecutionState(route);
        string directory=Path.Combine(root,"runs",plan.RunId.Value.ToString("N"));string recordRoot=legacy?root:Path.Combine(root,"runs");var records=new RunRecordStore(recordRoot);
        await records.CreatePlanAsync(plan);await using(var writer=await records.CreateExecutionAsync(plan,new(route,plan.Releases[0].Id,root,root),state.Events[0])){}
        var status=new AtomicJsonStore<RunStatus>(Path.Combine(directory,"status.json"),_=>{});await status.SaveAsync(new(plan.RunId,plan.Tool,plan.Project.RootPath,"실행 중",DateTimeOffset.UtcNow,null));
        Assert.IsEmpty(HistoryStore.Summarize(HistoryStore.ReadTrials(directory)));
        using(var lease=new FileStream(Path.Combine(root,"runtime.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            await Assert.ThrowsAsync<IOException>(()=>new HistoryStore(root).RecoverInterruptedAsync());
        string recordPath=Path.Combine(recordRoot,plan.RunId.Value.ToString("N"),"executions",route.ExecutionId.Value.ToString("N"),"record.json");string original=File.ReadAllText(recordPath);
        Assert.AreEqual(1,await new HistoryStore(root).RecoverInterruptedAsync());
        var trial=HistoryStore.ReadTrials(directory).Single();Assert.AreEqual("Interrupted",trial.Outcome);Assert.IsNull(trial.WorkMs);Assert.IsNull(trial.PreparationMs);
        File.WriteAllText(Path.Combine(directory,"auth.json"),"SECRET-FIXTURE");string export=Path.Combine(root,"export");await HistoryStore.ExportAsync(directory,export);
        Assert.IsFalse(File.Exists(Path.Combine(export,"auth.json")));Assert.Contains("Interrupted",File.ReadAllText(Path.Combine(export,"trials.csv")));
        Assert.AreEqual(original,File.ReadAllText(recordPath));Assert.IsTrue(File.Exists(Path.Combine(export,"plan.json")));Assert.AreEqual(original,File.ReadAllText(Path.Combine(export,"executions",route.ExecutionId.Value.ToString("N"),"record.json")));
    }
    [TestMethod] public void ExternalLocalPackagesCannotSilentlyEscapeBenchmarkClone()
    {
        string root=SampleData.TestDirectory();Directory.CreateDirectory(Path.Combine(root,"Packages"));string manifest=Path.Combine(root,"Packages","manifest.json");
        File.WriteAllText(manifest,"{\"dependencies\":{\"com.example.external\":\"file:../../shared\"}}");
        Assert.ThrowsExactly<InvalidDataException>(()=>ProjectFiles.RequireSelfContainedPackages(root));
        File.WriteAllText(manifest,"{\"dependencies\":{\"com.example.registry\":\"1.0.0\"}}");ProjectFiles.RequireSelfContainedPackages(root);
    }
}
