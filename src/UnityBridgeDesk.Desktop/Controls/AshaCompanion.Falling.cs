using System.Windows;

namespace UnityBridgeDesk.Desktop.Controls;

public sealed partial class AshaCompanion
{
    private bool falling;
    private bool awaitingGround;
    private double fallSpeed, fallDistance;

    private void SetDown()
    {
        // Released/lost capture share the same path. Refresh before landing so a removed
        // or resized control cannot keep supporting the character through an old map.
        RefreshGeometry();
        if (IsStanding) FinishFall();
    }

    private void BeginFall()
    {
        if (map is null) return;
        if (falling) return;
        StopRoute(); chartPlay.Cancel(clock.Elapsed.TotalSeconds);
        falling = true; Intent = AshaIntent.Descend; interestPoint = null;
        host.Opacity = 1; mascot.MotionEnabled = CanRun;
        mascot.SetAction(AshaAction.Drop, mascot.FacesLeft, .2);
    }

    private void TickFall(double seconds)
    {
        if (map is null) return;
        var result = map.Fall(Position, fallSpeed, seconds);
        fallDistance += Math.Max(0, result.Position.Y - Position.Y);
        fallSpeed = result.Speed;
        Place(result.Position);
        if (result.Contact == AshaFallContact.Ledge) { FinishFall(); return; }
        if (result.Contact is AshaFallContact.Blocked or AshaFallContact.Boundary) { RecoverFromFall(); return; }
        mascot.SetAction(AshaAction.Drop, mascot.FacesLeft, Math.Clamp(fallDistance / 225, .2, 1));
    }

    private void FinishFall()
    {
        if (map is null || !IsStanding) return;
        double now = clock.Elapsed.TotalSeconds, impact = Math.Clamp(fallDistance / 225, .2, 1);
        StopRoute(); lastSupport = Position;
        explorer.Arrived(Position, Position, VisitId(Position));
        landingLeft = mascot.FacesLeft; landUntil = now + .16 + impact * .18; groomingUntil = landUntil + .55;
        nextDecision = Math.Max(nextDecision, groomingUntil + TravelPause());
        if (map.Support(Position) is { } support) mascot.SetPerch(support.Right - support.Left < host.Width * 1.6);
        mascot.SetAction(AshaAction.Land, landingLeft, impact);
    }

    private void RecoverFromFall()
    {
        // The viewport edge is not a floor. After descending without a valid landing,
        // reappear on a verified surface instead of hovering at the boundary.
        StopRoute(); host.Opacity = 0; host.IsHitTestVisible = false;
        if (map?.Nearest(Position) is { } safe)
        {
            Place(safe); lastSupport = safe; RevealPlacement(); FinishFall();
        }
        else { awaitingGround = true; mascot.MotionEnabled = false; }
    }
}
