using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityBridgeDesk.Infrastructure.SpeedBench;
using UnityBridgeDesk.Infrastructure.Catalog;

namespace UnityBridgeDesk.Desktop.Tests;

internal static class ResearchRenderChecks
{
    public static void Verify(SpeedBenchWindow window, FrameworkElement content, string directory)
    {
        object? Invoke(string name, params object[] args) => typeof(SpeedBenchWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var original=(SpeedOptions)Invoke("ReadOptions")!;
        var previousRun=(SpeedRun?)typeof(SpeedBenchWindow).GetField("shownRun",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
        var editorList=Control<ComboBox>("EditorList"); var editorItems=editorList.ItemsSource; var oldEditor=editorList.SelectedItem;
        var selected=Control<StackPanel>("ReleaseList").Children.OfType<CheckBox>().Where(c=>c.IsChecked==true).ToArray();
        bool official=Control<CheckBox>("IncludeOfficial").IsChecked==true, go=Control<CheckBox>("IncludeGo").IsChecked==true;
        var tabs=Control<TabControl>("Tabs"); int tab=tabs.SelectedIndex;
        try
        {
            tabs.SelectedIndex=0;
            var editor=new LocalCandidate(DiscoveryKind.Editor,@"C:\synthetic\Unity.exe","Synthetic Unity","test","6000.3.23f1");
            editorList.ItemsSource=new[]{editor};editorList.SelectedItem=editor;
            foreach(var box in Control<StackPanel>("ReleaseList").Children.OfType<CheckBox>()) box.IsChecked=false;
            selected.First().IsChecked=true;
            Control<CheckBox>("IncludeOfficial").IsChecked=false; Control<CheckBox>("IncludeGo").IsChecked=false;
            var options=original with {Repeats=20,Research=new("aa","동일 바이너리의 경로·순서 영향 확인. 허용 차이는 연구 질문에 맞춰 사전에 설정한다.",1,true,"연구 묶음 A","전원 구성 동일 · 별도 세션")};
            Invoke("ApplyOptions",options); Invoke("UpdatePreparation");
            Assert.IsTrue(Control<Button>("StartButton").IsEnabled,Control<TextBlock>("PlanHint").Text);
            Assert.Contains("2개 대상 (동일 릴리스 A/B)",Control<TextBlock>("PlanSummary").Text);
            Assert.AreEqual(options.Research,((SpeedOptions)Invoke("ReadOptions")!).Research);
            Control<Expander>("ResearchExpander").IsExpanded=true;
            foreach(var size in new[]{new Size(960,600),new Size(1440,850)})
            foreach(double scale in new[]{1d,1.5d})
            {
                content.Measure(size);content.Arrange(new Rect(new Point(),size));content.UpdateLayout();
                var scroll=Control<ScrollViewer>("PreparationScroll");
                var form=(FrameworkElement)scroll.Content;
                scroll.ScrollToVerticalOffset(Control<Expander>("ResearchExpander").TranslatePoint(new Point(),form).Y);
                content.UpdateLayout();
                var question=Control<TextBox>("ResearchQuestion");
                Assert.IsGreaterThan(120d,question.ActualWidth);
                Assert.IsLessThanOrEqualTo(size.Width,question.TranslatePoint(new Point(),content).X+question.ActualWidth);
                Assert.IsTrue(question.Focusable);
                var image=new RenderTargetBitmap((int)(size.Width*scale),(int)(size.Height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
                image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using var output=File.Create(Path.Combine(directory,$"research-{size.Width}-{scale*100}.png"));encoder.Save(output);
            }
            Invoke("ApplyOptions",options with {Repeats=21});Invoke("UpdatePreparation");
            Assert.IsFalse(Control<Button>("StartButton").IsEnabled);
            Assert.Contains("배수",Control<TextBlock>("PlanHint").Text);
            Invoke("ApplyOptions",options with {Research=new("exploratory")});Invoke("UpdatePreparation");
            Assert.AreEqual(Visibility.Collapsed,Control<StackPanel>("ResearchAaOptions").Visibility);
            Control<TextBox>("ResearchTolerance").Text="7";
            Assert.AreEqual(7d,((SpeedOptions)Invoke("ReadOptions")!).Research!.TolerancePercent);
            Control<TextBox>("ResearchTolerance").Text="invalid";
            Assert.AreEqual(1d,((SpeedOptions)Invoke("ReadOptions")!).Research!.TolerancePercent);
            Invoke("ApplyOptions",options with {Experiments=["F01"],Research=new("sensitivity","100ms 변화가 외부 시간에 반영되는지 확인")});Invoke("UpdatePreparation");
            Assert.IsTrue(Control<Button>("StartButton").IsEnabled,Control<TextBlock>("PlanHint").Text);
            Assert.AreEqual(Visibility.Collapsed,Control<StackPanel>("ResearchAaOptions").Visibility);
            Assert.Contains("100ms",Control<TextBlock>("ResearchPurposeHint").Text);
            Invoke("ApplyOptions",options with {Research=new("aa","",1)});Invoke("UpdatePreparation");
            Assert.IsFalse(Control<Button>("StartButton").IsEnabled);
            Assert.AreSame(Control<TextBox>("ResearchQuestion"),Invoke("ResearchInvalidInput"));
            Invoke("ApplyOptions",options);Control<TextBox>("ResearchTolerance").Text="0";Invoke("UpdatePreparation");
            Assert.AreSame(Control<TextBox>("ResearchTolerance"),Invoke("ResearchInvalidInput"));
            Control<CheckBox>("IncludeGo").IsChecked=true;
            Invoke("ApplyOptions",options);Invoke("UpdatePreparation");
            Assert.IsFalse(Control<Button>("StartButton").IsEnabled);
            Assert.Contains("A/A",Control<TextBlock>("PlanHint").Text);
            var result=UnityBridgeDesk.Tests.SpeedReportSample.Create();
            result=result with{Options=result.Options with{Research=new("pilot","합성 화면 검토",StudyGroup:"연구 묶음 A")}};
            result=result with{ResearchPlan=SpeedResearch.Freeze(result,"synthetic fixture","synthetic worker")};
            Invoke("ShowRun",result);tabs.SelectedIndex=2;Invoke("SelectResultView",0);
            Control<Expander>("ResearchResultsExpander").IsExpanded=true;
            content.Measure(new Size(960,600));content.Arrange(new Rect(0,0,960,600));content.UpdateLayout();
            var resultScroll=Control<ScrollViewer>("ResultDetailsScroll");
            resultScroll.ScrollToVerticalOffset(Control<Expander>("ResearchResultsExpander").TranslatePoint(new Point(),(FrameworkElement)resultScroll.Content).Y);
            content.UpdateLayout();
            Assert.Contains("미수행",Control<TextBlock>("ResearchResultCompletion").Text);
            Assert.Contains("계획",Control<TextBlock>("ResearchResultCompletion").Text);
            Assert.Contains("연구 묶음 A",Control<TextBlock>("ResearchResultSession").Text);
            var resultImage=new RenderTargetBitmap(960,600,96,96,PixelFormats.Pbgra32);resultImage.Render(content);
            var resultEncoder=new PngBitmapEncoder();resultEncoder.Frames.Add(BitmapFrame.Create(resultImage));
            using(var output=File.Create(Path.Combine(directory,"research-results-960.png")))resultEncoder.Save(output);
        }
        finally
        {
            Control<Expander>("ResearchExpander").IsExpanded=false;
            Control<Expander>("ResearchResultsExpander").IsExpanded=false;
            if(previousRun is not null)Invoke("ShowRun",previousRun);
            Invoke("ApplyOptions",original);
            foreach(var box in Control<StackPanel>("ReleaseList").Children.OfType<CheckBox>())box.IsChecked=selected.Contains(box);
            Control<CheckBox>("IncludeOfficial").IsChecked=official;Control<CheckBox>("IncludeGo").IsChecked=go;
            editorList.ItemsSource=editorItems;editorList.SelectedItem=oldEditor;
            Invoke("UpdatePreparation");Control<ScrollViewer>("PreparationScroll").ScrollToTop();tabs.SelectedIndex=tab;
        }
    }
}
