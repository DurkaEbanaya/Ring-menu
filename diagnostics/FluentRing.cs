using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Separate DWM backdrop and premultiplied-alpha foreground: never blur text.
// Windows 10's AccentPolicy entry point is undocumented: failure is harmless
// and falls back to a solid surface. No screenshots or background capture.
internal sealed partial class RingLab
{
    private bool materialBlur, darkMaterial;
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
        if(acrylic!=null) { acrylic.Close(); acrylic.Dispose(); acrylic=null; }
        materialBlur=false;
    }
    private void PresentOverlay()
    {
        if(!visibleRing || !IsHandleCreated || Width<=0 || Height<=0) return;
        using(var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb)) {
            using(var g=Graphics.FromImage(bitmap)) { g.SmoothingMode=SmoothingMode.AntiAlias; DrawFluentRing(g); }
            PresentBitmap(Handle,Location,bitmap);
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
        Color text=hc ? SystemColors.WindowText : darkMaterial ? Color.White : Color.FromArgb(28,28,28);
        var disk=new RectangleF(center.X-radius,center.Y-radius,2*radius,2*radius);
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
        bool reveal=active!=0 && !waitInitialUp && !hc;
        for(int i=0;i<6;i++) using(var path=SectorPath(i)) {
            if(hover==i) using(var b=new SolidBrush(hc ? SystemColors.Highlight : Color.FromArgb(65,accent))) g.FillPath(b,path);
            using(var pen=new Pen(hc ? SystemColors.WindowText : Color.FromArgb(darkMaterial ? 50 : 36,text),scale)) g.DrawPath(pen,path);
            if(reveal && enabled[i]) {
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
            double angle=(-90+i*60)*Math.PI/180; float mid=(inner+radius)/2;
            float x=center.X+(float)Math.Cos(angle)*mid, y=center.Y+(float)Math.Sin(angle)*mid;
            Color ink=enabled[i] ? (hc && hover==i ? SystemColors.HighlightText : text) : SystemColors.GrayText;
            DrawFluentIcon(g,names[i],x,y-11*scale,ink);
            DrawText(g,names[i],x,y+15*scale,names[i].Length>12 ? 10 : 11,ink,true);
        }
        using(var pen=new Pen(Color.FromArgb(hc ? 255 : 80,text),scale)) g.DrawEllipse(pen,disk);
        DrawText(g,contextLabel,center.X,center.Y-9*scale,14,text,true);
        DrawText(g,"Отмена",center.X,center.Y+13*scale,11,hc ? text : darkMaterial ? Color.Silver : Color.DimGray,true);
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
