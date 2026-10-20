using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Separate DWM backdrop and premultiplied-alpha foreground: never blur text.
// Windows 10's AccentPolicy entry point is undocumented: failure is harmless
// and falls back to a solid surface. Local contrast samples never leave memory.
internal sealed partial class RingLab
{
    private bool materialBlur, darkMaterial;
    private bool whiteInk;
    private bool contrastReady;
    private Bitmap ringBase,ringLabels;
    private Bitmap revealFill;
    private GraphicsPath[] sectorCache;
    private GraphicsPath revealClip;
    private LayeredFrame ringFrame;
    private PointF presentedPoint;
    private int presentedHover;
    private uint presentedContact;
    private bool presentedWait,framePresented;
    private int renderPass;
    internal static void WarmRenderer()
    {
        // Pay JIT/GDI+ initialization before arming, never at first gesture.
        // Own hidden surface only: no Explorer, screen sample, or commands.
        using(var f=CreateOverlayCore(false)) {
            try {
                f.ClientSize=new Size(630,630); f.scale=2; f.radius=296; f.inner=116;
                f.center=new PointF(315,315); f.last=new PointF(315,109);
                f.visibleRing=true; f.active=123; f.materialBlur=true;
                var handle=f.Handle; f.PresentOverlay();
                if(f.Visible) throw new InvalidOperationException("Renderer warmup showed a window");
            } finally { f.ClearRingCache(); f.log.Dispose(); }
        }
    }
    private void ClearRingCache()
    {
        if(revealFill!=null) { revealFill.Dispose(); revealFill=null; }
        if(sectorCache!=null) { foreach(var path in sectorCache) path.Dispose(); sectorCache=null; }
        if(revealClip!=null) { revealClip.Dispose(); revealClip=null; }
        if(ringBase!=null) { ringBase.Dispose(); ringBase=null; }
        if(ringLabels!=null) { ringLabels.Dispose(); ringLabels=null; }
        if(ringFrame!=null) { ringFrame.Dispose(); ringFrame=null; }
        renderPass=0;
        framePresented=false;
    }
    // One bounded sample BEFORE any ring/backdrop/veil is visible. Do not
    // resample our own surface, save pixels, or log pixel/colour values.
    private void SampleRingContrast()
    {
        contrastReady=false;
        if(SystemInformation.HighContrast) return;
        try {
            using(var sample=new Bitmap(Width,Height,PixelFormat.Format32bppArgb)) {
                using(var g=Graphics.FromImage(sample)) g.CopyFromScreen(Location,Point.Empty,Size,CopyPixelOperation.SourceCopy);
                whiteInk=WhiteInkFor(sample,new Rectangle((int)(center.X-radius),(int)(center.Y-radius),(int)(2*radius),(int)(2*radius)));
            }
            contrastReady=true;
        } catch(System.ComponentModel.Win32Exception) { }
          catch(System.Runtime.InteropServices.ExternalException) { }
          catch(ArgumentException) { }
    }
    private static double Linear(byte value)
    { double s=value/255.0; return s<=.04045 ? s/12.92 : Math.Pow((s+.055)/1.055,2.4); }
    private static readonly double[] linearLuma=BuildLinearLuma();
    private static double[] BuildLinearLuma()
    { var values=new double[256]; for(int i=0;i<256;i++) values[i]=Linear((byte)i); return values; }
    private static bool WhiteInkFor(Bitmap sample,Rectangle patch)
    {
        patch.Intersect(new Rectangle(Point.Empty,sample.Size));
        if(patch.Width==0 || patch.Height==0) throw new ArgumentException("Empty contrast sample");
        double sum=0; int count=0;
        // Same pixels and linear-light rule, but no GetPixel interop and no
        // repeated pow() for every sample while the user waits for the menu.
        var data=sample.LockBits(new Rectangle(Point.Empty,sample.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {
            var row=new byte[patch.Width*4];
            for(int y=patch.Top;y<patch.Bottom;y+=4) {
                Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride+patch.Left*4),row,0,row.Length);
                for(int x=0;x<patch.Width;x+=4) {
                    int p=x*4; sum+=.2126*linearLuma[row[p+2]]+.7152*linearLuma[row[p+1]]+.0722*linearLuma[row[p]]; count++;
                }
            }
        } finally { sample.UnlockBits(data); }
        // Pick the greater WCAG contrast against the underlying linear luma.
        double l=sum/count; return 1.05/(l+.05)>=(l+.05)/.05;
    }
    private Color ReadableInk()
    { return SystemInformation.HighContrast ? SystemColors.WindowText : (contrastReady ? whiteInk : darkMaterial) ? Color.White : Color.Black; }
    internal static void TestContrast()
    {
        using(var b=new Bitmap(80,40)) using(var g=Graphics.FromImage(b)) {
            g.Clear(Color.Black);
            if(!WhiteInkFor(b,new Rectangle(0,0,80,40))) throw new Exception("Dark background contrast regression");
            g.Clear(Color.White);
            if(WhiteInkFor(b,new Rectangle(0,0,80,40))) throw new Exception("Light background contrast regression");
            using(var painter=CreateOverlay()) {
                try {
                    painter.contrastReady=true;
                    foreach(bool white in new[] { false,true }) {
                        painter.whiteInk=white;
                        if(!SystemInformation.HighContrast && painter.ReadableInk().ToArgb()!=(white ? Color.White : Color.Black).ToArgb())
                            throw new Exception("Uniform ring foreground regression");
                    }
                } finally { painter.log.Dispose(); }
            }
            g.Clear(Color.FromArgb(117,117,117));
            if(!WhiteInkFor(b,new Rectangle(0,0,80,40))) throw new Exception("Linear luminance threshold regression");
            double sum=0; int count=0;
            var patch=new Rectangle(3,5,61,29);
            for(int y=patch.Top;y<patch.Bottom;y+=4) for(int x=patch.Left;x<patch.Right;x+=4) {
                var c=Color.FromArgb(x*3,y*6,(x+y)*2); b.SetPixel(x,y,c);
                sum+=.2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B); count++;
            }
            double mean=sum/count;
            if(WhiteInkFor(b,patch)!=(1.05/(mean+.05)>=(mean+.05)/.05)) throw new Exception("Row luminance differs from pixel reference");
        }
    }
    private Color accent = Color.FromArgb(0,120,215);
    [StructLayout(LayoutKind.Sequential)] private struct Blend { internal byte Op,Flags,Alpha,Format; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int X,Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [StructLayout(LayoutKind.Sequential)] private struct DibInfo
    {
        internal uint Size; internal int Width,Height; internal ushort Planes,Bits;
        internal uint Compression,ImageSize; internal int XPels,YPels; internal uint Used,Important;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,ref DibInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dc,ref PointNative dest,ref NativeSize size,IntPtr src,ref PointNative origin,uint key,ref Blend blend,uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint color,out bool opaque);
    private void LoadFluentPalette()
    {
        darkMaterial=false;
        using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            if(k!=null) darkMaterial=Convert.ToInt32(k.GetValue("AppsUseLightTheme",1))==0;
        uint color; bool opaque;
        if(DwmGetColorizationColor(out color,out opaque)==0) accent=Color.FromArgb((int)(color & 0xffffff)|unchecked((int)0xff000000));
    }
    private void CloseMaterial()
    {
        ClearRingCache();
        if(acrylic!=null) { acrylic.Close(); acrylic.Dispose(); acrylic=null; }
        materialBlur=false;
    }
    private void PresentOverlay()
    {
        if(!visibleRing || !IsHandleCreated || Width<=0 || Height<=0) return;
        if(framePresented && ringFrame!=null && ringFrame.Bitmap.Size==Size && presentedPoint==last &&
            presentedHover==hover && presentedContact==active && presentedWait==waitInitialUp) return;
        if(ringFrame==null || ringFrame.Bitmap.Width!=Width || ringFrame.Bitmap.Height!=Height) {
            ClearRingCache();
            ringFrame=new LayeredFrame(Width,Height);
            ringBase=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
            ringLabels=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
            BuildRevealCache();
            try {
                renderPass=1; using(var g=Graphics.FromImage(ringBase)) { SetupGraphics(g); DrawFluentRing(g); }
                renderPass=2; using(var g=Graphics.FromImage(ringLabels)) { SetupGraphics(g); DrawFluentRing(g); }
            } finally { renderPass=0; }
        }
        var graphics=ringFrame.Graphics;
        if(SystemInformation.HighContrast) {
            // Accessibility highlight text depends on hover; never freeze it.
            graphics.Clear(Color.Transparent); DrawFluentRing(graphics);
            ringFrame.Present(Handle,Location); RememberFrame(); return;
        }
        graphics.CompositingMode=CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(ringBase,0,0);
        graphics.CompositingMode=CompositingMode.SourceOver;
        DrawCachedReveal(graphics);
        graphics.DrawImageUnscaled(ringLabels,0,0);
        ringFrame.Present(Handle,Location);
        RememberFrame();
    }
    private void RememberFrame()
    { presentedPoint=last; presentedHover=hover; presentedContact=active; presentedWait=waitInitialUp; framePresented=true; }
    private void BuildRevealCache()
    {
        sectorCache=new GraphicsPath[6]; revealClip=new GraphicsPath();
        for(int i=0;i<6;i++) {
            sectorCache[i]=SectorPath(i);
            if(enabled[i]) revealClip.AddPath(sectorCache[i],false);
        }
        float r=94*scale; int size=(int)Math.Ceiling(2*r)+4;
        revealFill=new Bitmap(size,size,PixelFormat.Format32bppPArgb);
        var c=new PointF(size/2f,size/2f);
        using(var g=Graphics.FromImage(revealFill)) using(var glow=new GraphicsPath()) {
            SetupGraphics(g); glow.AddEllipse(c.X-r,c.Y-r,2*r,2*r);
            using(var b=new PathGradientBrush(glow)) {
                b.CenterPoint=c; b.CenterColor=Color.FromArgb(76,accent); b.SurroundColors=new[] { Color.FromArgb(0,accent) };
                g.FillPath(b,glow);
            }
        }
    }
    private void DrawCachedReveal(Graphics g)
    {
        if(hover>=0 && hover<6) using(var b=new SolidBrush(Color.FromArgb(65,accent))) g.FillPath(b,sectorCache[hover]);
        if(active==0 || waitInitialUp) return;
        var state=g.Save();
        try {
            g.SetClip(revealClip,CombineMode.Intersect);
            g.DrawImageUnscaled(revealFill,(int)Math.Round(last.X-revealFill.Width/2f),(int)Math.Round(last.Y-revealFill.Height/2f));
        } finally { g.Restore(state); }
        // One shared border gradient, not six separate paths/brushes per frame.
        using(var glow=new GraphicsPath()) {
            float r=110*scale; glow.AddEllipse(last.X-r,last.Y-r,2*r,2*r);
            using(var b=new PathGradientBrush(glow)) {
                b.CenterPoint=last; b.CenterColor=Color.FromArgb(230,accent); b.SurroundColors=new[] { Color.FromArgb(0,accent) };
                using(var pen=new Pen(b,1.6f*scale)) g.DrawPath(pen,revealClip);
            }
        }
    }
    private static void SetupGraphics(Graphics g)
    { g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; }
    // Persistent DIB is also the GDI+ render target: no per-frame bitmap/DC,
    // LockBits, managed row buffers or double Marshal.Copy of every pixel.
    private sealed class LayeredFrame : IDisposable
    {
        internal Bitmap Bitmap; internal Graphics Graphics;
        private IntPtr dc,image,old;
        internal LayeredFrame(int width,int height)
        {
            try {
                var info=new DibInfo { Size=40,Width=width,Height=-height,Planes=1,Bits=32 };
                IntPtr bits; image=CreateDIBSection(IntPtr.Zero,ref info,0,out bits,IntPtr.Zero,0);
                dc=CreateCompatibleDC(IntPtr.Zero);
                if(image==IntPtr.Zero || bits==IntPtr.Zero || dc==IntPtr.Zero) throw new InvalidOperationException("Frame allocation failed");
                old=SelectObject(dc,image);
                Bitmap=new Bitmap(width,height,width*4,PixelFormat.Format32bppPArgb,bits);
                Graphics=Graphics.FromImage(Bitmap); SetupGraphics(Graphics);
            } catch { Dispose(); throw; }
        }
        internal void Present(IntPtr handle,Point location)
        {
            Graphics.Flush(FlushIntention.Sync);
            var dest=new PointNative { X=location.X,Y=location.Y }; var size=new NativeSize { X=Bitmap.Width,Y=Bitmap.Height }; var origin=new PointNative();
            var blend=new Blend { Alpha=255,Format=1 };
            if(!UpdateLayeredWindow(handle,IntPtr.Zero,ref dest,ref size,dc,ref origin,0,ref blend,2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            if(Graphics!=null) { Graphics.Dispose(); Graphics=null; }
            if(Bitmap!=null) { Bitmap.Dispose(); Bitmap=null; }
            if(old!=IntPtr.Zero) { SelectObject(dc,old); old=IntPtr.Zero; }
            if(image!=IntPtr.Zero) { DeleteObject(image); image=IntPtr.Zero; }
            if(dc!=IntPtr.Zero) { DeleteDC(dc); dc=IntPtr.Zero; }
        }
    }
    private static void PresentBitmap(IntPtr handle,Point location,Bitmap bitmap)
    {
            IntPtr screen=GetDC(IntPtr.Zero), dc=IntPtr.Zero, image=IntPtr.Zero, old=IntPtr.Zero;
            try {
                dc=CreateCompatibleDC(screen);
                IntPtr bits; image=AlphaDib(screen,bitmap,out bits);
                if(dc==IntPtr.Zero) throw new InvalidOperationException("Alpha DC allocation failed");
                old=SelectObject(dc,image);
                var dest=new PointNative { X=location.X,Y=location.Y }; var size=new NativeSize { X=bitmap.Width,Y=bitmap.Height }; var origin=new PointNative();
                var blend=new Blend { Alpha=255,Format=1 };
                if(!UpdateLayeredWindow(handle,screen,ref dest,ref size,dc,ref origin,0,ref blend,2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            } finally { if(old!=IntPtr.Zero) SelectObject(dc,old); if(image!=IntPtr.Zero) DeleteObject(image); if(dc!=IntPtr.Zero) DeleteDC(dc); if(screen!=IntPtr.Zero) ReleaseDC(IntPtr.Zero,screen); }
    }
    private static IntPtr AlphaDib(IntPtr screen,Bitmap bitmap,out IntPtr bits)
    {
        var info=new DibInfo { Size=40,Width=bitmap.Width,Height=-bitmap.Height,Planes=1,Bits=32 };
        IntPtr image=CreateDIBSection(screen,ref info,0,out bits,IntPtr.Zero,0);
        if(image==IntPtr.Zero || bits==IntPtr.Zero) throw new InvalidOperationException("Alpha surface allocation failed");
        try {
            // GetHbitmap can flatten alpha into a background colour. Copy
            // exact premultiplied BGRA into a top-down DIB instead.
            var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try {
                var row=new byte[bitmap.Width*4];
                for(int y=0;y<bitmap.Height;y++) {
                    Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                    Marshal.Copy(row,0,IntPtr.Add(bits,y*row.Length),row.Length);
                }
            } finally { bitmap.UnlockBits(data); }
            return image;
        } catch { DeleteObject(image); throw; }
    }
    internal static void TestAlphaTransport()
    {
        using(var b=new Bitmap(2,2,PixelFormat.Format32bppPArgb)) {
            b.SetPixel(1,0,Color.FromArgb(64,200,100,40)); b.SetPixel(0,1,Color.FromArgb(128,40,100,200));
            IntPtr bits; IntPtr image=AlphaDib(IntPtr.Zero,b,out bits);
            try {
                var actual=new byte[16]; Marshal.Copy(bits,actual,0,16);
                if(actual[3]!=0 || actual[7]!=64 || actual[11]!=128 || actual[15]!=0 ||
                   actual[4]>64 || actual[5]>64 || actual[6]>64) throw new Exception("DIB alpha/top-down/premultiplication regression");
            } finally { DeleteObject(image); }
        }
    }
    private GraphicsPath SectorPath(int i)
    {
        var p=new GraphicsPath(); float start=-120+i*60;
        p.AddArc(center.X-radius,center.Y-radius,2*radius,2*radius,start+.7f,58.6f);
        p.AddArc(center.X-inner,center.Y-inner,2*inner,2*inner,start+59.3f,-58.6f); p.CloseFigure(); return p;
    }
    private void DrawFluentRing(Graphics g)
    {
        bool hc=SystemInformation.HighContrast;
        Color surface=hc ? SystemColors.Window : darkMaterial ? Color.FromArgb(38,38,38) : Color.FromArgb(243,243,243);
        Color text=ReadableInk();
        var disk=new RectangleF(center.X-radius,center.Y-radius,2*radius,2*radius);
        bool background=renderPass==0 || renderPass==1, labels=renderPass==0 || renderPass==2, dynamic=renderPass==0 || renderPass==3;
        if(background) {
        // Soft elevation remains inside the existing 9-DIP safety margin.
        for(int n=8;n>=1;n--) using(var b=new SolidBrush(Color.FromArgb(3,0,0,0)))
            g.FillEllipse(b,disk.X-n*scale,disk.Y-n*scale+scale,disk.Width+2*n*scale,disk.Height+2*n*scale);
        using(var b=new SolidBrush(Color.FromArgb(materialBlur && !hc ? 32 : 255,surface))) g.FillEllipse(b,disk);
        if(materialBlur && !hc) {
            // Very subtle, deterministic grain. No stored/captured background.
            var state=g.Save();
            using(var clip=new GraphicsPath()) { clip.AddEllipse(disk); g.SetClip(clip,CombineMode.Intersect); }
            using(var grain=new Bitmap(32,32,PixelFormat.Format32bppPArgb)) {
                uint seed=17;
                for(int n=0;n<128;n++) {
                    seed=1664525*seed+1013904223; int x=(int)(seed & 31);
                    seed=1664525*seed+1013904223; int y=(int)((seed>>8) & 31);
                    grain.SetPixel(x,y,(n & 1)==0 ? Color.FromArgb(8,255,255,255) : Color.FromArgb(5,0,0,0));
                }
                using(var texture=new TextureBrush(grain)) { texture.ScaleTransform(scale,scale); g.FillEllipse(texture,disk); }
            }
            g.Restore(state);
        }
        }
        bool reveal=active!=0 && !waitInitialUp && !hc;
        for(int i=0;i<6;i++) using(var path=SectorPath(i)) {
            if(dynamic && hover==i) using(var b=new SolidBrush(hc ? SystemColors.Highlight : Color.FromArgb(65,accent))) g.FillPath(b,path);
            if(background) using(var pen=new Pen(hc ? SystemColors.WindowText : Color.FromArgb(darkMaterial ? 50 : 36,text),scale)) g.DrawPath(pen,path);
            if(dynamic && reveal && enabled[i]) {
                var state=g.Save(); g.SetClip(path,CombineMode.Intersect);
                using(var glow=new GraphicsPath()) {
                    float r=94*scale; glow.AddEllipse(last.X-r,last.Y-r,2*r,2*r);
                    using(var b=new PathGradientBrush(glow)) {
                        b.CenterPoint=last; b.CenterColor=Color.FromArgb(76,accent); b.SurroundColors=new[] { Color.FromArgb(0,accent) };
                        g.FillPath(b,glow);
                    }
                }
                g.Restore(state);
                using(var glow=new GraphicsPath()) {
                    float r=110*scale; glow.AddEllipse(last.X-r,last.Y-r,2*r,2*r);
                    using(var b=new PathGradientBrush(glow)) {
                        b.CenterPoint=last; b.CenterColor=Color.FromArgb(230,accent); b.SurroundColors=new[] { Color.FromArgb(0,accent) };
                        using(var pen=new Pen(b,1.6f*scale)) g.DrawPath(pen,path);
                    }
                }
            }
            if(!labels) continue;
            double angle=(-90+i*60)*Math.PI/180; float mid=(inner+radius)/2;
            float x=center.X+(float)Math.Cos(angle)*mid, y=center.Y+(float)Math.Sin(angle)*mid;
            Color ink=hc ? (hover==i ? SystemColors.HighlightText : SystemColors.WindowText) : text;
            if(!enabled[i]) ink=Color.FromArgb(145,ink);
            DrawFluentIcon(g,names[i],x,y-11*scale,ink);
            DrawText(g,names[i],x,y+15*scale,names[i].Length>12 ? 11 : 12,ink,true);
        }
        if(background) using(var pen=new Pen(Color.FromArgb(hc ? 255 : 80,text),scale)) g.DrawEllipse(pen,disk);
        if(!labels) return;
        Color centerInk=text;
        DrawText(g,contextLabel,center.X,center.Y-9*scale,14,centerInk,true);
        DrawText(g,"Отмена",center.X,center.Y+13*scale,11,centerInk,true);
    }
    private void DrawFluentIcon(Graphics g,string name,float x,float y,Color color)
    {
        string glyph=name=="Открыть" ? "\uE8E5" : name=="Копировать" ? "\uE8C8" : name=="Вырезать" ? "\uE8C6" :
            name=="Переименовать" ? "\uE8AC" : name=="В корзину" ? "\uE74D" : name=="Вставить" ? "\uE77F" :
            name=="Вставить ярлык" ? "\uE71B" : name=="Обновить" ? "\uE72C" : name=="Новая папка" ? "\uE8F4" : name=="Свойства" ? "\uE946" : "\uE712";
        using(var font=new Font("Segoe MDL2 Assets",21*scale,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(color)) using(var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center })
            g.DrawString(glyph,font,brush,x,y,format);
    }
    private sealed class AcrylicSurface : Form
    {
        [StructLayout(LayoutKind.Sequential)] private struct Policy { internal int State,Flags; internal uint Color; internal int Animation; }
        [StructLayout(LayoutKind.Sequential)] private struct Attribute { internal int Id; internal IntPtr Data; internal int Size; }
        [StructLayout(LayoutKind.Sequential)] private struct Power { internal byte AC,Flag,Percent,Saver; internal uint Life,FullLife; }
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr h,ref Attribute a);
        [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out Power status);
        private readonly bool dark;
        private bool circular;
        internal AcrylicSurface(Rectangle bounds,PointF center,float radius,bool dark)
            : this(bounds,dark)
        {
            circular=true;
            using(var p=new GraphicsPath()) { p.AddEllipse(center.X-radius,center.Y-radius,radius*2,radius*2); Region=new Region(p); }
        }
        internal AcrylicSurface(Rectangle bounds,bool dark)
        {
            this.dark=dark; FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
            Bounds=bounds; BackColor=Color.Black; ShowInTaskbar=false; TopMost=true;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x080000a0; return p; } } // transparent to input
        protected override void WndProc(ref Message m)
        { if(m.Msg==0x84) { m.Result=new IntPtr(-1); return; } if(m.Msg==0x21) { m.Result=new IntPtr(3); return; } base.WndProc(ref m); }
        internal bool EnableMaterial()
        {
            if(SystemInformation.HighContrast || SystemInformation.TerminalServerSession) return false;
            Power power; if(GetSystemPowerStatus(out power) && power.Saver!=0) return false;
            using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                if(k!=null && Convert.ToInt32(k.GetValue("EnableTransparency",1))==0) return false;
            bool enabled; if(DwmIsCompositionEnabled(out enabled)!=0 || !enabled) return false;
            // NO WinUI dependency, no registry writes. API acceptance does not
            // prove visible blur: actual appearance must be tested in console.
            // Win10 Acrylic accent (4) can ignore a nonrectangular window
            // region. A circular surface uses clipped blur accent (3) plus
            // our own tint/grain: custom Acrylic, not a rectangular accent.
            var policy=new Policy { State=circular ? 3 : 4,Color=dark ? 0x70262626U : 0x70F3F3F3U };
            IntPtr ptr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Policy)));
            try {
                Marshal.StructureToPtr(policy,ptr,false);
                var a=new Attribute { Id=19,Data=ptr,Size=Marshal.SizeOf(typeof(Policy)) };
                // Do not extend rectangular glass beyond the explicit region.
                // Accent owns the backdrop; WinForms must never fill it black.
                return SetWindowCompositionAttribute(Handle,ref a)!=0;
            } catch(EntryPointNotFoundException) { return false; }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
