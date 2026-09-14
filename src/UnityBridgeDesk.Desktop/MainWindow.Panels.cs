using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UnityBridgeDesk.Core.Models;


namespace UnityBridgeDesk.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer revealTimer=new(){Interval=TimeSpan.FromMilliseconds(130)};
    private readonly DispatcherTimer dismissTimer=new(){Interval=TimeSpan.FromMilliseconds(400)};
    private readonly TranslateTransform selectorOffset=new();

    private bool selectorOpen;
    private int selectorTransition;

    private IInputElement? focusBeforeSelector;

    private void InitializePanels()
    {
        SelectorDropdown.RenderTransform=selectorOffset;

        revealTimer.Tick+=(_,_)=>{revealTimer.Stop();if(SelectorTrigger.IsMouseOver&&Overlay.Visibility!=Visibility.Visible)ShowSelector();};
        dismissTimer.Tick+=(_,_)=>
        {
            dismissTimer.Stop();
            if(!SelectorDropdown.IsKeyboardFocusWithin&&!SelectorDropdown.IsMouseOver&&!SelectorTrigger.IsMouseOver)HideSelector();
        };
        Deactivated+=(_,_)=>HideSelector();
        Closed+=(_,_)=>{revealTimer.Stop();dismissTimer.Stop();};

    }
    private void SelectorTriggerEntered(object sender,MouseEventArgs e)
    {
        dismissTimer.Stop();
        if(Overlay.Visibility==Visibility.Visible)return;
        if(SelectorDropdown.Visibility==Visibility.Visible){ShowSelector();return;}
        revealTimer.Stop();revealTimer.Start();
    }
    private void SelectorPointerEntered(object sender,MouseEventArgs e){dismissTimer.Stop();ShowSelector();}
    private void SelectorPointerLeft(object sender,MouseEventArgs e)=>ScheduleSelectorDismiss();
    private void SelectorFocusLeft(object sender,KeyboardFocusChangedEventArgs e)=>ScheduleSelectorDismiss();
    private void ScheduleSelectorDismiss()
    {
        revealTimer.Stop();
        if(!selectorOpen)return;
        dismissTimer.Stop();dismissTimer.Start();
    }
    private void SelectorClick(object sender,RoutedEventArgs e)
    {
        ShowSelector();(ToolTabs.SelectedItem as TabItem)?.Focus();
    }
    private void SelectorKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Enter or Key.Space)||ToolTabs.SelectedItem is not TabItem tab)return;
        OpenTool(Enum.Parse<ToolKind>((string)tab.Tag));e.Handled=true;
    }
    private void ShowSelector()
    {
        revealTimer.Stop();dismissTimer.Stop();
        if(selectorOpen)return;
        bool fromHidden=SelectorDropdown.Visibility!=Visibility.Visible;
        if(fromHidden)focusBeforeSelector=Keyboard.FocusedElement;
        selectorOpen=true;selectorTransition++;
        SelectorDropdown.Visibility=Visibility.Visible;SelectorDropdown.IsHitTestVisible=true;
        AnimateSelector(true,fromHidden);
    }
    private void WorkspacePointerPressed(object sender,MouseButtonEventArgs e)
    {
        if(!selectorOpen)return;
        for(var node=e.OriginalSource as DependencyObject;node is not null;node=(node is Visual?VisualTreeHelper.GetParent(node):null)??LogicalTreeHelper.GetParent(node))
            if(node==SelectorDropdown||node==SelectorTrigger)return;
        HideSelector(false);
    }
    private void HideSelector(bool restoreFocus=true)
    {
        revealTimer.Stop();dismissTimer.Stop();
        if(!selectorOpen)return;
        selectorOpen=false;selectorTransition++;
        if(restoreFocus&&SelectorDropdown.IsKeyboardFocusWithin)
        {
            if(focusBeforeSelector is UIElement {IsVisible:true,IsEnabled:true} prior)prior.Focus();
            else SelectorTrigger.Focus();
        }
        AnimateSelector(false);
    }
    private void AnimateSelector(bool show,bool fromHidden=false)
    {
        int transition=selectorTransition;
        double targetY=show?0:-20,targetOpacity=show?1:0;
        void Finish()
        {
            if(transition!=selectorTransition)return;
            SelectorDropdown.BeginAnimation(OpacityProperty,null);SelectorDropdown.Opacity=targetOpacity;
            selectorOffset.BeginAnimation(TranslateTransform.YProperty,null);selectorOffset.Y=targetY;
            if(!show)SelectorDropdown.Visibility=Visibility.Hidden;
        }
        if(!SystemParameters.ClientAreaAnimation){Finish();return;}
        var duration=TimeSpan.FromMilliseconds(show?210:160);
        var easing=new CubicEase{EasingMode=EasingMode.EaseOut};
        selectorOffset.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(show&&fromHidden?-20:selectorOffset.Y,targetY,duration){EasingFunction=easing},HandoffBehavior.SnapshotAndReplace);
        var opacity=new DoubleAnimation(show&&fromHidden?0:SelectorDropdown.Opacity,targetOpacity,duration){EasingFunction=easing};
        opacity.Completed+=(_,_)=>Finish();
        SelectorDropdown.BeginAnimation(OpacityProperty,opacity,HandoffBehavior.SnapshotAndReplace);
    }
    private static T? ParentOf<T>(DependencyObject? node) where T:DependencyObject
    {
        for(;node is not null;node=(node is Visual?VisualTreeHelper.GetParent(node):null)??LogicalTreeHelper.GetParent(node))
            if(node is T found)return found;
        return null;
    }
    private void ToolTabPressed(object sender,MouseButtonEventArgs e)
    {
        if(ParentOf<TabItem>(e.OriginalSource as DependencyObject) is not { } tab)return;
        OpenTool(Enum.Parse<ToolKind>((string)tab.Tag));e.Handled=true;
    }
}

