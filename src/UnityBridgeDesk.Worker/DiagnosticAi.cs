using System.Diagnostics;
using UnityBridgeDesk.Core.Execution;
using UnityBridgeDesk.Infrastructure.Ai;
using UnityBridgeDesk.Infrastructure.Bridge;

// Explicit test provider; not selectable from the production UI and never presented as model output.
internal sealed class DiagnosticAi(IProcessRunner processes) : IAiRunner
{
    public bool IsSynthetic=>true;
    public Task<IAiSession> CreateAsync(AiOptions options,BridgeTarget target,string privateRoot,CancellationToken ct)=>
        Task.FromResult<IAiSession>(new Session(new BridgeAdapter(processes,new InstanceDiscovery(InstanceDiscovery.DefaultDirectory),InstanceDiscovery.DefaultDirectory),target));
    private sealed class Session(BridgeAdapter bridge,BridgeTarget target):IAiSession
    {
        private readonly string id=Guid.NewGuid().ToString();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public async Task<AiResult> SendAsync(string instruction,Action<string,string> observe,CancellationToken ct)
        {
            observe("instruction",instruction);observe("synthetic.provider","Diagnostic fixture; no generative AI call or token usage.");
            var clock=Stopwatch.StartNew();string code;
            if(instruction.Contains("100개 인스턴스",StringComparison.Ordinal))code=Prefab;
            else if(instruction.StartsWith("Item_000부터",StringComparison.Ordinal))code=Overrides;
            else if(instruction.StartsWith("기본 프리팹",StringComparison.Ordinal))code=Common;
            else if(instruction.Contains("큐브는 Cube_00",StringComparison.Ordinal))
            {
                string dir=Path.Combine(target.ProjectPath,"Assets","DiagnosticFixture");Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir,"DeskMachineFixture.cs"),MachineScript,ct);
                await bridge.CallAsync(target,["refresh","--compile","request","--wait"],TimeSpan.FromSeconds(120),cancellationToken:ct,allowBusy:true);
                code=MachineScene;
            }
            else if(instruction.Contains("Pause·Resume",StringComparison.Ordinal))code=Buttons+"AddButton(\"Pause\", 0); AddButton(\"Resume\", 60); UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),\"Assets/DeskTask.unity\");return true;";
            else code=Drop;
            await bridge.ExecAsync(target,code,TimeSpan.FromSeconds(120),cancellationToken:ct);
            return new(true,id,"Synthetic fixture submission",null,null,null,1,null,new(ProcessOutcome.Exited,0,"","",clock.Elapsed.TotalMilliseconds,null,null));
        }
    }
    private const string Drop="""
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        var floor=UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Plane);floor.name="Floor";floor.transform.localScale=new UnityEngine.Vector3(3,1,3);
        var cube=UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube);cube.name="Cube";cube.transform.position=new UnityEngine.Vector3(0,3,0);cube.AddComponent<UnityEngine.Rigidbody>();
        var camera=new UnityEngine.GameObject("Camera");camera.AddComponent<UnityEngine.Camera>();camera.transform.position=new UnityEngine.Vector3(0,4,-8);camera.transform.LookAt(cube.transform);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/DeskTask.unity");return true;
        """;
    private const string Prefab="""
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        System.IO.Directory.CreateDirectory("Assets/DiagnosticFixture");UnityEditor.AssetDatabase.Refresh();
        var white=new UnityEngine.Material(UnityEngine.Shader.Find("Universal Render Pipeline/Lit"));white.color=UnityEngine.Color.white;UnityEditor.AssetDatabase.CreateAsset(white,"Assets/DiagnosticFixture/White.mat");
        var source=UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube);source.GetComponent<UnityEngine.Renderer>().sharedMaterial=white;
        var prefab=UnityEditor.PrefabUtility.SaveAsPrefabAsset(source,"Assets/DiagnosticFixture/Item.prefab");UnityEngine.Object.DestroyImmediate(source);
        for(int i=0;i<100;i++){var go=(UnityEngine.GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);go.name="Item_"+i.ToString("D3");go.transform.position=new UnityEngine.Vector3((i%10)*2,0,(i/10)*2);UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);}
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/DeskTask.unity");return true;
        """;
    private const string Overrides="""
        var red=new UnityEngine.Material(UnityEngine.Shader.Find("Universal Render Pipeline/Lit"));red.color=UnityEngine.Color.red;UnityEditor.AssetDatabase.CreateAsset(red,"Assets/DiagnosticFixture/Red.mat");
        for(int i=0;i<20;i++){var go=UnityEngine.GameObject.Find("Item_"+i.ToString("D3"));var p=go.transform.position;p.y=1;go.transform.position=p;go.GetComponent<UnityEngine.Renderer>().sharedMaterial=red;UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(go.GetComponent<UnityEngine.Renderer>());}
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/DeskTask.unity");return true;
        """;
    private const string Common="""
        var prefab=UnityEditor.PrefabUtility.LoadPrefabContents("Assets/DiagnosticFixture/Item.prefab");prefab.GetComponent<UnityEngine.BoxCollider>().size=new UnityEngine.Vector3(2,1,1);
        UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefab,"Assets/DiagnosticFixture/Item.prefab");UnityEditor.PrefabUtility.UnloadPrefabContents(prefab);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/DeskTask.unity");return true;
        """;
    private const string Buttons="""
        System.Action<string,float> AddButton=(name,y)=>{var go=new UnityEngine.GameObject(name,typeof(UnityEngine.RectTransform),typeof(UnityEngine.UI.Image),typeof(UnityEngine.UI.Button));go.transform.SetParent(UnityEngine.GameObject.Find("Canvas").transform,false);((UnityEngine.RectTransform)go.transform).anchoredPosition=new UnityEngine.Vector2(0,y);};
        """;
    private const string MachineScene="""
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        var floor=UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube);floor.name="Floor";floor.transform.position=new UnityEngine.Vector3(3,-.5f,3);floor.transform.localScale=new UnityEngine.Vector3(20,1,20);
        for(int i=0;i<16;i++){var go=UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube);go.name="Cube_"+i.ToString("D2");go.transform.position=new UnityEngine.Vector3((i%4)*2,6,(i/4)*2);go.AddComponent<UnityEngine.Rigidbody>().isKinematic=true;}
        var canvas=new UnityEngine.GameObject("Canvas",typeof(UnityEngine.RectTransform),typeof(UnityEngine.Canvas),typeof(UnityEngine.UI.GraphicRaycaster));canvas.GetComponent<UnityEngine.Canvas>().renderMode=UnityEngine.RenderMode.ScreenSpaceOverlay;
        var count=new UnityEngine.GameObject("Count",typeof(UnityEngine.RectTransform),typeof(UnityEngine.UI.Text));count.transform.SetParent(canvas.transform,false);count.GetComponent<UnityEngine.UI.Text>().text="0";count.GetComponent<UnityEngine.UI.Text>().font=UnityEngine.Resources.GetBuiltinResource<UnityEngine.Font>("LegacyRuntime.ttf");
        var events=new UnityEngine.GameObject("EventSystem",typeof(UnityEngine.EventSystems.EventSystem));var module=System.AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule")).FirstOrDefault(t=>t!=null);if(module!=null)events.AddComponent(module);
        var controller=new UnityEngine.GameObject("Controller");controller.AddComponent<DeskMachineFixture>();
        """+Buttons+"AddButton(\"Start\",120);AddButton(\"Reset\",180);UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),\"Assets/DeskTask.unity\");return true;";
    private const string MachineScript="""
        using System.Collections.Generic;
        using UnityEngine;
        using UnityEngine.UI;
        public class DeskMachineFixture:MonoBehaviour
        {
            Rigidbody[] bodies;HashSet<int> landed=new HashSet<int>();bool running,paused;
            void Start(){bodies=new Rigidbody[16];for(int i=0;i<16;i++)bodies[i]=GameObject.Find("Cube_"+i.ToString("D2")).GetComponent<Rigidbody>();Bind("Start",Run);Bind("Reset",Reset);Bind("Pause",Pause);Bind("Resume",Resume);Reset();}
            void Bind(string name,UnityEngine.Events.UnityAction action){var go=GameObject.Find(name);if(go!=null)go.GetComponent<Button>().onClick.AddListener(action);}
            void Run(){if(running)return;running=true;paused=false;Time.timeScale=1;foreach(var b in bodies)b.isKinematic=false;}
            void Pause(){if(!running)return;paused=true;Time.timeScale=0;}
            void Resume(){if(!running||!paused)return;paused=false;Time.timeScale=1;}
            void Reset(){running=false;paused=false;Time.timeScale=1;landed.Clear();for(int i=0;i<16;i++){var b=bodies[i];b.isKinematic=false;b.linearVelocity=Vector3.zero;b.angularVelocity=Vector3.zero;b.position=new Vector3((i%4)*2,6,(i/4)*2);b.isKinematic=true;}GameObject.Find("Count").GetComponent<Text>().text="0";}
            void FixedUpdate(){if(!running||paused)return;for(int i=0;i<16;i++)if(bodies[i].GetComponent<Collider>().bounds.min.y<.15f)landed.Add(i);GameObject.Find("Count").GetComponent<Text>().text=landed.Count.ToString();}
        }
        """;
}
