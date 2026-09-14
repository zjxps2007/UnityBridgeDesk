using System.Collections.Immutable;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Desktop.Tools;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Desktop.Tests;

[TestClass]
public sealed class RunnerInputTests
{
    [TestMethod]
    public void InvalidNumbersIdentifyTheFieldAndAllowedRange()
    {
        foreach(string text in new[]{"", "1.", "2.5", "0", "101", "999999999999"})
        {
            var error=Assert.Throws<RunnerInputException>(()=>RunnerInput.Integer("repeats",text));
            Assert.AreEqual("repeats",error.Field);
            StringAssert.Contains(error.Message,"독립 반복 수");
            StringAssert.Contains(error.Message,"1~100");
        }
        Assert.AreEqual(100,RunnerInput.Integer("repeats"," 100 "));
        Assert.AreEqual(0,RunnerInput.Integer("warmups","0"));
        Assert.AreEqual(0,RunnerInput.Integer("repairs","0"));
        Assert.AreEqual(86400,RunnerInput.Integer("timeout","86400"));
        Assert.Throws<RunnerInputException>(()=>RunnerInput.Integer("timeout","86401"));
        Assert.Throws<RunnerInputException>(()=>RunnerInput.Integer("inner","0"));
    }

    [TestMethod]
    public async Task UnfinishedDraftSurvivesStorageWithoutBecomingAnExecutableConfiguration()
    {
        string path=Path.Combine(SampleData.TestDirectory(),"runner-draft.json");
        var store=new AtomicJsonStore<RunnerDraft>(path,x=>x.Validate());
        var draft=new RunnerDraft(new Dictionary<string,string>{{"repeats","1."},{"editor",""},{"model","작성 중인 모델"}}.ToImmutableDictionary(),
            [ReleaseId.New(),ReleaseId.New()],["F01","F03","A02"],true,true,false);
        Assert.AreEqual(SaveStatus.Saved,(await store.SaveAsync(draft)).Status);
        var restored=(await new AtomicJsonStore<RunnerDraft>(path,x=>x.Validate()).LoadAsync()).Value!;
        Assert.AreEqual("1.",restored.Fields["repeats"]);
        Assert.AreEqual("",restored.Fields["editor"]);
        CollectionAssert.AreEqual(draft.Releases.ToArray(),restored.Releases.ToArray());
        CollectionAssert.AreEqual(draft.Experiments.ToArray(),restored.Experiments.ToArray());
        Assert.Throws<RunnerInputException>(()=>RunnerInput.Integer("repeats",restored.Fields["repeats"]));
        Assert.AreEqual(SaveStatus.InvalidData,(await store.SaveAsync(draft with{Fields=draft.Fields.Add("permission","true")})).Status);
        Assert.AreEqual(SaveStatus.InvalidData,(await store.SaveAsync(draft with{Releases=[draft.Releases[0],draft.Releases[0]]})).Status);
        Assert.AreEqual("1.",(await store.LoadAsync()).Value!.Fields["repeats"]);
    }
}
