using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class BenchmarkTests
{
    private static RunPlan Plan(BenchmarkModes modes,ComparisonAxis axis=ComparisonAxis.CliAndConnector)
    {
        var draft=new RunDraft{Tool=ToolKind.Benchmark,Modes=modes,Project=new(ProjectId.New(),"fixture",SampleData.TestDirectory(),"fixture")};
        foreach(string label in new[]{"A","B"})draft.Releases.Add(new(ReleaseId.New(),label,Environment.ProcessPath,new string('a',64),null,SampleData.TestDirectory(),new string('b',64),"fixture"){ComparisonAxis=axis});
        return draft.Freeze();
    }
    [TestMethod] public void ScheduleModesAreSeparateBalancedAndAlwaysUseFreshTrialIds()
    {
        foreach(var mode in new[]{BenchmarkModes.FixedCommands,BenchmarkModes.AiCreation,BenchmarkModes.FixedCommands|BenchmarkModes.AiCreation})
        {
            var plan=Plan(mode);var schedule=BenchSchedule.Build(plan,BenchOptions.Default);
            Assert.AreEqual(schedule.Length,schedule.Select(x=>x.TrialId).Distinct().Count());
            CollectionAssert.AreEqual(new[]{"A","B","B","A"},schedule.Take(4).Select(x=>x.Release.Label).ToArray());
            Assert.AreEqual(mode.HasFlag(BenchmarkModes.FixedCommands),schedule.Any(x=>x.Experiment.Mode==ExecutionMode.FixedCommands));
            Assert.AreEqual(mode.HasFlag(BenchmarkModes.AiCreation),schedule.Any(x=>x.Experiment.Mode==ExecutionMode.AiCreation));
        }
        Assert.ThrowsExactly<InvalidOperationException>(()=>BenchOptions.Default.Validate(BenchmarkModes.None));
    }
    [TestMethod] public void CliOnlyRejectsDifferentConnectorHashesAndUnsetAxis()
    {
        Assert.ThrowsExactly<InvalidOperationException>(()=>BenchSchedule.Build(Plan(BenchmarkModes.FixedCommands,ComparisonAxis.Unset),BenchOptions.Default));
        var plan=Plan(BenchmarkModes.FixedCommands,ComparisonAxis.CliOnly);
        var draft=new RunDraft{Tool=plan.Tool,Project=plan.Project,Modes=plan.Modes};draft.Releases.Add(plan.Releases[0]);draft.Releases.Add(plan.Releases[1] with{ConnectorSha256=new string('c',64)});
        Assert.ThrowsExactly<InvalidOperationException>(()=>BenchSchedule.Build(draft.Freeze(),BenchOptions.Default));
    }
    private sealed class FixtureDiscovery(string root):IInstanceDiscovery
    {
        public BridgeInstance Find(BridgeTarget target,int? expectedPort=null)=>new(root,Environment.ProcessId,8090,"ready","fixture","0.2.1",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),false,DateTimeOffset.UtcNow);
    }
    private sealed class FixtureRunner(string root,string corrupt):IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessCommand command,TimeSpan timeout,Action<ProcessFrame>? observe=null,CancellationToken cancellationToken=default)
        {
            var args=command.Arguments;string output;
            int index=args.IndexOf("--params");
            if(index>=0)
            {
                using var doc=JsonDocument.Parse(args[index+1]);var p=doc.RootElement;string action=p.GetProperty("action").GetString()!;
                object value=action switch{"echo"=>corrupt=="echo"?41:42,"payload"=>new string('A',p.GetProperty("size").GetInt32()-1),"inspect"=>false,"revision"=>1,"state"=>new{playing=false,compiling=false},_=>1000};
                output=JsonSerializer.Serialize(new{success=true,data=new{nonce=p.GetProperty("nonce").GetString(),projectPath=corrupt=="foreign"?Path.GetTempPath():root,pid=Environment.ProcessId,value}});
            }
            else output="{\"success\":true}";
            if(corrupt=="json")output="{bad";
            return Task.FromResult(new ProcessResult(ProcessOutcome.Exited,corrupt=="exit"?7:0,output,"",1,123,DateTimeOffset.UtcNow));
        }
    }
    [TestMethod] public async Task FixedValidatorsRejectFastWrongAnswersTruncationMissingObjectsOldCodeAndFalsePlay()
    {
        string root=SampleData.TestDirectory();Directory.CreateDirectory(Path.Combine(root,"Assets","DeskBenchmark","Editor"));
        foreach(var (id,variant,corrupt) in new[]{("F01","cold","echo"),("F02","1","objects"),("F03","1024","payload"),("F04","revision","revision"),("F05","cycle","state"),("F01","cold","foreign")})
        {
            var target=new BridgeTarget(root,Environment.ProcessPath!,Environment.ProcessPath!,new string('0',64));
            var bridge=new BridgeAdapter(new FixtureRunner(root,corrupt),new FixtureDiscovery(root),root);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>new FixedExperiments(bridge).RunAsync(target,new(id,"fixture",variant,ExecutionMode.FixedCommands),BenchOptions.Default,(_,_)=>{},default));
        }
    }
    [TestMethod] public async Task BridgeErrorsKeepMalformedJsonSeparateFromNonzeroExit()
    {
        string root=SampleData.TestDirectory();var target=new BridgeTarget(root,Environment.ProcessPath!,Environment.ProcessPath!,new string('0',64));
        foreach(var (kind,expected) in new[]{("json",BridgeFailure.BadJson),("exit",BridgeFailure.NonzeroExit)})
        {
            var bridge=new BridgeAdapter(new FixtureRunner(root,kind),new FixtureDiscovery(root),root);
            var error=await Assert.ThrowsExactlyAsync<BridgeException>(()=>bridge.CallAsync(target,["status"],TimeSpan.FromSeconds(1)));
            Assert.AreEqual(expected,error.Failure);
        }
    }
    [TestMethod] public void SummaryExcludesFailedTimesKeepsRunsSpecsAndModesSeparate()
    {
        var run=RunId.New();var project=ProjectId.New();var release=ReleaseId.New();
        TrialResult Result(string outcome,double time,string spec="v1",string experiment="F01",RunId? other=null)=>new(
            new(other??run,ExecutionId.New(),project,ToolKind.Benchmark,experiment.StartsWith('F')?ExecutionMode.FixedCommands:ExecutionMode.AiCreation,TrialId.New()),"A",experiment,"cold",1,spec,"Pilot",outcome,null,10,time,5,2,null,true,1,null,null,null,[],"C:/fixture",false){ReleaseId=release};
        var results=new[]{Result("Succeeded",100),Result("Succeeded",300),Result("Failed",1),Result("Cancelled",2),Result("Failed",1,"v2"),Result("Succeeded",90,experiment:"A01"),Result("Succeeded",1,other:RunId.New())};
        var groups=HistoryStore.Summarize(results);Assert.AreEqual(4,groups.Count);
        var first=groups.Single(x=>x.Trials==4);Assert.AreEqual(200,first.MedianMs);Assert.AreEqual(200,first.MeanMs);Assert.AreEqual(2,first.Failed);
        Assert.IsNull(groups.Single(x=>x.Specification=="v2").MeanMs);
        Assert.Contains("Pilot",HistoryStore.Csv(results));Assert.Contains("Cancelled",HistoryStore.Csv(results));
    }
    [TestMethod] public async Task CorruptHistoryIsVisibleAndHasNoFabricatedRows()
    {
        string root=SampleData.TestDirectory();string run=Path.Combine(root,"runs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(run);File.WriteAllText(Path.Combine(run,"status.json"),"{broken");
        var history=await new HistoryStore(root).ReadAsync();Assert.AreEqual(1,history.Count);Assert.IsNull(history[0].Status);Assert.AreEqual(0,HistoryStore.ReadTrials(run).Count);
    }
    [TestMethod] public void AiTaskInstructionsAreStableAndHaveDifferentFollowupCounts()
    {
        Assert.AreEqual(1,AiTasks.Instructions("A01").Length);Assert.AreEqual(3,AiTasks.Instructions("A02").Length);Assert.AreEqual(2,AiTasks.Instructions("A03").Length);
        CollectionAssert.AreEqual(AiTasks.Instructions("A02"),AiTasks.Instructions("A02"));
    }
}
