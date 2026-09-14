using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Benchmark;
using UnityBridgeDesk.Infrastructure.Bridge;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Installation;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Infrastructure.Execution;

public sealed record RuntimeNotice(ToolKind Tool, RunId RunId, string Message, bool Running);
public sealed record RuntimeProgress(RunId RunId,int Completed,int Total,string Release,string Experiment,string Variant,int Repeat,string Stage);
public sealed record RunStatus(RunId RunId, ToolKind Tool, string Project, string Status, DateTimeOffset UpdatedAt, string? Failure);

public sealed class DeskRuntime
{
    public string DataRoot { get; }
    private readonly IProcessRunner processes;
    private readonly IAiRunner ai;
    private readonly IExecutionCoordinator coordinator = new ExecutionCoordinator();
    private readonly Dictionary<ToolKind, (CancellationTokenSource Cancel, Task Task)> active = [];
    private readonly object sync = new();
    public event Action<RuntimeNotice>? Changed;
    public event Action<ExecutionState>? StateChanged;
    public event Action<RuntimeProgress>? ProgressChanged;
    public bool IsRunning { get { lock(sync) return active.Values.Any(x=>!x.Task.IsCompleted); } }
    public bool ToolRunning(ToolKind tool) { lock(sync) return active.TryGetValue(tool,out var value) && !value.Task.IsCompleted; }
    public DeskRuntime(string dataRoot, IProcessRunner processes, IAiRunner? ai = null)
    { DataRoot = dataRoot; this.processes = processes; this.ai = ai ?? new CodexRunner(processes); }
    public Task StartAsync(RunPlan plan, RunOptions options)
    {
        lock(sync)
        {
            if (ToolRunning(plan.Tool)) throw new InvalidOperationException("이 도구의 작업이 이미 진행 중입니다.");
            var cancel = new CancellationTokenSource();
            var task = Task.Run(() => ExecuteAsync(plan, options, cancel.Token));
            active[plan.Tool] = (cancel, task);
            return task;
        }
    }
    public void Cancel(ToolKind tool) { lock(sync) if (active.TryGetValue(tool,out var value)) value.Cancel.Cancel(); }
    public async Task StopAllAsync()
    {
        Task[] tasks;
        lock(sync) { foreach (var value in active.Values) value.Cancel.Cancel(); tasks = active.Values.Select(x=>x.Task).ToArray(); }
        try { await Task.WhenAll(tasks); } catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { }
    }
    private void Notice(RunPlan plan, string text, bool running = true) => Changed?.Invoke(new(plan.Tool, plan.RunId, text, running));
    public async Task ExecuteAsync(RunPlan plan, RunOptions options, CancellationToken ct)
    {
        string runDir = Path.Combine(DataRoot, "runs", plan.RunId.Value.ToString("N"));
        Directory.CreateDirectory(runDir);
        var status = new AtomicJsonStore<RunStatus>(Path.Combine(runDir,"status.json"), _=>{});
        async Task Status(string value, string? failure = null)
        {
            var saved = await status.SaveAsync(new(plan.RunId,plan.Tool,plan.Project.RootPath,value,DateTimeOffset.UtcNow,failure));
            if (saved.Status != SaveStatus.Saved) throw new IOException("작업 상태 기록 실패: " + saved.Status);
            Notice(plan,value,value is "대기 중" or "준비 중" or "실행 중");
        }
        try
        {
            await Status("대기 중");
            using var lease = await coordinator.AcquireAsync(plan,ct);
            using var hostLease = new FileStream(Path.Combine(DataRoot,"runtime.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            await Status("준비 중");
            UnityEnvironment.VerifyEditor(plan.Project, options.UnityExecutable);
            var observed = await new LocalInspector().InspectProjectAsync(plan.Project.RootPath);
            if (observed.Status != InspectionStatus.Available || observed.EditorVersion != plan.Project.UnityBuild)
                throw new InvalidDataException("프로젝트 경로·Editor 설정이 계획 이후 바뀌었습니다.");
            if (plan.Releases.Length == 0) throw new InvalidOperationException("릴리스를 선택하세요.");
            if (plan.Tool == ToolKind.AiWork || plan.Modes.HasFlag(BenchmarkModes.AiCreation))
                (options.Ai ?? throw new InvalidOperationException("AI 설정이 없습니다.")).Validate();
            var records = new RunRecordStore(Path.Combine(DataRoot,"runs"));
            if ((await records.CreatePlanAsync(plan,ct)).Status != SaveStatus.Saved) throw new IOException("실행 계획 기록 실패");
            await WriteDocument(Path.Combine(runDir,"options.json"),options,ct);
            ImmutableArray<TrialSchedule> schedule = plan.Tool == ToolKind.Benchmark ? BenchSchedule.Build(plan,options.Benchmark) :
                [new(1,1,plan.Releases[0], new(plan.Tool == ToolKind.Installation ? "INSTALL" : "AI-WORK", "", "", plan.Tool == ToolKind.Installation ? ExecutionMode.Installation : ExecutionMode.AiWork), TrialId.New())];
            if (schedule.Length > 1000) throw new InvalidOperationException("한 계획은 1,000 시행까지 지원합니다.");
            if(plan.Tool==ToolKind.Benchmark)ProjectFiles.RequireSelfContainedPackages(plan.Project.RootPath);
            string? baseline = plan.Tool == ToolKind.Benchmark ? await ProjectFiles.FingerprintAsync(plan.Project.RootPath,ct) : null;
            var inputFiles=Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"Assets"),"*.txt").Order().Select(path=>new{name=Path.GetFileName(path),sha256=ProjectFiles.HashFile(path)}).ToArray();
            var instructions=options.Benchmark.AiIds.Select(id=>new{id,stages=AiTasks.Instructions(id)}).ToArray();
            await WriteDocument(Path.Combine(runDir,"environment.json"),new { baseline, editorHash = ProjectFiles.HashFile(options.UnityExecutable),
                os = Environment.OSVersion.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                processorCount=Environment.ProcessorCount,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runnerHash=ProjectFiles.HashFile(typeof(DeskRuntime).Assembly.Location),inputFiles,instructions,
                conditionHash=ProjectFiles.HashText(JsonSerializer.Serialize(new{options,inputFiles,instructions},DeskJson.Options)),
                stopwatchFrequency = Stopwatch.Frequency, evidenceKind = ai.IsSynthetic && schedule.Any(x=>x.Experiment.Mode is ExecutionMode.AiCreation or ExecutionMode.AiWork) ? "SyntheticIncluded" : options.Benchmark.ConfirmedSpecification ? "Real" : "Pilot",
                schedule, note = "Separate processes/workspaces/sessions. OS cache, thermals and provider state are not isolated." },ct);
            await Status("실행 중");
            bool failed = false;
            foreach (var trial in schedule)
            {
                ct.ThrowIfCancellationRequested();
                Notice(plan,$"{trial.Order}/{schedule.Length} · {trial.Release.Label} · {trial.Experiment.Id}/{trial.Experiment.Variant} · 반복 {trial.Repeat}");
                void Report(string stage,int completed)=>ProgressChanged?.Invoke(new(plan.RunId,completed,schedule.Length,trial.Release.Label,trial.Experiment.Id,trial.Experiment.Variant,trial.Repeat,stage));
                var result = await ExecuteTrial(plan,options,trial,baseline,records,runDir,ct,stage=>Report(stage,trial.Order-1));
                if (result.Outcome != "Succeeded") failed = true;
                if (result.Failure == "CleanupIncomplete") throw new IOException("프로세스·경로 정리를 확인하지 못해 다음 시행을 중지했습니다.");
                if (baseline is not null && await ProjectFiles.FingerprintAsync(plan.Project.RootPath,ct) != baseline)
                    throw new InvalidDataException("기준 프로젝트가 변경되어 다음 시행을 중지했습니다.");
                Report("시행 기록 저장 완료",trial.Order);
            }
            await Status(failed ? "일부 실패" : "완료");
        }
        catch (OperationCanceledException) { await Status("중단됨"); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or TimeoutException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        { await Status("실패",EventJournal.Redact(error.Message)); Notice(plan,EventJournal.Redact(error.Message),false); }
    }
    private async Task<TrialResult> ExecuteTrial(RunPlan plan, RunOptions options, TrialSchedule trial, string? baseline,
        RunRecordStore records, string runDir, CancellationToken runStop,Action<string> report)
    {
        var route = new ExecutionRoute(plan.RunId,ExecutionId.New(),plan.Project.Id,plan.Tool,trial.Experiment.Mode,plan.Tool==ToolKind.Benchmark?trial.TrialId:null);
        string output = Path.Combine(runDir,"executions",route.ExecutionId.Value.ToString("N")); Directory.CreateDirectory(output);
        string managed = Path.Combine(DataRoot,"workspaces"); string workspaceRoot = Path.Combine(managed,trial.TrialId.Value.ToString("N"));
        string project = baseline is null ? plan.Project.RootPath : Path.Combine(workspaceRoot,"project");
        string owner = Guid.NewGuid().ToString("N");
        var state = new ExecutionState(route); StateChanged?.Invoke(state);
        await using var writer = await records.CreateExecutionAsync(plan,new(route,trial.Release.Id,project,output),state.Events[0]);
        long saved = 1;
        async Task SaveState()
        {
            foreach (var item in state.Events.Where(x=>x.Sequence>saved))
            { if ((await writer.AppendAsync(item)).Status != SaveStatus.Saved) throw new IOException("생명주기 기록 저장 실패"); saved = item.Sequence; }
            StateChanged?.Invoke(state);
        }
        using var journal = new EventJournal(Path.Combine(output,"events.jsonl"),route);
        void Observe(string kind,string text) { journal.Append(kind,text); if (kind is "validation" or "instruction" or "error") Notice(plan,EventJournal.Redact(text.Length>1500?text[..1500]:text)); }
        UnityEnvironment? unity = null; PackageChange? package = null; bool packageApplied = false;
        ExperimentMeasurement? measurement = null; string? failure = null; RunOutcome outcome = RunOutcome.Failed;
        double preparation = 0, recovery = 0; bool retained = baseline is not null; bool cleanup = true;
        var prep = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Benchmark.PrepareTimeoutSeconds));
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(runStop,timeout.Token);
        try
        {
            state.TryAdvance(ExecutionPhase.Preparing); await SaveState();
            if (baseline is not null)
            {
                report("새 프로젝트 복제본 준비 중");
                Directory.CreateDirectory(workspaceRoot); await File.WriteAllTextAsync(Path.Combine(workspaceRoot,".desk-owner"),owner,preparing.Token);
                await ProjectFiles.CloneAsync(plan.Project.RootPath,project,baseline,preparing.Token);
                Observe("baseline",baseline);
            }
            if (baseline is not null || options.InstallConnector)
            {
                report("비교 버전의 Bridge 적용 중");
                package = await new PackageProvisioner(DataRoot).PrepareAsync(project,trial.Release,Path.Combine(output,"package"),preparing.Token);
                Observe("package.change",JsonSerializer.Serialize(package));
                packageApplied = PackageProvisioner.Apply(package);
            }
            if (trial.Experiment.Mode == ExecutionMode.FixedCommands) await UnityEnvironment.InstallFixtureAsync(project,preparing.Token);
            if (trial.Experiment.Mode == ExecutionMode.AiCreation)
            {
                string directory = Path.Combine(project,"Assets","DeskValidation","Editor");
                if (Directory.Exists(Path.Combine(project,"Assets","DeskValidation"))) throw new InvalidDataException("기준 프로젝트에 이전 검증 파일이 있습니다.");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory,"DeskValidation.cs"),await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Assets","DeskValidation.cs.txt"),preparing.Token),preparing.Token);
            }
            var target = new BridgeTarget(project,options.UnityExecutable,trial.Release.CliPath!,trial.Release.CliSha256!,RequiredConnectorVersion:trial.Release.ObservedConnectorVersion);
            var discovery = new InstanceDiscovery(InstanceDiscovery.DefaultDirectory);
            var bridge = new BridgeAdapter(processes,discovery,InstanceDiscovery.DefaultDirectory);
            report("Unity 기동·컴파일·Bridge 연결 확인 중");
            unity = await UnityEnvironment.OpenAsync(processes,discovery,target,Path.Combine(output,"editor.log"),TimeSpan.FromSeconds(options.Benchmark.PrepareTimeoutSeconds),Observe,preparing.Token,baseline is not null);
            if (discovery.Find(unity.Target).UnityVersion != plan.Project.UnityBuild) throw new InvalidDataException("실제 Unity 버전 불일치");
            preparation = prep.Elapsed.TotalMilliseconds;
            state.TryAdvance(ExecutionPhase.Running); await SaveState();
            report(trial.Experiment.Mode==ExecutionMode.AiCreation?"AI 제작·독립 검증 중":"고정 작업 실행·응답 검증 중");
            using var executionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Benchmark.TimeoutSeconds));
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(runStop,executionTimeout.Token);
            if (trial.Experiment.Mode == ExecutionMode.FixedCommands)
                measurement = await new FixedExperiments(bridge).RunAsync(unity.Target,trial.Experiment,options.Benchmark,Observe,execution.Token);
            else if (trial.Experiment.Mode == ExecutionMode.AiCreation)
            {
                string validator = Path.Combine(project,"Assets","DeskValidation","Editor","DeskValidation.cs"); string hash = ProjectFiles.HashFile(validator);
                measurement = await new AiExperiments(bridge,ai).RunAsync(unity.Target,trial.Experiment,options.Benchmark,options.Ai!,Path.Combine(DataRoot,"private-ai"),Observe,execution.Token);
                if (ProjectFiles.HashFile(validator) != hash) throw new InvalidDataException("AI가 독립 검증 파일을 변경했습니다.");
            }
            else if (trial.Experiment.Mode == ExecutionMode.AiWork)
            {
                var before = await ProjectFiles.InventoryAsync(project,execution.Token);
                await using var session = await ai.CreateAsync(options.Ai!,unity.Target,Path.Combine(DataRoot,"private-ai"),execution.Token);
                var clock = Stopwatch.StartNew(); var result = await session.SendAsync(options.Instruction,Observe,execution.Token);
                if (!result.Completed) throw new IOException("AI 작업 실패: " + result.Failure);
                Observe("ai.report",result.Message);
                var after=await ProjectFiles.InventoryAsync(project,execution.Token);
                var changes=before.Keys.Union(after.Keys).Where(path=>before.GetValueOrDefault(path)!=after.GetValueOrDefault(path)).Select(path=>new{path,before=before.GetValueOrDefault(path),after=after.GetValueOrDefault(path)}).ToArray();
                Observe("files.changed",JsonSerializer.Serialize(changes));
                var check = await bridge.ExecAsync(unity.Target,"return new { playing = UnityEditor.EditorApplication.isPlaying, compiling = UnityEditor.EditorApplication.isCompiling, scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path };",TimeSpan.FromSeconds(options.Benchmark.TimeoutSeconds),cancellationToken:execution.Token);
                Observe("validation",check.Json.ToString());
                measurement = new(0,clock.Elapsed.TotalMilliseconds,0,[],true,1,result.InputTokens,result.OutputTokens,result.ToolCalls,clock.Elapsed.TotalMilliseconds);
                Observe("verification.scope","AI 실행·Bridge 응답 확인. 임의 자연어 요구의 기능 완성은 자동 판정하지 않습니다.");
            }
            else
            {
                var clock = Stopwatch.StartNew();
                var check = await bridge.ExecAsync(unity.Target,"return UnityEngine.Application.unityVersion;",TimeSpan.FromSeconds(options.Benchmark.TimeoutSeconds),frame=>Observe("cli."+frame.Kind,frame.Text),execution.Token);
                if (check.Json.GetString()!=plan.Project.UnityBuild) throw new InvalidDataException("설치 후 실제 응답 버전 불일치");
                Observe("validation","패키지 해시·Editor·Connector·실제 명령 응답 확인");
                measurement = new(0,clock.Elapsed.TotalMilliseconds,0,[]);
            }
            state.TryAdvance(ExecutionPhase.Validating); await SaveState(); outcome = RunOutcome.Succeeded;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or TimeoutException or JsonException or KeyNotFoundException or OperationCanceledException or UnauthorizedAccessException)
        {
            failure = error is BridgeException b ? b.Failure.ToString() : error.GetType().Name;
            Observe("error",EventJournal.Redact(error.Message));
            if (runStop.IsCancellationRequested) { await state.RequestStopAsync(StopReason.UserCancellation); outcome=RunOutcome.Cancelled; }
            else if (error is OperationCanceledException or TimeoutException || error is BridgeException { Failure: BridgeFailure.TimedOut })
            { await state.RequestStopAsync(StopReason.Timeout); outcome=RunOutcome.TimedOut; }
        }
        finally
        {
            report("Editor 종료·작업 정리 중");
            if (preparation == 0) preparation=prep.Elapsed.TotalMilliseconds;
            var cleaning = Stopwatch.StartNew();
            try { if (unity is not null) await unity.DisposeAsync(); }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException) { cleanup=false; Observe("cleanup.error",error.Message); }
            if (baseline is null && packageApplied && outcome != RunOutcome.Succeeded && package is not null)
            {
                try { if (!PackageProvisioner.Rollback(package)) { cleanup=false; Observe("cleanup.error","Bridge 의존성이 다른 작업으로 바뀌어 복원하지 않았습니다."); } }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { cleanup=false; Observe("cleanup.error",error.Message); }
            }
            if (baseline is not null && cleanup && !(outcome==RunOutcome.Succeeded?options.Benchmark.KeepSuccessfulClones:options.Benchmark.KeepFailedClones))
            {
                try { ProjectFiles.DeleteOwnedFolder(managed,workspaceRoot,owner); retained=false; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { cleanup=false; Observe("cleanup.error",error.Message); }
            }
            recovery=cleaning.Elapsed.TotalMilliseconds;
            if (runStop.IsCancellationRequested) await state.RequestStopAsync(StopReason.UserCancellation);
            if (!cleanup) failure="CleanupIncomplete";
            state.TryBeginFinalizing(outcome,new(outcome==RunOutcome.Succeeded?ValidationStatus.Passed:ValidationStatus.Failed,null));
            state.TryComplete(new(cleanup?CleanupStatus.Completed:CleanupStatus.RecoveryRequired)); await SaveState();
        }
        var resultRecord = new TrialResult(route,trial.Release.Label,trial.Experiment.Id,trial.Experiment.Variant,trial.Repeat,
            options.Benchmark.Specification,ai.IsSynthetic&&trial.Experiment.Mode is ExecutionMode.AiCreation or ExecutionMode.AiWork?"Synthetic":options.Benchmark.ConfirmedSpecification?"Real":"Pilot",state.Snapshot.Outcome.ToString()!,failure,
            preparation+(measurement?.PreparationMs??0),measurement?.WorkMs,measurement?.ValidationMs,recovery,measurement?.EndToEndMs,
            measurement?.FirstPass,measurement?.Attempts??0,measurement?.InputTokens,measurement?.OutputTokens,measurement?.ToolCalls,
            measurement is null?[]:[..measurement.Samples],project,retained) { ReleaseId=trial.Release.Id };
        await WriteDocument(Path.Combine(output,"result.json"),resultRecord,CancellationToken.None);
        return resultRecord;
    }
    public static async Task WriteDocument<T>(string path,T document,CancellationToken ct)
    {
        string temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            await using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            {await JsonSerializer.SerializeAsync(file,new VersionedDocument<T>(1,document),DeskJson.Options,ct);await file.FlushAsync(ct);file.Flush(true);}
            File.Move(temp,path,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
