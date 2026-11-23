using System;
using Drawer.Core;
using static Drawer.NativeMethods;

namespace Drawer;
public sealed partial class MainWindow
{
    private SlotMotion? _sizeMotion;
    private SlotMotion? _positionMotion;
    private EdgeGeometry _sizeTarget;
    private void SetSizeTarget(EdgeGeometry target, bool immediate = false)
    {
        _sizeTarget=target;
        if (_sizeMotion == null || !_visible && !_previewOnly || immediate)
            _sizeMotion=new(target.Drawer.Width,target.Drawer.Height);
        if (_positionMotion == null || !_visible && !_previewOnly || immediate)
            _positionMotion = new((target.Drawer.Left + target.Drawer.Right) / 2d, (target.Drawer.Top + target.Drawer.Bottom) / 2d);
        _positionMotion.TargetX=(target.Drawer.Left+target.Drawer.Right)/2d;_positionMotion.TargetY=(target.Drawer.Top+target.Drawer.Bottom)/2d;
        _sizeMotion.TargetX=target.Drawer.Width; _sizeMotion.TargetY=target.Drawer.Height;
        StepSize(0);
    }
    private void StepSize(double dt, bool duringDrag = false)
    {
        if (_sizeMotion==null || (_internalDrag && !duringDrag) || _morphCommitted || _morphEnding) return;
        using var sample = IdlePerformance.Measure(IdlePerformance.Work.StepSize);
        _sizeMotion.Step(dt);
        _positionMotion?.Step(dt);
        var target=_sizeTarget; var work=target.Work;
        int width=Math.Clamp((int)Math.Round(_sizeMotion.X),1,Math.Max(1,work.Width));
        int height=Math.Clamp((int)Math.Round(_sizeMotion.Y),1,Math.Max(1,work.Height));
        int maxX = Math.Max(work.Left, work.Right - width), maxY = Math.Max(work.Top, work.Bottom - height);
        int x=!_surfaceFree&&_edge==DrawerEdge.Left?work.Left:!_surfaceFree&&_edge==DrawerEdge.Right?maxX:Math.Clamp((int)Math.Round((_positionMotion?.X??(target.Drawer.Left+target.Drawer.Right)/2d)-width/2d),work.Left,maxX);
        int y=!_surfaceFree&&_edge==DrawerEdge.Top?work.Top:Math.Clamp((int)Math.Round((_positionMotion?.Y??(target.Drawer.Top+target.Drawer.Bottom)/2d)-height/2d),work.Top,maxY);
        var current=target with {Drawer=new PixelRect(x,y,x+width,y+height)};
        if (_geometry!=current) { _geometry=current;_lastHeight=-1; }
        double contentWidth = Math.Max(1, (width - _frameWidth) / _presentationScale);
        double contentHeight = Math.Max(1, (height - _frameHeight) / _presentationScale);
        if (DropSurface.Width != contentWidth) DropSurface.Width = contentWidth;
        if (DropSurface.Height != contentHeight) DropSurface.Height = contentHeight;
        double padding = _resize?.Current.Metrics.Padding ?? _metrics.Padding;
        if (RootGrid.Padding.Left != padding) RootGrid.Padding = new Microsoft.UI.Xaml.Thickness(padding);
        UpdateNavigationTransition(dt, contentWidth);
        var trace = ResizeTrace;
        using (trace?.Measure(ResizePerformance.Stage.Content)) ApplyGridMetrics();
        using (trace?.Measure(ResizePerformance.Stage.Clip))
        {
            var rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, contentWidth - 2 * padding),
                Math.Max(0, contentHeight - 2 * padding - TopChrome));
            if (GridHost.Clip is not Microsoft.UI.Xaml.Media.RectangleGeometry clip)
            {
                GridHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = rect };
                trace?.Increment(ResizePerformance.Counter.ClipChanged);
            }
            else if (!clip.Rect.Equals(rect))
            {
                clip.Rect = rect;
                trace?.Increment(ResizePerformance.Counter.ClipChanged);
            }
            else trace?.Increment(ResizePerformance.Counter.ClipUnchanged);
        }
    }
    // Shell balloons are transient and have no layout footprint or keyboard-focus side effect.
    private void NotifyTransient(string message)
    {
        _tray.Flags=0x10; _tray.InfoTitle="Drawer";
        _tray.Info=message.Length>250?message[..250]+"…":message;
        Shell_NotifyIcon(1,ref _tray);
        _tray.Flags=7; _tray.Info="";_tray.InfoTitle="";
    }
}
