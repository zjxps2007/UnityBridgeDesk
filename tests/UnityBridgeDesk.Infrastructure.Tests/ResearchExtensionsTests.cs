using System.Text.Json;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;
[TestClass]
public sealed class ResearchExtensionsTests
{
    [TestMethod]
    public void RandomizedConditionRoundsKeepEveryConditionAndBalanceAllTargetPositions()
    {
        var options = new SpeedOptions(Repeats:12, Experiments:["F01","F02","F03"], Seed:456);
        string[] tags = ["A","B","C","D"];
        var plan = SpeedProtocol.Schedule(options,tags);
        var repeated = SpeedProtocol.Schedule(options,tags);
        CollectionAssert.AreEqual(plan.Select(t=>(t.Order,t.Block,t.Tag,t.Experiment,t.Variant)).ToArray(), repeated.Select(t=>(t.Order,t.Block,t.Tag,t.Experiment,t.Variant)).ToArray());
        Assert.IsFalse(plan.Select(t=>t.Id).Intersect(repeated.Select(t=>t.Id)).Any());
        int cases = SpeedProtocol.Cases(options).Length;
        foreach(var round in plan.Chunk(cases*tags.Length))
            Assert.AreEqual(cases,round.Select(t=>(t.Experiment,t.Variant)).Distinct().Count());
        foreach(var condition in plan.GroupBy(t=>(t.Experiment,t.Variant)))
        foreach(var tag in tags)
        for(int position=0;position<tags.Length;position++)
            Assert.AreEqual(3,condition.GroupBy(t=>t.Block).Count(g=>g.ElementAt(position).Tag==tag));
        Assert.IsGreaterThan(1,plan.GroupBy(t=>(t.Block-1)/cases).Select(g=>string.Join("/",g.GroupBy(t=>t.Block).Select(b=>b.First().Variant))).Distinct().Count());
    }
    [TestMethod]
    public async Task RuntimeManifestIncludesManagedDependenciesAndAssetsButNotPrivateJsonOrOtherBuilds()
    {
        string root = SampleData.TestDirectory(); Directory.CreateDirectory(Path.Combine(root,"Assets"));
        await File.WriteAllTextAsync(Path.Combine(root,"Worker.exe"),"native");
        await File.WriteAllTextAsync(Path.Combine(root,"Core.dll"),"first");
        await File.WriteAllTextAsync(Path.Combine(root,"Worker.deps.json"),"{}");
        await File.WriteAllTextAsync(Path.Combine(root,"auth.json"),"private");
        await File.WriteAllTextAsync(Path.Combine(root,"Assets","DeskProbe.cs.txt"),"probe");
        Directory.CreateDirectory(Path.Combine(root,"old-build")); await File.WriteAllTextAsync(Path.Combine(root,"old-build","Old.dll"),"unused");
        var first=await ResearchRuntime.Capture(root,root,CancellationToken.None);
        Assert.AreEqual(8,first.Files.Length); Assert.IsFalse(first.Files.Any(f=>f.RelativePath.Contains("auth") || f.RelativePath.Contains("Old.dll")));
        Assert.AreEqual(first.Sha256,(await ResearchRuntime.Capture(root,root,CancellationToken.None)).Sha256);
        await File.WriteAllTextAsync(Path.Combine(root,"Core.dll"),"changed");
        Assert.AreNotEqual(first.Sha256,(await ResearchRuntime.Capture(root,root,CancellationToken.None)).Sha256);
    }
    [TestMethod]
    public void SceneOracleChecksActualIdsCountAndPositionRegardlessOfEnumerationOrder()
    {
        var objects = Enumerable.Range(0,1000).Reverse().Select(i=>new{name="DeskFixed_"+i,x=i%100,y=i/100,z=i%7}).ToArray();
        SceneOracle.Validate(JsonSerializer.SerializeToElement(objects),1000);
        Assert.ThrowsExactly<SpeedMeasurementException>(()=>SceneOracle.Validate(JsonSerializer.SerializeToElement(objects.Take(999)),1000));
        var duplicate=objects.ToArray();duplicate[1]=duplicate[0];
        Assert.ThrowsExactly<SpeedMeasurementException>(()=>SceneOracle.Validate(JsonSerializer.SerializeToElement(duplicate),1000));
        var wrong=objects.ToArray();wrong[0]=new{name="DeskFixed_999",x=0,y=0,z=0};
        Assert.ThrowsExactly<SpeedMeasurementException>(()=>SceneOracle.Validate(JsonSerializer.SerializeToElement(wrong),1000));
        SceneOracle.Validate(JsonSerializer.SerializeToElement(new[]{new{name="DeskFixed_0",x=0.000001,y=0d,z=0d}}),1);
        Assert.ThrowsExactly<SpeedMeasurementException>(()=>SceneOracle.Validate(JsonSerializer.SerializeToElement(new[]{new{name="DeskFixed_0",x=0.001,y=0d,z=0d}}),1));
    }
    [TestMethod]
    public void CompletionKeepsPreparationCleanupCancellationAndNotRunInPlannedDenominator()
    {
        var report=new SpeedReport(SpeedReportSample.Create());
        var rows=ResearchOutcomes.Completion(report);
        Assert.AreEqual(6,rows.Sum(r=>r.Planned));Assert.AreEqual(5,rows.Sum(r=>r.Recorded));
        Assert.AreEqual(3,rows.Sum(r=>r.Valid));Assert.AreEqual(2,rows.Sum(r=>r.Failed));Assert.AreEqual(1,rows.Sum(r=>r.NotRun));
        Assert.IsTrue(rows.All(r=>r.Planned==r.Valid+r.Failed+r.Cancelled+r.NotRun));
    }
    [TestMethod]
    public async Task ExportedSceneEvidenceIsBoundToItsTrialAndRejectsChangedFiles()
    {
        var original=SpeedReportSample.Create();
        var plan=original.Plan.Select(t=>t with {Experiment="F02",Variant="1"}).ToArray();
        var results=original.Results.Select((r,i)=>r with {Trial=plan[i]}).ToArray();
        string directory=SampleData.TestDirectory();
        var objects=Enumerable.Range(0,1000).Select(i=>new{name="DeskFixed_"+i,x=i%100,y=i/100,z=i%7}).ToArray();
        foreach(int i in Enumerable.Range(0,results.Length).Where(i=>SpeedAnalysis.IsValid(results[i])))
        {
            string file=Path.Combine(directory,plan[i].Id.ToString("N"),"scene-observed.json");
            await SpeedFiles.Write(file,objects);
            results[i]=results[i] with {Guest=results[i].Guest! with {SceneEvidenceSha256=await SpeedFiles.Hash(file)}};
        }
        var run=original with{Plan=plan,Results=results,Options=original.Options with{Experiments=["F02"]}};
        run=run with{ResearchPlan=SpeedResearch.Freeze(run,"fixture","worker")};
        string archive=Path.Combine(directory,"scene.zip");
        await ResearchBundle.Write(archive,new SpeedReport(run),runEvidenceDirectory:directory);
        using(var zip=System.IO.Compression.ZipFile.OpenRead(archive))
            Assert.AreEqual(3,zip.Entries.Count(e=>e.Name.StartsWith("scene-") && e.Name.EndsWith(".json")));
        string fixtureOutput=Environment.GetEnvironmentVariable("DESK_RESEARCH_FIXTURE") ?? "";
        if(fixtureOutput.Length>0) {Directory.CreateDirectory(fixtureOutput);File.Copy(archive,Path.Combine(fixtureOutput,"scene.zip"),true);}
        await File.WriteAllTextAsync(Path.Combine(directory,plan[0].Id.ToString("N"),"scene-observed.json"),"[]");
        await Assert.ThrowsExactlyAsync<IOException>(()=>ResearchBundle.Write(Path.Combine(directory,"changed.zip"),new SpeedReport(run),runEvidenceDirectory:directory));
        Assert.IsFalse(File.Exists(Path.Combine(directory,"changed.zip")));
    }
    [TestMethod]
    public void OldFrozenPlansRemainReadableAndNewSessionNotesArePartOfTheFrozenIdentity()
    {
        var run=SpeedReportSample.Methodology();var plan=SpeedResearch.Freeze(run,"fixture","worker");
        string old=plan.Payload.Replace("desk-research-v2","desk-research-v1");
        run=run with {ResearchPlan=new(old,SpeedResearch.Digest(old))};
        Assert.AreEqual("사전 고정 계획 일치",SpeedResearch.PlanStatus(run));
        Assert.Contains("불일치",SpeedResearch.PlanStatus(run with{Options=run.Options with{Research=new(StudyGroup:"changed")}}));
        string json=JsonSerializer.Serialize(new ResearchSettings(),SpeedProtocol.Json);
        Assert.IsFalse(json.Contains("StudyGroup"));Assert.IsFalse(json.Contains("SessionNote"));
    }
    [TestMethod]
    public void SensitivityReportsActualInternalAndExternalChangesWithoutClaimingProductSuperiority()
    {
        var run=SpeedReportSample.Methodology(20,1);var releases=SpeedResearch.AaTargets(run.Releases[0]);
        var options=run.Options with{Research=new("sensitivity","known Unity delay",StudyGroup:"path-validation")};options.Validate();
        SpeedResearch.ValidateTargets(options,releases);
        Assert.ThrowsExactly<ArgumentException>(()=>(options with{Experiments=["F02"]}).Validate());
        var plan=run.Plan.Select((t,i)=>t with{Tag=releases[i%2].Tag}).ToArray();
        var results=run.Results.Select((r,i)=>r with{Trial=plan[i],Guest=r.Guest! with{WorkMs=i%2==0?100:201,Samples=[new(0,i%2==0?100:201,10,"success",InnerDelayMs:i%2==0?0:101)]}}).ToArray();
        run=run with{Options=options,Releases=releases,Plan=plan,Results=results};run=run with{ResearchPlan=SpeedResearch.Freeze(run,"fixture","worker")};
        var report=new SpeedReport(run);
        Assert.Contains("실제 내부 증가 101.00ms",report.Comparisons[0].Inference);
        Assert.Contains("잔차 0.00ms",report.Comparisons[0].Inference);Assert.IsNull(SpeedStatistics.FollowupOptions(report));
        var missing=new SpeedReport(run with{Results=results.Select(r=>r with{Guest=r.Guest! with{Samples=[new(0,100,10,"success")]}}).ToArray()});
        Assert.Contains("자료 부족",missing.Comparisons[0].Inference);
    }
}
