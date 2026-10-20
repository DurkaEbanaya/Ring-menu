using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
        var watch=System.Diagnostics.Stopwatch.StartNew();
        using(var menu=new TouchMoreMenu(shell,point))
        using(var shield=new OutsideVeil(Screen.FromPoint(point).WorkingArea)) {
            if(shell.Diagnostic!=null) shell.Diagnostic("MORE_TIMING stage=entriesLayout ms="+watch.ElapsedMilliseconds);
            menu.SampleContrast();
            shield.Touched+=delegate { menu.Close(); };
            shield.MouseUp+=delegate { menu.Close(); };
            shield.Show(); menu.Show();
            if(shell.Diagnostic!=null) shell.Diagnostic("MORE_TIMING stage=firstPresented ms="+watch.ElapsedMilliseconds);
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
        private LayeredFrame frame;
        private AcrylicSurface backdrop;
        private List<ShellCommands.Entry> entries;
        private readonly Stack<Tuple<List<ShellCommands.Entry>,int>> parents=new Stack<Tuple<List<ShellCommands.Entry>,int>>();
        private int scroll,firstRow,viewport,contentHeight,hot=-1,pressed=-10;
        private Point downPoint;
        private int downScroll;
        private bool dragging,beganInList;
        private uint contact;
        private readonly int row,pad,separator,header,footer;
        private readonly float dpi;
        internal ShellCommands.Entry Choice;
        internal bool Original;
        private readonly Rectangle work;
        internal TouchMoreMenu(ShellCommands shell,Point point)
        {
            this.shell=shell; entries=shell.Entries(shell.Menu,0);
            dpi=ExplorerRingNative.GetDpiForWindow(ExplorerRingNative.GetForegroundWindow())/96f;
            if(dpi<=0) dpi=1;
            row=(int)(32*dpi); pad=Math.Max(1,(int)(6*dpi)); separator=Math.Max(1,(int)(7*dpi));
            header=(int)(32*dpi); footer=(int)(28*dpi); work=Screen.FromPoint(point).WorkingArea;
            painter=CreateOverlay(); painter.scale=dpi; painter.LoadFluentPalette();
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; TopMost=true; ShowInTaskbar=false;
            int width=Math.Min((int)(320*dpi),work.Width-2*pad);
            Bounds=new Rectangle(Math.Max(work.Left+pad,Math.Min(point.X,work.Right-pad-width)),work.Top+pad,width,100);
            LayoutEntries();
            Top=Math.Max(work.Top+pad,Math.Min(point.Y,work.Bottom-pad-Height));
            Shown+=delegate {
                backdrop=new AcrylicSurface(Bounds,painter.darkMaterial); backdrop.Show(); painter.materialBlur=backdrop.EnableMaterial();
                ExplorerRingNative.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13); Render();
            };
            MouseMove+=delegate(object s,MouseEventArgs e) { if(pressed!=-10) MoveContact(e.Location); else hot=HitRow(e.Location); Render(); };
            MouseDown+=delegate(object s,MouseEventArgs e) { if(e.Button==MouseButtons.Left) { Capture=true; BeginContact(e.Location); } };
            MouseUp+=delegate(object s,MouseEventArgs e) { if(e.Button==MouseButtons.Left) { EndContact(e.Location); Capture=false; } };
            MouseWheel+=delegate(object s,MouseEventArgs e) { ScrollTo(scroll+(e.Delta<0 ? 3*row : -3*row)); Render(); };
        }
        private int EntryHeight(ShellCommands.Entry e) { return e.Separator ? separator : row; }
        private void LayoutEntries()
        {
            contentHeight=0;
            foreach(var e in entries) contentHeight+=EntryHeight(e);
            Height=Math.Min(contentHeight+header+footer+2*pad,work.Height-2*pad);
            Top=Math.Max(work.Top+pad,Math.Min(Top,work.Bottom-pad-Height));
            firstRow=pad+header; viewport=Math.Max(1,Height-firstRow-footer-pad);
            ScrollTo(scroll);
            if(backdrop!=null) backdrop.Bounds=Bounds;
        }
        private void ScrollTo(int value) { scroll=Math.Max(0,Math.Min(Math.Max(0,contentHeight-viewport),value)); }
        internal void SampleContrast()
        {
            // Live-only sample before material/shield; hidden tests never capture.
            painter.Bounds=Bounds; painter.center=new PointF(Width/2,Height/2);
            painter.radius=Math.Min(Width,Height)/2; painter.inner=0;
            painter.SampleRingContrast();
            if(painter.contrastReady) painter.darkMaterial=painter.whiteInk;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08080080; return p; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { Render(); }
        private void BeginContact(Point point)
        { downPoint=point; downScroll=scroll; dragging=false; beganInList=point.Y>=firstRow && point.Y<firstRow+viewport; pressed=HitRow(point); hot=pressed; }
        private void MoveContact(Point point)
        {
            if(!beganInList) return;
            if(Math.Abs(point.Y-downPoint.Y)>7*dpi || Math.Abs(point.X-downPoint.X)>7*dpi) dragging=true;
            if(dragging) { ScrollTo(downScroll+downPoint.Y-point.Y); hot=-10; }
            else hot=HitRow(point);
        }
        private void EndContact(Point point)
        {
            MoveContact(point);
            int prior=pressed; pressed=-10;
            if(!dragging && prior==HitRow(point)) SelectRow(prior);
            if(!IsDisposed) Render();
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x21) { m.Result=new IntPtr(3); return; }
            if(m.Msg==0x2cc) { m.Result=new IntPtr(0x10001); return; }
            if(m.Msg==0x7b) { m.Result=IntPtr.Zero; return; }
            if(m.Msg==0x24c) { contact=0; pressed=-10; dragging=true; m.Result=IntPtr.Zero; return; }
            if(m.Msg==0x246 || m.Msg==0x245 || m.Msg==0x247) {
                uint id=unchecked((uint)m.WParam.ToInt64())&65535; PointerInfo info;
                if(GetPointerInfo(id,out info) && info.Type==2) {
                    var point=PointToClient(new Point(info.Pixel.X,info.Pixel.Y));
                    if((info.Flags&0x8000)!=0) { contact=0; pressed=-10; dragging=true; }
                    else if(m.Msg==0x246) { if(contact!=0) { Close(); return; } contact=id; BeginContact(point); }
                    else if(contact==id && m.Msg==0x247) { contact=0; EndContact(point); }
                    else if(contact==id) MoveContact(point);
                    if(!IsDisposed) Render(); m.Result=IntPtr.Zero; return;
                }
            }
            base.WndProc(ref m);
        }
        private int HitRow(Point p)
        {
            if(p.X<pad || p.X>=Width-pad || p.Y<pad || p.Y>=Height-pad) return -10;
            if(p.Y<firstRow) return -1;
            if(p.Y>=firstRow+viewport) return -3;
            int y=firstRow-scroll;
            for(int i=0;i<entries.Count;i++) {
                int h=EntryHeight(entries[i]);
                if(p.Y>=y && p.Y<y+h) return entries[i].Separator ? -10 : i;
                y+=h;
            }
            return -10;
        }
        internal void TestRows()
        {
            int first=entries.FindIndex(delegate(ShellCommands.Entry e) { return !e.Separator; });
            int y=firstRow; for(int i=0;i<first;i++) y+=EntryHeight(entries[i]);
            if(HitRow(new Point(pad+1,pad+1))!=-1 || (first>=0 && HitRow(new Point(pad+1,y+1))!=first) ||
               HitRow(new Point(-1,-1))!=-10) throw new Exception("Compact menu row geometry failed");
            if(Left<work.Left || Top<work.Top || Right>work.Right || Bottom>work.Bottom) throw new Exception("Touch menu clips work area");
            if(entries.Count>0 && entries[0].Children!=null) {
                var original=entries; SelectRow(0);
                if(parents.Count!=1 || entries!=original[0].Children) throw new Exception("Local submenu did not open");
                SelectRow(-1);
                if(parents.Count!=0 || entries!=original) throw new Exception("Local submenu back failed");
                bool refused=false; try { shell.Execute(entries[0]); } catch(InvalidOperationException) { refused=true; }
                if(!refused) throw new Exception("Local submenu accepted as Shell numeric command");
            }
            var saved=entries;
            try {
                entries=new List<ShellCommands.Entry>();
                for(int i=0;i<25;i++) entries.Add(new ShellCommands.Entry { Text="Test",Enabled=true,LocalAction="test" });
                scroll=0; LayoutEntries();
                if(work.Height>=900*dpi && contentHeight>viewport) throw new Exception("25 compact commands should fit a 900-DIP work area");
                entries.Add(new ShellCommands.Entry { Separator=true });
                if(EntryHeight(entries[25])>=row) throw new Exception("Separator wastes a command row");
                entries.Clear();
                for(int i=0;i<100;i++) entries.Add(new ShellCommands.Entry { Text="Test",Enabled=true,LocalAction="test" });
                scroll=0; LayoutEntries();
                BeginContact(new Point(pad+10,firstRow+row/2)); MoveContact(new Point(pad+10,firstRow-3*row));
                EndContact(new Point(pad+10,firstRow-3*row));
                if(scroll<=0 || Choice!=null || !dragging) throw new Exception("Swipe selected a command or failed to scroll");
                ScrollTo(Int32.MaxValue);
                if(scroll!=contentHeight-viewport) throw new Exception("Scroll end clamp failed");
                if(HitRow(new Point(pad+1,firstRow+viewport-1))!=99) throw new Exception("Last scrolled item unreachable");
                ScrollTo(-1); if(scroll!=0) throw new Exception("Scroll start clamp failed");
                using(var f=new Font("Segoe UI",13*dpi,FontStyle.Regular,GraphicsUnit.Pixel))
                    if(f.Name!="Segoe UI") throw new Exception("Segoe UI font unavailable");
            } finally { entries=saved; scroll=0; LayoutEntries(); }
        }
        private void SelectRow(int n)
        {
            if(n==-1) {
                if(parents.Count==0) { Close(); return; }
                var p=parents.Pop(); entries=p.Item1; scroll=p.Item2;
            } else if(n==-3) { Original=true; Close(); return; }
            else if(n>=0 && n<entries.Count) {
                var e=entries[n]; if(!e.Enabled || e.Separator) return;
                if(e.Unsupported) { Original=true; Close(); return; }
                if(e.Sub!=IntPtr.Zero || e.Children!=null) {
                    if(parents.Count>=8) return;
                    var next=e.Children ?? shell.Entries(e.Sub,e.Position);
                    parents.Push(Tuple.Create(entries,scroll)); scroll=0; entries=next;
                } else { Choice=e; Close(); return; }
            }
            LayoutEntries(); hot=-1; Render();
        }
        private static string IconFor(ShellCommands.Entry e)
        {
            string verb=(e.Verb ?? "").ToLowerInvariant(),name=e.Text ?? "";
            if(e.Checked) return "\uE73E";
            if(e.Children!=null) return name=="Вид" ? "\uE8A9" : "\uE8CB";
            if(e.LocalAction!=null) return e.LocalAction.StartsWith("view:") ? "\uE8A9" : "\uE8CB";
            if(verb=="open" || verb=="explore") return "\uE8E5";
            if(verb=="copy") return "\uE8C8"; if(verb=="cut") return "\uE8C6";
            if(verb=="paste" || verb=="pastelink") return "\uE77F";
            if(verb=="delete") return "\uE74D"; if(verb=="rename") return "\uE8AC";
            if(verb=="properties") return "\uE946"; if(verb=="refresh") return "\uE72C";
            if(verb=="new" || verb=="newfolder") return "\uE8F4";
            if(verb=="sendto") return "\uE724"; if(verb=="print") return "\uE749";
            if(verb=="runas") return "\uEA18";
            return e.Sub!=IntPtr.Zero ? "\uE8F1" : "\uE8A5";
        }
        internal void Render()
        {
            if(!IsHandleCreated || IsDisposed) return;
            if(frame==null || frame.Bitmap.Size!=Size) { if(frame!=null) frame.Dispose(); frame=new LayeredFrame(Width,Height); }
            var g=frame.Graphics;
            {
                g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                Color surface=SystemInformation.HighContrast ? SystemColors.Window : painter.darkMaterial ? Color.FromArgb(38,38,38) : Color.FromArgb(243,243,243);
                Color text=painter.ReadableInk();
                g.Clear(Color.FromArgb(painter.materialBlur ? 24 : 255,surface));
                DrawGlyph(g,parents.Count==0 ? "\uE711" : "\uE72B",pad+16*dpi,pad+header/2,text,14);
                DrawLabel(g,parents.Count==0 ? "Ещё" : "Назад",new RectangleF(pad+34*dpi,pad,Width-pad*2-40*dpi,header),text,13);
                var state=g.Save(); g.SetClip(new Rectangle(pad,firstRow,Width-2*pad,viewport));
                int y=firstRow-scroll;
                for(int i=0;i<entries.Count;i++) {
                    var e=entries[i]; int h=EntryHeight(e);
                    if(y+h>firstRow && y<firstRow+viewport) {
                        if(e.Separator) { using(var pen=new Pen(Color.FromArgb(45,text))) g.DrawLine(pen,pad+34*dpi,y+h/2,Width-pad-10*dpi,y+h/2); }
                        else {
                            if(hot==i && e.Enabled) using(var b=new SolidBrush(Color.FromArgb(55,painter.accent))) g.FillRectangle(b,pad,y,Width-2*pad,h);
                            Color ink=e.Enabled ? text : Color.FromArgb(140,text);
                            DrawGlyph(g,IconFor(e),pad+16*dpi,y+h/2,ink,16);
                            string name=String.IsNullOrEmpty(e.Text) ? "Нестандартный пункт" : e.Text.Split('\t')[0];
                            DrawLabel(g,name,new RectangleF(pad+34*dpi,y,Width-2*pad-58*dpi,h),ink,13);
                            if(e.Sub!=IntPtr.Zero || e.Children!=null || e.Unsupported) DrawGlyph(g,"\uE76C",Width-pad-15*dpi,y+h/2,ink,10);
                        }
                    }
                    y+=h;
                }
                g.Restore(state);
                if(contentHeight>viewport) {
                    float thumb=Math.Max(20*dpi,viewport*(float)viewport/contentHeight);
                    float top=firstRow+(viewport-thumb)*scroll/Math.Max(1,contentHeight-viewport);
                    using(var b=new SolidBrush(Color.FromArgb(140,text))) g.FillRectangle(b,Width-pad-2*dpi,top,2*dpi,thumb);
                }
                int foot=firstRow+viewport;
                using(var pen=new Pen(Color.FromArgb(45,text))) g.DrawLine(pen,pad,foot,Width-pad,foot);
                DrawGlyph(g,"\uE8A7",pad+16*dpi,foot+footer/2,text,13);
                DrawLabel(g,"Штатное меню…",new RectangleF(pad+34*dpi,foot,Width-2*pad-40*dpi,footer),text,12);
                using(var pen=new Pen(Color.FromArgb(70,text))) g.DrawRectangle(pen,0,0,Width-1,Height-1);
                frame.Present(Handle,Location);
            }
        }
        private void DrawGlyph(Graphics g,string glyph,float x,float y,Color color,float size)
        {
            using(var f=new Font("Segoe MDL2 Assets",size*dpi,FontStyle.Regular,GraphicsUnit.Pixel)) using(var b=new SolidBrush(color))
            using(var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center }) g.DrawString(glyph,f,b,x,y,format);
        }
        private void DrawLabel(Graphics g,string value,RectangleF bounds,Color color,float size)
        {
            using(var f=new Font("Segoe UI",size*dpi,FontStyle.Regular,GraphicsUnit.Pixel)) using(var b=new SolidBrush(color))
            using(var format=new StringFormat { LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap })
                g.DrawString(value,f,b,bounds,format);
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) { if(frame!=null) { frame.Dispose(); frame=null; } if(backdrop!=null) { backdrop.Close(); backdrop.Dispose(); backdrop=null; } if(painter!=null) { painter.log.Dispose(); painter.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
