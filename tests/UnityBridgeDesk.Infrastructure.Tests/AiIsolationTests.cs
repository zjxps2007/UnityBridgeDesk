using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Tests;

namespace UnityBridgeDesk.Infrastructure.Tests;

[TestClass]
public sealed class AiIsolationTests
{
    private sealed class ProviderFixture : IProcessRunner
    {
        public readonly List<ProcessCommand> Commands=[];
        private readonly Dictionary<string,string> sessions=[];
        public Task<ProcessResult> RunAsync(ProcessCommand command,TimeSpan timeout,Action<ProcessFrame>? observe=null,CancellationToken cancellationToken=default)
        {
            Commands.Add(command);string home=command.Environment!["CODEX_HOME"];
            if(!sessions.TryGetValue(home,out string? id))sessions[home]=id=Guid.NewGuid().ToString();
            string output=JsonSerializer.Serialize(new{type="thread.started",thread_id=id})+"\n"+
                "{\"type\":\"item.completed\",\"item\":{\"id\":\"msg\",\"type\":\"agent_message\",\"text\":\"synthetic only\"}}\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":2}}\n";
            observe?.Invoke(new(command.CallId,1,1,"stdout",output[..12]));observe?.Invoke(new(command.CallId,2,2,"stdout",output[12..]));
            return Task.FromResult(new ProcessResult(ProcessOutcome.Exited,0,output,"",1,1,DateTimeOffset.UtcNow));
        }
    }
    [TestMethod] public async Task NewTasksSeedAuthenticationOnlyFollowupsUseExactIdAndDisposeRemovesPrivateCopy()
    {
        string root=SampleData.TestDirectory(),auth=Path.Combine(root,"configured-auth"),managed=Path.Combine(root,"private");Directory.CreateDirectory(auth);
        File.WriteAllText(Path.Combine(auth,"auth.json"),"{\"synthetic_auth\":true}");
        File.WriteAllText(Path.Combine(auth,"memory.md"),"MUST NOT LEAK");Directory.CreateDirectory(Path.Combine(auth,"sessions"));
        var options=new AiOptions(Environment.ProcessPath!,"fixture-model","medium",auth,30,10,null,true);
        var target=new BridgeTarget(root,Environment.ProcessPath!,Environment.ProcessPath!,new string('0',64));
        var fixture=new ProviderFixture();var runner=new CodexRunner(fixture);
        string firstHome;
        await using(var first=await runner.CreateAsync(options,target,managed,default))
        {
            var initial=await first.SendAsync("first",(_,_)=>{},default);Assert.IsTrue(initial.Completed);
            firstHome=fixture.Commands[0].Environment!["CODEX_HOME"];
            Assert.IsFalse(File.Exists(Path.Combine(firstHome,"memory.md")));Assert.IsFalse(Directory.Exists(Path.Combine(firstHome,"sessions")));
            var followup=await first.SendAsync("followup",(_,_)=>{},default);
            Assert.AreEqual(initial.ThreadId,followup.ThreadId);Assert.Contains("resume",fixture.Commands[1].Arguments);Assert.Contains(initial.ThreadId!,fixture.Commands[1].Arguments);
            Assert.IsFalse(fixture.Commands[1].Arguments.Contains("--last"));
        }
        Assert.IsFalse(Directory.Exists(firstHome));Assert.IsTrue(File.Exists(Path.Combine(auth,"auth.json")));
        await using(var next=await runner.CreateAsync(options,target,managed,default))
        {
            var result=await next.SendAsync("new-task",(_,_)=>{},default);Assert.IsTrue(result.Completed);
            Assert.AreNotEqual(firstHome,fixture.Commands[2].Environment!["CODEX_HOME"]);Assert.IsFalse(fixture.Commands[2].Arguments.Contains("resume"));
        }
    }
    [TestMethod] public async Task MissingAuthenticationFailsBeforeAProviderProcessStarts()
    {
        string root=SampleData.TestDirectory();var fixture=new ProviderFixture();
        var options=new AiOptions(Environment.ProcessPath!,"fixture","medium",root,30,10,null,true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new CodexRunner(fixture).CreateAsync(options,new(root,Environment.ProcessPath!,Environment.ProcessPath!,new string('0',64)),Path.Combine(root,"private"),default));
        Assert.AreEqual(0,fixture.Commands.Count);
    }
}
