using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Execution;
using UnityBridgeDesk.Infrastructure.Storage;
using UnityBridgeDesk.Infrastructure.Ai;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if(args is ["--compare-tools",var comparisonPath])
{
    using var stop=new CancellationTokenSource();
    Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
    try
    {
        var comparison=await UnityBridgeDesk.Infrastructure.SpeedBench.SpeedFiles.Read<UnityBridgeDesk.Infrastructure.SpeedBench.OfficialComparisonRequest>(comparisonPath);
        var result=await UnityBridgeDesk.Infrastructure.SpeedBench.OfficialComparison.Run(comparison,AppContext.BaseDirectory,
            new Progress<string>(Console.WriteLine),stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(new{result.Id,result.Status,valid=result.Results.Count(r=>r.Status=="success"),planned=result.Plan.Length}));
        return result.Status=="completed" && result.Results.All(r=>r.Status=="success") ? 0 : 1;
    }
    catch(OperationCanceledException) {Console.Error.WriteLine("비교 준비 중단");return 130;}
    catch(Exception error) {Console.Error.WriteLine(error.Message);return 1;}
}
if(args is ["--local-trial",var localRequest,var localResult])
    return await UnityBridgeDesk.Infrastructure.SpeedBench.SpeedGuest.RunLocalFile(localRequest,localResult);
if(args is ["--vm-trial",var vmRequest,var vmResult])
    return await UnityBridgeDesk.Infrastructure.SpeedBench.SpeedGuest.RunFile(vmRequest,vmResult);
if(args is ["--integration",var integration])return await DiagnosticIntegration.RunAsync(integration);
if(args is ["--recover-history",var historyRoot,var exportRoot])
{
    if(!Directory.Exists(Path.Combine(historyRoot,"runs")))throw new DirectoryNotFoundException("기존 기록 폴더가 필요합니다.");
    var history=new HistoryStore(historyRoot);int recovered=await history.RecoverInterruptedAsync();
    var records=await history.ReadAsync();
    foreach(var item in records)await HistoryStore.ExportAsync(item.Directory,Path.Combine(exportRoot,Path.GetFileName(item.Directory)));
    Console.WriteLine(JsonSerializer.Serialize(new{recovered,runs=records.Count,exports=Path.GetFullPath(exportRoot)}));return 0;
}
if(args is ["--seed-catalog",var seedPath])
{
    using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(seedPath));var config=doc.RootElement;
    using var catalog=new CatalogService(config.GetProperty("dataRoot").GetString()!);await catalog.LoadAsync();
    var registered=await catalog.RegisterProjectAsync(config.GetProperty("project").GetString()!);
    if(!registered.Success && catalog.Document.Projects.Length==0)throw new IOException(registered.Message);
    foreach(var release in config.GetProperty("releases").EnumerateArray())
    {
        string cli=release.GetProperty("cli").GetString()!,connector=release.GetProperty("connector").GetString()!,label=release.GetProperty("label").GetString()!;
        await catalog.RegisterArtifactAsync(cli,ArtifactKind.CliExecutable,label+" CLI");
        await catalog.RegisterArtifactAsync(connector,ArtifactKind.ConnectorFolder,label+" Connector");
        var cliEntry=catalog.Document.Artifacts.Single(x=>LocalInspector.NormalizePath(x.Path)==LocalInspector.NormalizePath(cli));
        var connectorEntry=catalog.Document.Artifacts.Single(x=>LocalInspector.NormalizePath(x.Path)==LocalInspector.NormalizePath(connector));
        if(!catalog.Document.Releases.Any(x=>x.Label==label))await catalog.AddReleaseAsync(label,ComparisonAxis.CliAndConnector,cliEntry.Id,connectorEntry.Id);
    }
    await catalog.SelectProjectAsync(catalog.Document.Projects[0].Project.Id);await catalog.SelectReleaseAsync(catalog.Document.Releases[^1].Id);
    Console.WriteLine("Catalog seeded with inspected local test releases; project unchanged.");return 0;
}
if(args is ["--smoke" or "--smoke-ai",var configuration])
{
    using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(configuration));var config=doc.RootElement;
    string data=config.GetProperty("dataRoot").GetString()!,source=config.GetProperty("project").GetString()!;
    var project=await new LocalInspector().InspectProjectAsync(source);
    bool synthetic=args[0]=="--smoke-ai";
    var draft=new RunDraft{Tool=ToolKind.Benchmark,Project=new(ProjectId.New(),"진단용 시험 복제본",source,project.EditorVersion),Modes=synthetic?BenchmarkModes.AiCreation:BenchmarkModes.FixedCommands};
    foreach(var release in config.GetProperty("releases").EnumerateArray())
    {
        string connector=release.GetProperty("connector").GetString()!,cli=release.GetProperty("cli").GetString()!;
        var observed=await new LocalInspector().InspectArtifactAsync(connector,ArtifactKind.ConnectorFolder);
        draft.Releases.Add(new(ReleaseId.New(),release.GetProperty("label").GetString()!,cli,ProjectFiles.HashFile(cli),null,connector,observed.Sha256,observed.DeclaredVersion){ComparisonAxis=ComparisonAxis.CliAndConnector,ConnectorHashScheme="connector-tree-v1"});
    }
    var experiments=config.GetProperty("experiments").EnumerateArray().Select(x=>x.GetString()!).ToArray();
    var options=new BenchOptions(synthetic?[]:[..experiments],synthetic?[..experiments]:[],Repeats:1,InnerCalls:2,TimeoutSeconds:config.TryGetProperty("timeoutSeconds",out var seconds)?seconds.GetInt32():180,PrepareTimeoutSeconds:600,KeepSuccessfulClones:false);
    var processRunner=new WorkerRunner(Environment.ProcessPath!);
    var runtime=new DeskRuntime(data,processRunner,synthetic?new DiagnosticAi(processRunner):null);runtime.Changed+=notice=>Console.WriteLine(JsonSerializer.Serialize(notice));
    AiOptions? profile=null;
    if(synthetic){string auth=Path.Combine(data,"synthetic-auth");Directory.CreateDirectory(auth);await File.WriteAllTextAsync(Path.Combine(auth,"auth.json"),"{\"fixture\":true}");profile=new(Environment.ProcessPath!,"SYNTHETIC-FIXTURE","medium",auth,options.TimeoutSeconds,100,null,true);draft.AiProfile=new(AiProfileId.New(),"SyntheticFixture","SYNTHETIC-FIXTURE","medium",null);}
    var plan=draft.Freeze();
    await runtime.ExecuteAsync(plan,new(config.GetProperty("editor").GetString()!,options,profile),CancellationToken.None);
    var result=await new AtomicJsonStore<RunStatus>(Path.Combine(data,"runs",plan.RunId.Value.ToString("N"),"status.json"),_=>{}).LoadAsync();
    return result.Value?.Status=="완료"?0:1;
}
// Diagnostic fixture is opt-in, never used by production calls.
if (args is ["--fixture", var behavior, ..])
{
    if (behavior == "echo") { Console.WriteLine(JsonSerializer.Serialize(args.Skip(2))); return 0; }
    if (behavior == "large") { await Task.WhenAll(Console.Out.WriteAsync(new string('한', 200000)), Console.Error.WriteAsync(new string('E', 200000))); return 0; }
    if (behavior == "hang") { await Task.Delay(Timeout.Infinite); return 0; }
    if (behavior == "spawn-hang")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--fixture"); info.ArgumentList.Add("hang");
        using var grandchild = Process.Start(info)!;
        Console.WriteLine(grandchild.Id); await Console.Out.FlushAsync(); await Task.Delay(Timeout.Infinite); return 0;
    }
    if (behavior == "fail") { Console.Error.WriteLine("fixture failure"); return 7; }
    if (behavior == "broken") { Console.Write("{bad"); return 0; }
    if (behavior == "ansi") { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);Console.OutputEncoding=Encoding.GetEncoding(949);Console.Write("한글 공백");return 0; }
    return 2;
}
var request = await Console.In.ReadLineAsync(); // No children before the parent job handshake.
if (request is null) return 2;
var command = JsonSerializer.Deserialize<ProcessCommand>(request);
if (command is null) return 2;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var childEncoding=Encoding.GetEncoding(command.OutputCodePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
long sequence = 0;
var outputLock = new SemaphoreSlim(1, 1);
async Task Emit(string kind, string text)
{
    await outputLock.WaitAsync();
    try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new ProcessFrame(command.CallId, ++sequence, Stopwatch.GetTimestamp(), kind, text))); await Console.Out.FlushAsync(); }
    finally { outputLock.Release(); }
}
try
{
    var info = new ProcessStartInfo(command.FileName) { WorkingDirectory = command.WorkingDirectory,
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = command.StandardInput is not null,
        StandardOutputEncoding = childEncoding, StandardErrorEncoding = childEncoding };
    foreach (var arg in command.Arguments) info.ArgumentList.Add(arg);
    if (command.Environment is not null) foreach (var entry in command.Environment) info.Environment[entry.Key] = entry.Value;
    using var child = Process.Start(info) ?? throw new IOException("Child did not start.");
    await Emit("started", JsonSerializer.Serialize(new { pid = child.Id, startedAt = child.StartTime.ToUniversalTime() }));
    async Task Drain(StreamReader reader, string kind)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) await Emit(kind, new string(buffer, 0, count));
    }
    async Task Input()
    {
        if (command.StandardInput is null) return;
        await child.StandardInput.WriteAsync(command.StandardInput);
        child.StandardInput.Close();
    }
    await Task.WhenAll(Drain(child.StandardOutput, "stdout"), Drain(child.StandardError, "stderr"), Input(), child.WaitForExitAsync());
    await Emit("exit", child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}
catch (Exception error) { await Emit("error", error.Message); return 1; }
