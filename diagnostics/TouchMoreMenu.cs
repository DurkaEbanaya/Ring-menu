using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

internal sealed partial class RingLab
{
        internal static void TestTouchMenu(ShellCommands shell)
    {
        using(var menu=new TouchMoreMenu(shell,new Point(100,100))) {
            var handle=menu.Handle; menu.Render();
            if(menu.Visible) throw new Exception("Hidden touch menu test showed a window");
            menu.TestRows();
        }
    }
    internal static ShellCommands.Entry ShowTouchMenu(ShellCommands shell,Point point,IntPtr root,out bool original)
    {
        using(var menu=new TouchMoreMenu(shell,point))
        using(var shield=new OutsideVeil(Screen.FromPoint(point).WorkingArea)) {
            menu.SampleContrast();
            shield.Touched+=delegate { menu.Close(); };
            shield.MouseUp+=delegate { menu.Close(); };
            shield.Show(); menu.Show(); menu.Render();
            DateTime until=DateTime.UtcNow.AddSeconds(30);
            try {
                while(menu.Visible && DateTime.UtcNow<until && ExplorerRingNative.GetForegroundWindow()==root) {
                    Application.DoEvents(); System.Threading.Thread.Sleep(10);
                }
                original=menu.Original; return menu.Choice;
            } finally { menu.Close(); shield.Close(); }
        }
    }
    private sealed class TouchMoreMenu : Form
    {
        private readonly ShellCommands shell;
        private readonly RingLab painter;
        private AcrylicSurface backdrop;
        private List<ShellCommands.Entry> entries;
        private readonly Stack<Tuple<System.Collections.Generic.List<ShellCommands.Entry>,int>> parents=new Stack<Tuple<System.Collections.Generic.List<ShellCommands.Entry>,int>>();
        private IntPtr popup;
        private int offset,firstRow,shown,hot=-1,pressed=-1;
        private Point lastPoint;
        private uint contact;
        private readonly int row,pad;
        private readonly float dpi;
        internal ShellCommands.Entry Choice;
        internal bool Original;
        private readonly Rectangle work;
        internal TouchMoreMenu(ShellCommands shell,Point point)
        {
            this.shell=shell; popup=shell.Menu;
            entries=shell.Entries(popup,0);
            dpi=ExplorerRingNative.GetDpiForWindow(ExplorerRingNative.GetForegroundWindow())/96f;
            if(dpi<=0) dpi=1;
            row=(int)(44*dpi); pad=(int)(10*dpi); work=Screen.FromPoint(point).WorkingArea;
            painter=CreateOverlay(); painter.scale=dpi; painter.LoadFluentPalette();
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; TopMost=true; ShowInTaskbar=false;
            int width=Math.Min((int)(360*dpi),work.Width-2*pad);
            int height=Math.Min((entries.Count+3)*row+2*pad,work.Height-2*pad);
            Bounds=new Rectangle(Math.Max(work.Left+pad,Math.Min(point.X,work.Right-pad-width)),Math.Max(work.Top+pad,Math.Min(point.Y,work.Bottom-pad-height)),width,height);
            shown=Math.Max(1,(height-2*pad)/row-3); firstRow=pad+row;
            Shown+=delegate { backdrop=new AcrylicSurface(Bounds,painter.darkMaterial); backdrop.Show(); painter.materialBlur=backdrop.EnableMaterial(); ExplorerRingNative.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13); Render(); };
            MouseMove+=delegate(object s,MouseEventArgs e) { hot=HitRow(e.Location); Render(); };
            MouseDown+=delegate(object s,MouseEventArgs e) { pressed=HitRow(e.Location); };
            MouseUp+=delegate(object s,MouseEventArgs e) { lastPoint=e.Location; int hit=HitRow(e.Location); if(hit==pressed) SelectRow(hit); pressed=-1; };
            MouseWheel+=delegate(object s,MouseEventArgs e) { offset=Math.Max(0,Math.Min(Math.Max(0,entries.Count-shown),offset+(e.Delta<0 ? 3 : -3))); Render(); };
        }
        internal void SampleContrast()
        {
            // Explicit live-only path; hidden tests never capture the desktop.
            painter.Bounds=Bounds; painter.center=new PointF(Width/2,Height/2);
            painter.radius=Math.Min(Width,Height)/2; painter.inner=0;
            painter.SampleRingContrast();
            if(painter.contrastReady) painter.darkMaterial=painter.whiteInk[6];
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08080080; return p; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { Render(); }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x21) { m.Result=new IntPtr(3); return; }
            if(m.Msg==0x2cc) { m.Result=new IntPtr(0x10001); return; }
            if(m.Msg==0x7b) { m.Result=IntPtr.Zero; return; }
            if(m.Msg==0x246 || m.Msg==0x245 || m.Msg==0x247) {
                uint id=unchecked((uint)m.WParam.ToInt64())&65535; PointerInfo info;
                if(GetPointerInfo(id,out info) && info.Type==2) {
                    var point=PointToClient(new Point(info.Pixel.X,info.Pixel.Y)); lastPoint=point; int hit=HitRow(point);
                    if((info.Flags&0x8000)!=0) { contact=0; pressed=-1; }
                    else if(m.Msg==0x246) { if(contact!=0) { Close(); return; } contact=id; pressed=hit; hot=hit; }
                    else if(contact==id && m.Msg==0x247) { contact=0; int prior=pressed; pressed=-1; if(prior==hit) SelectRow(hit); }
                    else if(contact==id) hot=hit;
                    if(!IsDisposed) Render(); m.Result=IntPtr.Zero; return;
                }
            }
            base.WndProc(ref m);
        }
        private int HitRow(Point p)
        {
            if(p.X<pad || p.X>=Width-pad || p.Y<pad) return -10;
            int n=(p.Y-pad)/row;
            if(n==0) return -1; // back/close
            if(n<=shown) return offset+n-1<entries.Count ? offset+n-1 : -10;
            if(n==shown+1) return -2; // paging, left/right halves
            if(n==shown+2) return -3; // native fallback
            return -10;
        }
        internal void TestRows()
        {
            if(HitRow(new Point(pad+1,pad+1))!=-1 || HitRow(new Point(pad+1,firstRow+1))!=0 ||
               HitRow(new Point(-1,-1))!=-10) throw new Exception("Touch menu row geometry failed");
            if(Left<work.Left || Top<work.Top || Right>work.Right || Bottom>work.Bottom)
                throw new Exception("Touch menu clips work area");
            int oldOffset=offset;
            lastPoint=new Point(Width-1,0); SelectRow(-2);
            if(offset!=Math.Min(Math.Max(0,entries.Count-shown),shown)) throw new Exception("Forward page failed");
            lastPoint=new Point(0,0); SelectRow(-2);
            if(offset!=0) throw new Exception("Back page failed");
            offset=oldOffset;
            if(entries.Count>0 && entries[0].Children!=null) {
                var original=entries;
                SelectRow(0);
                if(parents.Count!=1 || entries!=original[0].Children) throw new Exception("Local submenu did not open");
                SelectRow(-1);
                if(parents.Count!=0 || entries!=original) throw new Exception("Local submenu back failed");
                bool refused=false;
                try { shell.Execute(entries[0]); } catch(InvalidOperationException) { refused=true; }
                if(!refused) throw new Exception("Local submenu accepted as Shell numeric command");
            }
        }
        private void SelectRow(int n)
        {
            if(n==-1) {
                if(parents.Count==0) { Close(); return; }
                var p=parents.Pop(); entries=p.Item1; offset=p.Item2;
            } else if(n==-2) {
                offset=Math.Max(0,Math.Min(Math.Max(0,entries.Count-shown),offset+(lastPoint.X<Width/2 ? -shown : shown)));
            } else if(n==-3) { Original=true; Close(); return; }
            else if(n>=0 && n<entries.Count) {
                var e=entries[n]; if(!e.Enabled || e.Separator) return;
                if(e.Unsupported) { Original=true; Close(); return; }
                if(e.Sub!=IntPtr.Zero || e.Children!=null) {
                    if(parents.Count>=8) return;
                    var next=e.Children ?? shell.Entries(e.Sub,e.Position);
                    parents.Push(Tuple.Create(entries,offset)); offset=0; entries=next;
                } else { Choice=e; Close(); return; }
            }
            hot=-1; Render();
        }
        internal void Render()
        {
            if(!IsHandleCreated || IsDisposed) return;
            using(var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb)) using(var g=Graphics.FromImage(bitmap)) {
                Color surface=SystemInformation.HighContrast ? SystemColors.Window : painter.darkMaterial ? Color.FromArgb(38,38,38) : Color.FromArgb(243,243,243);
                Color text=SystemInformation.HighContrast ? SystemColors.WindowText : painter.darkMaterial ? Color.White : Color.FromArgb(25,25,25);
                g.Clear(Color.FromArgb(painter.materialBlur ? 24 : 255,surface));
                // Stable contrast for the large list even over mixed wallpaper.
                using(var b=new SolidBrush(Color.FromArgb(200,surface))) g.FillRectangle(b,pad,pad,Width-2*pad,Height-2*pad);
                DrawRow(g,parents.Count==0 ? "Ещё · закрыть" : "‹ Назад",pad,text,false);
                for(int i=0;i<shown && offset+i<entries.Count;i++) {
                    var e=entries[offset+i]; int y=firstRow+i*row;
                    if(e.Separator) { using(var pen=new Pen(Color.FromArgb(60,text))) g.DrawLine(pen,pad,y+row/2,Width-pad,y+row/2); continue; }
                    if(hot==offset+i && e.Enabled) using(var b=new SolidBrush(Color.FromArgb(70,painter.accent))) g.FillRectangle(b,pad,y,Width-2*pad,row);
                    string name=(e.Checked ? "✓ " : "")+(String.IsNullOrEmpty(e.Text) ? "Нестандартный пункт" : e.Text.Split('\t')[0])+(e.Sub!=IntPtr.Zero || e.Children!=null ? "  ›" : "")+(e.Unsupported ? " …" : "");
                    DrawRow(g,name,y,e.Enabled ? text : SystemColors.GrayText,false);
                }
                DrawRow(g,"‹ Страница   "+(offset+1)+"–"+Math.Min(entries.Count,offset+shown)+" / "+entries.Count+"   Страница ›",firstRow+shown*row,text,true);
                DrawRow(g,"Открыть штатное меню…",firstRow+(shown+1)*row,text,true);
                using(var pen=new Pen(Color.FromArgb(90,text))) g.DrawRectangle(pen,0,0,Width-1,Height-1);
                PresentBitmap(Handle,Location,bitmap);
            }
        }
        private void DrawRow(Graphics g,string value,int y,Color color,bool small)
        {
            using(var f=new Font("Segoe UI",(small ? 12 : 16)*dpi,FontStyle.Regular,GraphicsUnit.Pixel)) using(var b=new SolidBrush(color))
            using(var format=new StringFormat { LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter })
                g.DrawString(value,f,b,new RectangleF(pad*2,y,Width-pad*4,row),format);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { if(backdrop!=null) { backdrop.Close(); backdrop.Dispose(); backdrop=null; } if(painter!=null) { painter.log.Dispose(); painter.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
