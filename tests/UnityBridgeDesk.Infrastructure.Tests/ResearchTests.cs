using System.IO.Compression;
using System.Text.Json;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class ResearchTests
{
    [TestMethod]
    public async Task CancelledBundleDoesNotLeaveAnArchiveOrTemporaryFile()
    {
        string directory = SampleData.TestDirectory();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ResearchBundle.Write(
            Path.Combine(directory,"cancelled.zip"),new SpeedReport(SpeedReportSample.Create()),new CancellationToken(true)));
        Assert.AreEqual(0,Directory.GetFiles(directory).Length);
    }
    [TestMethod]
    public void AaKeepsBinaryIdentityAndRcVersionExceptionWhileRejectingMismatches()
    {
        var source = SpeedReportSample.Create().Releases[0] with { Tag = "v0.2.2-rc.1", Version = "0.2.2-rc.1", Commit = "74639d7b3f3adf550d58cc853b715f878153839f" };
        var targets = SpeedResearch.AaTargets(source);
        var options = new SpeedOptions(Repeats:20, Research:new("aa", "synthetic validation",1));
        SpeedResearch.ValidateTargets(options, targets);
        Assert.AreEqual("0.2.1", CliDistribution.ReportedConnectorVersion(targets[1]));
        Assert.AreEqual(source.CliPath, targets[1].CliPath);
        targets[1] = targets[1] with { CliSha256 = "changed" };
        Assert.ThrowsExactly<ArgumentException>(() => SpeedResearch.ValidateTargets(options, targets));
        Assert.ThrowsExactly<ArgumentException>(() => SpeedResearch.ValidateTargets(new(), SpeedResearch.AaTargets(source)));
        Assert.ThrowsExactly<ArgumentException>(() => SpeedBenchWorkflow.ValidateSelection(2,false,false,true));
        SpeedBenchWorkflow.ValidateSelection(1,false,false,true);
    }
    [TestMethod]
    public void AaSchedulesUniqueTrialsWithBalancedOrderAndNoSampleReuse()
    {
        var options = new SpeedOptions(Repeats:20, Research:new("aa","same bytes",1));
        var tags = SpeedResearch.AaTargets(SpeedReportSample.Create().Releases[0]).Select(r=>r.Tag).ToArray();
        var plan = SpeedProtocol.Schedule(options,tags);
        Assert.AreEqual(plan.Length,plan.Select(t=>t.Id).Distinct().Count());
        foreach(var group in plan.GroupBy(t=>(t.Experiment,t.Variant)))
            Assert.AreEqual(10,group.GroupBy(t=>t.Block).Count(b=>b.First().Tag==tags[0]));
        Assert.ThrowsExactly<ArgumentException>(()=>SpeedResearch.ValidateTargets(options with {Repeats=21},SpeedResearch.AaTargets(SpeedReportSample.Create().Releases[0])));
        Assert.ThrowsExactly<ArgumentException>(()=>new SpeedOptions(Research:new("confirmatory","",1)).Validate());
    }
    [TestMethod]
    public void FrozenPlanDetectsChangedOptionsTargetOrderAndHashButAllowsResultsToArrive()
    {
        var run=SpeedReportSample.Methodology();
        var frozen=SpeedResearch.Freeze(run,"fixture","worker");
        run=run with {ResearchPlan=frozen};
        Assert.AreEqual("사전 고정 계획 일치",SpeedResearch.PlanStatus(run));
        Assert.AreEqual("사전 고정 계획 일치",SpeedResearch.PlanStatus(run with {Status="cancelled",Results=[]}));
        Assert.Contains("불일치",SpeedResearch.PlanStatus(run with {Options=run.Options with {Calls=25}}));
        Assert.Contains("불일치",SpeedResearch.PlanStatus(run with {Releases=run.Releases.Reverse().ToArray()}));
        Assert.Contains("무결성",SpeedResearch.PlanStatus(run with {ResearchPlan=frozen with {Payload=frozen.Payload+" "}}));
        Assert.Contains("과거",SpeedResearch.PlanStatus(run with {ResearchPlan=null}));
    }
    [TestMethod]
    public void AaCannotCertifyInsufficientIncompleteTrendingOrOutOfToleranceData()
    {
        var original=SpeedReportSample.Methodology(20);
        var releases=SpeedResearch.AaTargets(original.Releases[0]);
        var options=original.Options with {Research=new("aa","synthetic tolerance check",1)};
        var plan=original.Plan.Select(t=>t with {Tag=t.Order%2==1?releases[0].Tag:releases[1].Tag}).ToArray();
        var random=new Random(456);
        var results=original.Results.Select((r,i)=>
        {
            double x=100+random.NextDouble()*.01;
            return r with {Trial=plan[i],Guest=r.Guest! with {WorkMs=x,Samples=[new(0,x,10,"success")]}};
        }).ToArray();
        var run=original with {Releases=releases,Options=options,Plan=plan,Results=results};
        run=run with {ResearchPlan=SpeedResearch.Freeze(run,"fixture","worker")};
        var report=new SpeedReport(run);
        Assert.Contains("허용 ±",SpeedResearch.Assessment(report,report.Comparisons[0]));
        var incomplete=new SpeedReport(run with {Status="cancelled"});
        Assert.Contains("자료 부족",SpeedResearch.Assessment(incomplete,incomplete.Comparisons[0]));
        var changed=report.Comparisons[0] with {Evidence=report.Comparisons[0].Evidence! with {Lower=1.05,Upper=1.1}};
        Assert.Contains("밖",SpeedResearch.Assessment(report,changed));
        Assert.Contains("불충분",SpeedResearch.Assessment(report,changed with {Evidence=changed.Evidence! with {Lower=.9,Upper=1.1}}));
        Assert.Contains("유보",SpeedResearch.Assessment(report,changed with {Evidence=changed.Evidence! with {SequenceWarning=true}}));
        Assert.IsNull(SpeedStatistics.FollowupOptions(report));
    }
    [TestMethod]
    public async Task ResearchBundlePreservesRawFailuresPlanAndIndependentVerifierWithoutSweepingLogs()
    {
        var run=SpeedReportSample.Create();
        run=run with {ResearchPlan=SpeedResearch.Freeze(run,"fixture","worker")};
        string directory=SampleData.TestDirectory(), path=Path.Combine(directory,"research.zip");
        await File.WriteAllTextAsync(Path.Combine(directory,"auth.json"),"private");
        await ResearchBundle.Write(path,new SpeedReport(run));
        using var zip=ZipFile.OpenRead(path);
        Assert.IsNull(zip.GetEntry("auth.json"));
        Assert.IsNotNull(zip.GetEntry("verify_analysis.py"));
        using var reader=new StreamReader(zip.GetEntry("run.json")!.Open());
        var actual=JsonSerializer.Deserialize<SpeedRun>(await reader.ReadToEndAsync(),SpeedProtocol.Json)!;
        Assert.AreEqual(run.Results.Length,actual.Results.Length);
        Assert.AreEqual(run.ResearchPlan,actual.ResearchPlan);
        Assert.IsTrue(actual.Results.Any(r=>r.Status=="failed"));
        await Assert.ThrowsExactlyAsync<IOException>(()=>ResearchBundle.Write(path,new SpeedReport(run)));
        // Developer verification runs Python separately against this unmodified archive.
        string fixtureOutput=Environment.GetEnvironmentVariable("DESK_RESEARCH_FIXTURE") ?? "";
        if(fixtureOutput.Length>0)
        {
            Directory.CreateDirectory(fixtureOutput);
            string copy=Path.Combine(fixtureOutput,"legacy.zip");
            File.Copy(path,copy,true);
            var normal=SpeedReportSample.Methodology(30);
            normal=normal with {ResearchPlan=SpeedResearch.Freeze(normal,"fixture","worker")};
            await ResearchBundle.Write(Path.Combine(fixtureOutput,Guid.NewGuid().ToString("N")+".zip"),new SpeedReport(normal));
        }
    }
}
