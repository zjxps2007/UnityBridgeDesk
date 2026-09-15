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
    private readonly Dictionary<string, TextBlock> pathSummaries = [];
    private readonly Dictionary<string, Expander> pathDetails = [];
    private readonly List<Button> pathFinders = [];
    private string? automaticEditor;
    private ProjectId? setupProject;
    private ReleaseId? setupRelease;
    private bool resolvingPaths;
    private int setupGeneration;

    private void AddPathDiscovery(Panel parent, string key, Grid manualInput)
    {
        string name=key switch { "editor"=>"Unity Editor", "codex"=>"AI 프로그램 · Codex", _=>"Codex 로그인 정보" };
        var content=new StackPanel();
        var card=new Border{Child=content,Padding=new(14,10,14,10),Margin=new(0,6,0,8),CornerRadius=new(10),BorderThickness=new(1)};
        card.SetResourceReference(Border.BackgroundProperty,"Tint");card.SetResourceReference(Border.BorderBrushProperty,"Line");
        AutomationProperties.SetAutomationId(card,"Connection-"+key);parent.Children.Add(card);
        var heading=Text(name,13);heading.FontWeight=FontWeights.SemiBold;heading.Margin=new(0,0,0,5);content.Children.Add(heading);
        var summary=Text("설치 위치를 확인하고 있어요…",14);summary.Margin=new(0,0,0,4);
        AutomationProperties.SetAutomationId(summary,"ConnectionStatus-"+key);pathSummaries[key]=summary;content.Children.Add(summary);
        var hint=Text("기본 위치와 이전 선택을 자동으로 확인합니다.",11);
        hint.Margin=new(0,0,0,7);hint.SetResourceReference(ForegroundProperty,"Muted");pathHints[key]=hint;content.Children.Add(hint);
        var actions = new WrapPanel();
        var find = new Button { Content = "다시 찾기", Margin = new(0,0,8,5), MinHeight=30, Padding=new(10,4,10,4) };
        AutomationProperties.SetName(find, name+" 자동 찾기");AutomationProperties.SetAutomationId(find,"FindPath-"+key);
        find.Click += async (_, _) => await ResolveLocalSetupAsync();
        actions.Children.Add(find);pathFinders.Add(find);content.Children.Add(actions);
        var detailContent=new StackPanel();
        var choice = new ComboBox { MinWidth=180, MaxWidth=440, HorizontalAlignment=HorizontalAlignment.Stretch, MinHeight=34, Margin=new(0,6,0,8), Visibility=Visibility.Collapsed, DisplayMemberPath=nameof(PathChoice.Label) };
        var choiceStyle=new Style(typeof(ComboBoxItem));
        choiceStyle.Setters.Add(new Setter(ToolTipProperty,new System.Windows.Data.Binding("Candidate.Path")));
        choice.ItemContainerStyle=choiceStyle;
        choice.SelectionChanged += (_, _) => { if (!resolvingPaths && choice.SelectedItem is PathChoice option) { fields[key].Text=option.Candidate.Path; RefreshPathState(key); } };
        AutomationProperties.SetName(choice, name + " 찾은 위치 선택");
        AutomationProperties.SetAutomationId(choice,"PathChoices-"+key);
        detailContent.Children.Add(choice);pathChoices[key]=choice;
        detailContent.Children.Add(Text(key=="auth"?"auth.json 파일이 들어 있는 폴더":"실행 파일의 전체 경로",11));
        detailContent.Children.Add(manualInput);
        var details=new Expander{Header="위치 확인 · 직접 지정",Content=detailContent,Margin=new(0,3,0,0)};
        AutomationProperties.SetAutomationId(details,"PathDetails-"+key);pathDetails[key]=details;content.Children.Add(details);
        var help=new StackPanel();
        help.Children.Add(Text(key switch {
            "editor"=>"보관함에서 프로젝트를 선택하면 같은 버전의 Editor를 연결합니다. 찾지 못하면 Unity Hub에서 해당 버전을 설치한 뒤 ‘다시 찾기’를 누르세요. 다른 위치에 설치했다면 ‘직접 지정’에서 Editor 폴더의 Unity.exe를 고릅니다. Unity Hub 실행 파일은 사용할 수 없습니다.",
            "codex"=>"Codex 데스크톱 설치 위치, 시스템 PATH와 기본 설치 폴더에서 실행 파일을 찾습니다. 다른 위치에 설치했다면 ‘직접 지정’에서 codex.exe를 한 번 선택하세요. 찾은 위치는 다른 도구에서도 재사용합니다.",
            _=>"이 앱은 파일로 저장된 로그인 정보를 사용합니다. Codex에 로그인되어 있어도 운영체제의 자격 증명 저장소를 사용하면 파일이 없을 수 있습니다. 파일 저장 방식으로 로그인한 후 ‘다시 찾기’를 누르세요. 별도 홈을 쓴다면 auth.json이 들어 있는 폴더를 ‘직접 지정’에서 선택하세요. 파일 내용을 붙여넣을 필요는 없습니다."
        },11));
        if(key is "codex" or "auth")
        {
            var official=new Button{Content="Codex 공식 로그인 안내 ↗",HorizontalAlignment=HorizontalAlignment.Left,Padding=new(10,4,10,4)};
            official.Click+=(_,_)=>{
                try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://learn.chatgpt.com/docs/auth"){UseShellExecute=true});}
                catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){pathHints[key].Text="안내 페이지를 열지 못했어요. https://learn.chatgpt.com/docs/auth 에서 확인하세요.";}
            };
            help.Children.Add(official);
        }
        content.Children.Add(new Expander{Header="설정 방법",Content=help,Margin=new(0,7,0,0)});
    }
    private sealed record PathChoice(LocalCandidate Candidate)
    {
        public string Label => $"{Candidate.Label} · {Candidate.Source} · {Path.GetFileName(Path.GetDirectoryName(Candidate.Path))}";
    }
    private bool PathExists(string key) => key=="auth"?File.Exists(Path.Combine(Value(key),"auth.json")):File.Exists(Value(key));
    private void RefreshPathState(string key, string? source=null)
    {
        string current=Value(key);bool exists=PathExists(key);
        var project=catalog.Document.Projects.FirstOrDefault(x=>x.Project.Id==catalog.Document.SelectedProject)?.Project;
        string? required=project is null?null:LocalDiscovery.ProjectVersion(project.RootPath);
        string? observed=key=="editor"?LocalDiscovery.EditorVersion(current):null;
        int count=pathChoices[key].Items.Count;
        string status, hint;
        bool ready=exists;
        if(exists)
        {
            status=key=="editor"?$"Unity {observed??"버전 확인 필요"}":key=="codex"?"Codex 실행 파일 확인됨":"로그인 파일 위치 확인됨";
            hint=key=="auth"?"로그인 유효성은 AI 실행 시 확인합니다.":"이 위치를 사용합니다. 변경할 때만 ‘직접 지정’을 여세요.";
            if(key=="editor")
            {
                if(project is null){hint="보관함에서 프로젝트를 선택하면 버전 일치 여부를 확인합니다.";ready=false;}
                else if(required is null){hint="프로젝트의 Editor 버전을 읽지 못했어요. ProjectVersion.txt를 확인하세요.";ready=false;}
                else if(required!=observed){hint=$"프로젝트에 필요한 버전은 {required}입니다. 같은 버전을 설치하거나 선택하세요.";ready=false;}
                else hint=$"프로젝트 버전 {required}과 일치합니다. 이대로 진행하세요.";
            }
            if(source is not null)hint=source+" · "+hint;
        }
        else if(key=="editor" && project is null){status="프로젝트 선택이 먼저예요";hint="보관함에서 프로젝트를 선택하면 알맞은 Editor를 자동으로 연결합니다.";}
        else if(count>1){status=$"{count}개 위치 중 선택이 필요해요";hint="‘위치 확인 · 직접 지정’에서 사용할 위치를 한 번 선택하세요.";}
        else
        {
            status=key=="editor"?"맞는 Editor를 찾지 못했어요":key=="codex"?"Codex 실행 파일이 필요해요":"로그인 파일을 찾지 못했어요";
            hint=key=="editor"?(required is null?"프로젝트의 Editor 버전을 확인하고 ‘직접 지정’에서 선택하세요.":$"Unity Hub에서 {required} 설치 후 ‘다시 찾기’를 누르세요."):
                key=="codex"?"설치 후 ‘다시 찾기’를 누르거나 ‘직접 지정’에서 codex.exe를 선택하세요.":"‘설정 방법’에서 로그인 저장 방식을 확인하세요. 고정 명령 벤치에는 필요하지 않습니다.";
        }
        pathSummaries[key].Text=status;pathSummaries[key].SetResourceReference(ForegroundProperty,ready?"AccentInk":"Ink");
        pathSummaries[key].ToolTip=string.IsNullOrWhiteSpace(current)?null:current;
        pathHints[key].Text=hint;
    }
    private async Task ResolveLocalSetupAsync()
    {
        if (disposed || resolvingPaths || operationBusy || operationReviewBusy || benchmarkBusy || benchmarkReviewBusy) return;
        resolvingPaths=true; int generation=setupGeneration;
        foreach(var button in pathFinders){button.IsEnabled=false;button.Content="찾는 중…";}
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
            var result=await localDiscovery.ScanAsync(catalog.Document,memory.Folders,remembered,configuredPaths:memory.Paths);
            string? required=project is null?null:LocalDiscovery.ProjectVersion(project.RootPath);
            if(disposed||generation!=setupGeneration||operationBusy||benchmarkBusy||operationReviewBusy||benchmarkReviewBusy)return;
            foreach(var key in pathHints.Keys)
            {
                if(fields[key].Text!=before[key])continue; // A late scan cannot overwrite typing or a file picker choice.
                var kind=key switch{"editor"=>DiscoveryKind.Editor,"codex"=>DiscoveryKind.AiExecutable,_=>DiscoveryKind.AuthenticationHome};
                var candidates=result.Candidates.Where(x=>x.Kind==kind && (key!="editor"||required is null||x.Version==required)).ToArray();
                string current=Value(key);
                bool exists=PathExists(key);
                LocalCandidate? selected=key=="editor"?LocalDiscovery.MatchEditor(candidates,required):candidates.FirstOrDefault();
                if(key!="editor" && candidates.Length>1)
                {
                    selected=memory.Paths.TryGetValue(key,out var saved)?candidates.FirstOrDefault(x=>string.Equals(x.Path,saved,StringComparison.OrdinalIgnoreCase)):null;
                }
                // Refresh preserves a valid manual choice; a mismatched automatic Editor follows the project.
                if((!exists||key=="editor"&&current==automaticEditor)&&selected is not null)
                {fields[key].Text=selected.Path;if(key=="editor")automaticEditor=selected.Path;current=selected.Path;exists=true;}
                else if(key=="editor"&&current==automaticEditor&&selected is null){fields[key].Clear();automaticEditor=null;current="";exists=false;}
                var choices=candidates.Select(x=>new PathChoice(x)).ToArray();
                pathChoices[key].ItemsSource=choices;
                pathChoices[key].SelectedItem=choices.FirstOrDefault(x=>string.Equals(x.Candidate.Path,current,StringComparison.OrdinalIgnoreCase));
                pathChoices[key].Visibility=candidates.Length>1||candidates.Length>0&&pathChoices[key].SelectedItem is null?Visibility.Visible:Visibility.Collapsed;
                if(!exists && candidates.Length>1)pathDetails[key].IsExpanded=true;
                RefreshPathState(key,candidates.FirstOrDefault(x=>string.Equals(x.Path,current,StringComparison.OrdinalIgnoreCase))?.Source);
                if((result.Limited||result.UnreadableLocations>0)&&!exists)pathHints[key].Text+=" 일부 위치는 검색하지 못했어요. 설치되어 있다면 직접 지정할 수 있습니다.";
            }
            if(before.Any(x=>fields[x.Key].Text!=x.Value)){inputDirty=true;if(!loading)QueueInputSave();}
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {if(!disposed)foreach(var key in pathHints.Keys){RefreshPathState(key);pathHints[key].Text="자동 확인을 마치지 못했어요. ‘직접 지정’에서 위치를 선택할 수 있습니다.";}}
        finally
        {
            resolvingPaths=false;
            if(!disposed)
            {
                foreach(var button in pathFinders){button.IsEnabled=true;button.Content="다시 찾기";}
                if(generation!=setupGeneration)await ResolveLocalSetupAsync();
                if(tool==ToolKind.Benchmark)UpdateBenchmarkFooter();
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
            }
            SetReleaseSelection(useShared?(catalog.Document.SelectedRelease is { } id?[id]:[]):selected);
            setupRelease=catalog.Document.SelectedRelease;loading=prior;Invalidate();
        }
        if(setupProject==catalog.Document.SelectedProject)return;
        setupGeneration++;
        if(!operationBusy&&!benchmarkBusy&&!bridgeSetupBusy){Invalidate();await ResolveLocalSetupAsync();}
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
        if(loading||!pathHints.ContainsKey(key))return;
        if(key=="editor"&&!resolvingPaths)automaticEditor=null;
        RefreshPathState(key);
    }
}
