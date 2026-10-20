using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

internal sealed partial class RingLab
{
    internal static void TestSettingsPreview()
    {
        var p=new RingPreferences();
        using(var a=PreviewImage(p,new Size(340,340),new Point(170,80))) {
            p.Acrylic=false; p.RevealIntensity=0;
            using(var b=PreviewImage(p,new Size(340,340),new Point(170,80))) {
                if(a.GetPixel(170,55).ToArgb()==b.GetPixel(170,55).ToArgb()) throw new Exception("Preview ignores material settings");
                if(a.GetPixel(0,0).A!=0 || b.GetPixel(0,0).A!=0) throw new Exception("Preview alpha regression");
            }
        }
    }
    internal static void ShowMaterialDemo(RingPreferences preferences,Point point,uint dpi)
    {
        using(var demo=CreateOverlayCore(false)) {
            try {
                demo.BeginOverlayCore(0,point,dpi,preferences); demo.PreviewWait();
                DateTime until=DateTime.UtcNow.AddSeconds(8);
                while(demo.OverlayVisible && DateTime.UtcNow<until) {
                    Application.DoEvents(); ExplorerRingNative.MsgWaitForMultipleObjectsEx(0,IntPtr.Zero,10,0x04ff,4);
                }
                demo.DismissOverlay("Preview end",false);
            } finally { demo.log.Dispose(); }
        }
    }
    internal static Bitmap PreviewImage(RingPreferences preferences,Size size,Point pointer)
    {
        using(var painter=CreateOverlayCore(false)) {
            try {
                painter.appearance=preferences.Copy(); painter.scale=Math.Min(size.Width,size.Height)/340f;
                painter.center=new PointF(size.Width/2f,size.Height/2f); painter.radius=148*painter.scale; painter.inner=58*painter.scale;
                painter.last=pointer; painter.active=1; painter.waitInitialUp=false;
                painter.hover=painter.Hit(pointer); painter.materialBlur=preferences.Acrylic;
                painter.darkMaterial=false; painter.contrastReady=true; painter.whiteInk=false;
                var image=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(image)) { SetupGraphics(g); painter.DrawFluentRing(g); }
                return image;
            } finally { painter.log.Dispose(); }
        }
    }
}

internal sealed class SettingsPreview : Control
{
    internal RingPreferences Preferences=new RingPreferences();
    private Bitmap backdrop,ring;
    private Point pointer;
    private readonly Timer debounce=new Timer { Interval=35 };
    internal SettingsPreview()
    {
        DoubleBuffered=true; pointer=new Point(180,70);
        debounce.Tick+=delegate { debounce.Stop(); Rebuild(); };
        MouseMove+=delegate(object s,MouseEventArgs e) { pointer=e.Location; QueueRefresh(); };
        Resize+=delegate { QueueRefresh(); };
    }
    internal void QueueRefresh() { if(!debounce.Enabled) debounce.Start(); }
    internal void RefreshNow() { debounce.Stop(); Rebuild(); }
    private void Rebuild()
    {
        if(Width<10 || Height<10) return;
        if(backdrop!=null) backdrop.Dispose();
        backdrop=new Bitmap(Width,Height);
        using(var g=Graphics.FromImage(backdrop)) {
            g.Clear(Color.FromArgb(222,234,244));
            using(var b=new LinearGradientBrush(ClientRectangle,Color.FromArgb(205,226,244),Color.FromArgb(250,218,190),35)) g.FillRectangle(b,ClientRectangle);
            using(var b=new SolidBrush(Color.FromArgb(120,86,159,215))) g.FillEllipse(b,-70,30,Width,Height);
            using(var b=new SolidBrush(Color.FromArgb(110,244,166,112))) g.FillRectangle(b,Width/2,Height/3,Width/2,Height);
            using(var font=new Font("Segoe UI",13)) using(var b=new SolidBrush(Color.FromArgb(65,74,94))) {
                g.DrawString("Документы",font,b,25,30); g.DrawString("Проект      Фото      Заметки",font,b,32,Height-55);
            }
        }
        // Bounded synthetic blur approximation only. Real live DWM is shown
        // by the separate preview button; never capture the desktop here.
        if(Preferences.Acrylic) {
            using(var small=new Bitmap(Math.Max(1,Width/18),Math.Max(1,Height/18))) {
                using(var g=Graphics.FromImage(small)) g.DrawImage(backdrop,0,0,small.Width,small.Height);
                using(var g=Graphics.FromImage(backdrop)) using(var path=new GraphicsPath()) {
                    float r=148*Math.Min(Width,Height)/340f; path.AddEllipse(Width/2f-r,Height/2f-r,2*r,2*r); g.SetClip(path);
                    g.InterpolationMode=InterpolationMode.HighQualityBilinear; g.DrawImage(small,ClientRectangle);
                }
            }
        }
        if(ring!=null) ring.Dispose();
        ring=RingLab.PreviewImage(Preferences,Size,pointer); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    { if(backdrop!=null) e.Graphics.DrawImageUnscaled(backdrop,0,0); if(ring!=null) e.Graphics.DrawImageUnscaled(ring,0,0); }
    protected override void Dispose(bool disposing)
    { if(disposing) { debounce.Dispose(); if(backdrop!=null) backdrop.Dispose(); if(ring!=null) ring.Dispose(); } base.Dispose(disposing); }
}
