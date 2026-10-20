using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class RingLab
{
    private bool integrated;
    private OutsideVeil veil;
    private AcrylicSurface acrylic;
    internal event Action<int> Selected;
    internal event Action<string> OverlayCancelled;
    internal bool AwaitingNextTouch { get { return visibleRing && fixedMode && !waitInitialUp; } }
    internal bool OverlayVisible { get { return visibleRing; } }
    internal uint OverlayContact { get { return active; } }
    internal static RingLab CreateOverlay()
    { return CreateOverlayCore(true); }
    private static RingLab CreateOverlayCore(bool writeLog)
    {
        var f = new RingLab(writeLog);
        f.integrated = true;
        f.timer.Stop();
        f.Text = "Explorer Ring";
        f.BackColor = Color.Black;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        return f;
    }
    protected override bool ShowWithoutActivation { get { return integrated; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            if (integrated) p.ExStyle |= 0x08080080; // NOACTIVATE | LAYERED | TOOLWINDOW
            return p;
        }
    }
    internal void BeginOverlay(uint id, Point screen, uint dpi)
    {
        DismissOverlay("New contact", false);
        Bounds = Screen.FromPoint(screen).WorkingArea;
        scale = (dpi == 0 ? 96 : dpi) / 96f;
        radius = 148*scale; inner = 58*scale; margin = 14*scale; guard = 26*scale;
        anchor = last = new PointF(screen.X - Left, screen.Y - Top);
        active = id;
        OpenRing();
        if (!visibleRing) return;
        // Keep per-pixel composition small, independent of monitor resolution.
        var screenCenter = new PointF(Left+center.X, Top+center.Y);
        int extentPixels = (int)Math.Ceiling(radius+9*scale);
        var origin = new Point((int)Math.Floor(screenCenter.X)-extentPixels,
            (int)Math.Floor(screenCenter.Y)-extentPixels);
        var offset = new PointF(Left-origin.X,Top-origin.Y);
        Bounds = new Rectangle(origin,new Size(extentPixels*2+2,extentPixels*2+2));
        center = new PointF(center.X+offset.X,center.Y+offset.Y);
        anchor = new PointF(anchor.X+offset.X,anchor.Y+offset.Y);
        last = new PointF(last.X+offset.X,last.Y+offset.Y);
        // Limit window hit testing to the visible circle. Existing Explorer
        // contact stays implicitly captured to Explorer, forwarded as data.
        using (var path = new GraphicsPath())
        {
            float extent = radius + 9*scale;
            path.AddEllipse(center.X-extent, center.Y-extent, 2*extent, 2*extent);
            var old = Region;
            Region = new Region(path);
            if (old != null) old.Dispose();
        }
        LoadFluentPalette();
        SampleRingContrast();
        if(contrastReady) darkMaterial=whiteInk;
        acrylic = new AcrylicSurface(Bounds,center,radius,darkMaterial);
        acrylic.Show();
        materialBlur = acrylic.EnableMaterial();
        Log("MATERIAL requested=ClippedBlurTintGrain alphaTransport=DIB adaptiveContrast="+contrastReady+" apiAccepted="+materialBlur+" dark="+darkMaterial+" highContrast="+SystemInformation.HighContrast);
        PresentOverlay();
        Show();
        // Original Shown handler reads monitor DPI, starts no timer.
        Invalidate();
    }
    internal void Configure(string[] labels,bool[] available,bool background)
    {
        if(labels.Length!=6 || available.Length!=6) throw new ArgumentException("Six sectors required");
        Array.Copy(labels,names,6); Array.Copy(available,enabled,6);
        contextLabel=background ? "Папка" : "Файл";
    }
    internal void ForwardMove(uint id, Point screen, bool cancelled)
    {
        if (!visibleRing) return;
        if (cancelled) { DismissOverlay("System cancelled contact", true); return; }
        TouchMove(id, new PointF(screen.X-Left, screen.Y-Top));
        PresentOverlay();
    }
    internal void ForwardUp(uint id, Point screen, bool cancelled)
    {
        if (!visibleRing) return;
        if (cancelled) { DismissOverlay("System cancelled contact", true); return; }
        TouchUp(id, new PointF(screen.X-Left, screen.Y-Top));
        PresentOverlay();
        if (AwaitingNextTouch)
        {
            veil = new OutsideVeil(Screen.FromPoint(screen).WorkingArea);
            veil.Touched += delegate { DismissOverlay("Outside touch", true); };
            veil.Show();
            if(acrylic!=null) ExplorerRingNative.SetWindowPos(acrylic.Handle,new IntPtr(-1),0,0,0,0,0x13);
            // Keep ring above veil without activating Explorer or overlay.
            ExplorerRingNative.SetWindowPos(Handle, new IntPtr(-1), 0,0,0,0, 0x13);
        }
    }
    internal void DismissOverlay(string reason, bool notify)
    {
        if (veil != null) { veil.Close(); veil.Dispose(); veil = null; }
        CloseMaterial();
        Cancel(reason);
        Hide();
        if (notify && OverlayCancelled != null) OverlayCancelled(reason);
    }
    private void CompleteOverlay(int sector)
    {
        if (veil != null) { veil.Close(); veil.Dispose(); veil = null; }
        CloseMaterial();
        Hide();
        if (Selected != null) Selected(sector);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing && integrated) { CloseMaterial(); if(veil!=null) { veil.Dispose(); veil=null; } }
        base.Dispose(disposing);
    }
    private bool OverlayMessage(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return true; } // MA_NOACTIVATE
        if (m.Msg == 0x246 || m.Msg == 0x245 || m.Msg == 0x247)
        {
            uint id = unchecked((uint)m.WParam.ToInt64()) & 0xffff;
            PointerInfo p;
            if (GetPointerInfo(id, out p) && p.Type == 2)
            {
                if (!AwaitingNextTouch && active == 0) { DismissOverlay("Unexpected touch", true); }
                else if ((p.Flags & 0x8000) != 0) DismissOverlay("Cancelled", true);
                else
                {
                    var point = new PointF(p.Pixel.X-Left, p.Pixel.Y-Top);
                    if (m.Msg == 0x246) TouchDown(id, point);
                    else if (m.Msg == 0x245) TouchMove(id, point);
                    else TouchUp(id, point);
                    PresentOverlay();
                    if (!visibleRing && Visible) DismissOverlay("Touch cancelled", true);
                }
                m.Result = IntPtr.Zero; return true;
            }
        }
        if (m.Msg == 0x24c && active != 0) { DismissOverlay("Overlay lost capture", true); m.Result = IntPtr.Zero; return true; }
        return false;
    }

    private sealed class OutsideVeil : Form
    {
        internal event Action Touched;
        private uint contact;
        internal OutsideVeil(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black; Opacity = 0.01;
            ShowInTaskbar = false; TopMost = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
            if (m.Msg == 0x2cc) { m.Result = new IntPtr(0x10001); return; }
            if (m.Msg == 0x7b) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x246) { contact = unchecked((uint)m.WParam.ToInt64()) & 0xffff; m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x245) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x247)
            {
                if (contact == (unchecked((uint)m.WParam.ToInt64()) & 0xffff) && Touched != null) Touched();
                m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
        }
    }
}
