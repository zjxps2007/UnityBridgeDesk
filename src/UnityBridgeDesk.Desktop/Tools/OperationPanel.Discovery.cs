using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UnityBridgeDesk.Core.Models;
using UnityBridgeDesk.Infrastructure.Catalog;
using UnityBridgeDesk.Infrastructure.Storage;

namespace UnityBridgeDesk.Desktop.Tools;

public sealed partial class OperationPanel
{
    private readonly Dictionary<string, TextBlock> pathHints = [];
    private readonly Dictionary<string, ComboBox> pathChoices = [];
    private readonly List<Button> pathFinders = [];
    private string? automaticEditor;
    private ProjectId? setupProject;
    private ReleaseId? setupRelease;
    private bool resolvingPaths;
    private int setupGeneration;

    private void AddPathDiscovery(Panel parent, string key)
    {
        var actions = new WrapPanel();
        var find = new Button { Content = "자동 찾기", Margin = new(0,0,8,5), MinHeight=32 };
        AutomationProperties.SetName(find, key == "editor" ? "프로젝트 버전에 맞는 Editor 자동 찾기" : key + " 자동 찾기");
        find.Click += async (_, _) => await ResolveLocalSetupAsync(key);
        actions.Children.Add(find); pathFinders.Add(find);
        var choice = new ComboBox { MinWidth=180, MaxWidth=440, MinHeight=32, Margin=new(0,0,0,5), Visibility=Visibility.Collapsed, DisplayMemberPath=nameof(LocalCandidate.Label) };
        choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is LocalCandidate candidate) fields[key].Text=candidate.Path; };
        AutomationProperties.SetName(choice, key + " 찾은 위치 선택");
        actions.Children.Add(choice); pathChoices[key]=choice; parent.Children.Add(actions);
        var hint = Text("기본 위치와 이전 선택을 자동으로 확인합니다.",11);
        hint.SetResourceReference(ForegroundProperty,"Muted"); pathHints[key]=hint; parent.Children.Add(hint);
    }
    private async Task ResolveLocalSetupAsync(string? requestedKey=null)
    {
        if (disposed || resolvingPaths || operationBusy || operationReviewBusy || benchmarkBusy || benchmarkReviewBusy) return;
        resolvingPaths=true; int generation=setupGeneration;
        foreach(var button in pathFinders)button.IsEnabled=false;
        var before=fields.Where(x=>pathHints.ContainsKey(x.Key)).ToDictionary(x=>x.Key,x=>x.Value.Text);
        var project=catalog.Document.Projects.FirstOrDefault(x=>x.Project.Id==catalog.Document.SelectedProject)?.Project;
        setupProject=project?.Id;
        try
        {
            var setup=new LocalSetupStore(runtime.DataRoot);var memory=await setup.LoadAsync();
            var remembered=memory.Paths.Values.ToList();
            // Reuse locations already configured in another tool, including installations saved before discovery existed.
            foreach(var kind in new[]{ToolKind.Installation,ToolKind.AiWork,ToolKind.Benchmark})
            {
                var prefs=(await new AtomicJsonStore<RunnerPreferences>(Path.Combine(runtime.DataRoot,"settings","runner-"+kind+".json"),x=>x.Validate()).LoadAsync()).Value;
                if(prefs is not null)remembered.AddRange(new[]{prefs.Editor,prefs.Codex,prefs.AuthenticationHome});
                var draft=(await new AtomicJsonStore<RunnerDraft>(Path.Combine(runtime.DataRoot,"settings","draft-runner-"+kind+".json"),x=>x.Validate()).LoadAsync()).Value;
                if(draft is not null)remembered.AddRange(draft.Fields.Where(x=>x.Key is "editor" or "codex" or "auth").Select(x=>x.Value));
            }
            remembered.AddRange(before.Values);
            var result=await localDiscovery.ScanAsync(catalog.Document,memory.Folders,remembered);
            string? required=project is null?null:LocalDiscovery.ProjectVersion(project.RootPath);
            if(disposed||generation!=setupGeneration||operationBusy||benchmarkBusy||operationReviewBusy||benchmarkReviewBusy)return;
            foreach(var key in pathHints.Keys)
            {
                if(fields[key].Text!=before[key])continue; // A late scan cannot overwrite typing or a file picker choice.
                var kind=key switch{"editor"=>DiscoveryKind.Editor,"codex"=>DiscoveryKind.AiExecutable,_=>DiscoveryKind.AuthenticationHome};
                var candidates=result.Candidates.Where(x=>x.Kind==kind && (key!="editor"||required is null||x.Version==required)).ToArray();
                string current=Value(key);
                bool exists=key=="auth"?File.Exists(Path.Combine(current,"auth.json")):File.Exists(current);
                LocalCandidate? selected=key=="editor"?LocalDiscovery.MatchEditor(candidates,required):candidates.FirstOrDefault();
                if(key!="editor" && candidates.Length>1)
                {
                    selected=memory.Paths.TryGetValue(key,out var saved)?candidates.FirstOrDefault(x=>string.Equals(x.Path,saved,StringComparison.OrdinalIgnoreCase)):null;
                }
                if((!exists||requestedKey==key||key=="editor"&&current==automaticEditor)&&selected is not null)
                {fields[key].Text=selected.Path;if(key=="editor")automaticEditor=selected.Path;current=selected.Path;exists=true;}
                else if(key=="editor"&&current==automaticEditor&&selected is null){fields[key].Clear();automaticEditor=null;current="";exists=false;}
                pathChoices[key].ItemsSource=candidates;
                pathChoices[key].Visibility=candidates.Length>1||key=="editor"&&required is null&&candidates.Length>0?Visibility.Visible:Visibility.Collapsed;
                string? observed=key=="editor"?LocalDiscovery.EditorVersion(current):null;
                pathHints[key].Text=exists?(key=="editor"&&required is not null&&observed!=required?
                    $"프로젝트는 {required}입니다. 현재 지정한 Editor의 버전을 확인하세요." :
                    key=="editor"?$"{observed??"버전 미확인"} · 위치 확인됨 · 다른 위치는 ‘찾기’로 변경":
                    key=="auth"?"로그인 파일 위치 확인됨 · 로그인 유효성은 AI 실행 시 확인합니다.":"실행 파일 위치 확인됨 · 다른 위치는 ‘찾기’로 변경"):
                    candidates.Length>1?"찾은 위치가 여러 개입니다. 위 목록에서 한 번 선택하세요.":
                    key=="editor"&&required is not null?$"{required} Editor를 찾지 못했어요. 설치된 Unity.exe를 ‘찾기’로 지정하세요.":
                    key=="editor"&&project is null?"먼저 보관함에서 프로젝트를 선택하세요.":
                    "기본 위치에서 찾지 못했어요. ‘찾기’로 한 번 지정하면 다른 도구에서도 재사용합니다.";
            }
            if(before.Any(x=>fields[x.Key].Text!=x.Value)){inputDirty=true;if(!loading)QueueInputSave();}
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {if(!disposed)foreach(var hint in pathHints.Values)hint.Text="자동 확인을 마치지 못했어요. ‘찾기’로 직접 지정할 수 있습니다.";}
        finally
        {
            resolvingPaths=false;
            if(!disposed)
            {
                foreach(var button in pathFinders)button.IsEnabled=true;
                if(generation!=setupGeneration)await ResolveLocalSetupAsync();
            }
        }
    }
    private async void SetupCatalogChanged()
    {
        if(!Dispatcher.CheckAccess()){_ = Dispatcher.BeginInvoke(SetupCatalogChanged);return;}
        if(disposed||!initialized)return;
        if(tool!=ToolKind.Benchmark&&!operationBusy)
        {
            var selected=releases.SelectedItems.Cast<ReleaseRow>().Select(x=>x.Id).ToHashSet();
            bool useShared=setupRelease!=catalog.Document.SelectedRelease;
            bool prior=loading;loading=true;releases.Items.Clear();
            foreach(var release in catalog.Document.Releases)
            {
                var row=new ReleaseRow(release.Id,release.Label);releases.Items.Add(row);
                if(useShared?row.Id==catalog.Document.SelectedRelease:selected.Contains(row.Id))releases.SelectedItems.Add(row);
            }
            setupRelease=catalog.Document.SelectedRelease;loading=prior;Invalidate();
        }
        if(setupProject==catalog.Document.SelectedProject)return;
        setupGeneration++;
        if(!operationBusy&&!benchmarkBusy){Invalidate();await ResolveLocalSetupAsync();}
    }
    private async Task RememberInputPathsAsync()
    {
        var memory=new LocalSetupStore(runtime.DataRoot);
        foreach(string key in new[]{"editor","codex","auth"})
        {
            string path=Value(key);
            if(!Path.IsPathFullyQualified(path))continue;
            if(key=="auth"?!File.Exists(Path.Combine(path,"auth.json")):!File.Exists(path))continue;
            string storageKey=key=="editor"?"editor:"+(LocalDiscovery.EditorVersion(path)??"manual"):key;
            if(!await memory.RememberAsync(key:storageKey,path:path))
                pathHints[key].Text="현재 입력은 저장했지만 공통 위치를 저장하지 못했어요.";
        }
    }
    private void PathEdited(string key)
    {
        if(loading||resolvingPaths||!pathHints.TryGetValue(key,out var hint))return;
        if(key=="editor")automaticEditor=null;
        string path=Value(key);
        bool exists=key=="auth"?File.Exists(Path.Combine(path,"auth.json")):File.Exists(path);
        hint.Text=exists?"직접 지정한 위치 · 저장하면 다른 도구에서도 재사용합니다.":"위치를 확인해 주세요. ‘자동 찾기’ 또는 ‘찾기’로 지정할 수 있어요.";
    }
}
