using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Tools;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class BenchmarkPresentationTests
{
    private static TrialResult Trial(RunPlan plan,string experiment,string variant,double work,params double[] calls)=>new(
        SampleData.Route(plan),"0.2.1",experiment,variant,1,"draft-v1","Pilot","Succeeded",null,
        120000,work,1,20000,null,null,0,null,null,null,[..calls.Select(x=>new TimingSample("echo",x,2))],"fixture",false)
        {ReleaseId=plan.Releases[0].Id};

    [TestMethod]
    public void ResponseResultsAverageEachIndependentTrialAndExcludeFailures()
    {
        var plan=SampleData.Draft().Freeze();
        var first=Trial(plan,"F01","warm",400,100,300);
        var second=Trial(plan,"F01","warm",1000,1000);
        var failed=Trial(plan,"F01","warm",25,25) with{Outcome="Failed"};
        var result=BenchmarkPresentation.Summarize([first,second,failed]).Single();
        Assert.AreEqual(600d,result.MeanMs);Assert.AreEqual(200d,result.MinimumMs);Assert.AreEqual(1000d,result.MaximumMs);
        Assert.AreEqual(3,result.Trials);Assert.AreEqual(2,result.Succeeded);Assert.AreEqual(1,result.Failed);
        Assert.AreEqual(400d,first.WorkMs); // Presentation never rewrites the evidence.
    }
    [TestMethod]
    public void OtherExperimentsKeepWholeWorkAndMissingResponseCallsStayUnknown()
    {
        var plan=SampleData.Draft().Freeze();
        Assert.AreEqual(300d,BenchmarkPresentation.Summarize([Trial(plan,"F02","10",300,100,100,100)]).Single().MeanMs);
        Assert.IsNull(BenchmarkPresentation.Summarize([Trial(plan,"F01","warm",300)]).Single().MeanMs);
        Assert.AreEqual(300d,BenchmarkPresentation.Summarize([Trial(plan,"F01","cold",300)]).Single().MeanMs);
        Assert.AreEqual("Unity 준비 후 첫 명령",BenchmarkPresentation.Condition("F01","cold"));
    }
    [TestMethod]
    public void DifferentReleaseIdentitiesRemainSeparateEvenWithSameLabel()
    {
        var plan=SampleData.Draft().Freeze();var first=Trial(plan,"F01","cold",100,100);
        var second=Trial(plan,"F01","cold",200,200) with{ReleaseId=ReleaseId.New()};
        Assert.AreEqual(2,BenchmarkPresentation.Summarize([first,second]).Count);
    }
}
