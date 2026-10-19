using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class ExplorerRingNative
{
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        internal uint Tick, Thread, Stage, Message;
        internal ulong Hwnd, Extra;
        internal uint SourceDevice, SourceOrigin, Id, Type, Flags, PointerError;
        internal int X, Y;
        internal uint OsTime, Reserved;
    }
    [DllImport("ExplorerBridge-v04.dll")] internal static extern uint BeginBridge(uint thread, ulong root, ulong view);
    [DllImport("ExplorerBridge-v04.dll")] internal static extern uint BeginObservation(uint thread);
    [DllImport("ExplorerBridge-v04.dll")] internal static extern ulong ResolveBridgeView(ulong root, ulong hwnd);
    [DllImport("ExplorerBridge-v04.dll")] internal static extern uint BridgePulse(uint enabled);
    [DllImport("ExplorerBridge-v04.dll")] internal static extern void EndObservation();
    [DllImport("ExplorerBridge-v04.dll")] internal static extern uint DrainObservation([Out] Event[] events, uint capacity, out uint dropped);
    [DllImport("ExplorerBridge-v04.dll")] internal static extern uint RetargetBridge(uint thread, ulong root, ulong view);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr lp);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x,int y,int cx,int cy,uint flags);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    internal static string ClassOf(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd,b,b.Capacity); return b.ToString(); }
}

internal sealed class ExplorerRing : Form
{
    private readonly Button arm = new Button(), stop = new Button();
    private readonly Label status = new Label();
    private readonly Timer timer = new Timer();
    private readonly StreamWriter log;
    private readonly RingLab overlay;
    private readonly ExplorerRingNative.Event[] events = new ExplorerRingNative.Event[128];
    private bool waiting, running, stopped, stopping;
    private DateTime armAt, stopAt, ringAt, stoppingAt;
    private IntPtr root;
    private uint dropped, oldDropped, nativeOwned;
    private bool cancelledContact;
    private string result = "";
    private ExplorerRename target, pendingRename;
    private DateTime renameAt;

    internal ExplorerRing()
    {
        Text = "Ring menu 0.4 — исправление после перемещения";
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(880, 540);
        StartPosition = FormStartPosition.CenterScreen;
        var intro = new Label(); intro.SetBounds(24,18,830,340);
        intro.Text = "Кольцо в папках и на рабочем столе · тест переименования\n\n" +
            "1. Закройте предыдущие диагностические программы.\n" +
            "2. Откройте папку с ненужным тестовым файлом.\n" +
            "3. Нажмите «Подключить» и переключитесь в папку за 8 секунд.\n" +
            "4. Удержите файл: через 650 мс ожидается кольцо.\n" +
            "5. В центре: ведите в сектор и отпустите. У края: отпустите,\n" +
            "    затем коснитесь сектора. «Переименовать» — настоящее.\n\n" +
            "Проверяем: нет второго штатного меню и перетаскивания;\n" +
            "короткий тап, быстрый drag, ПКМ мыши, Shift+F10 не сломаны.\n\n" +
            "DLL временно меняет обработку сенсорного жеста. Если что-то\n" +
            "пошло не так — отпустите палец и нажмите «Остановить».\n" +
            "Остальные секторы — имитация. По одному файлу за раз.\n" +
            "Автоотключение через 3 минуты. Важные файлы не используйте.";
        Controls.Add(intro);
        arm.Text = "Подключить через 8 секунд"; arm.SetBounds(24,370,380,48);
        arm.Click += delegate { arm.Enabled=false; waiting=true; armAt=DateTime.UtcNow.AddSeconds(8); timer.Start(); };
        Controls.Add(arm);
        stop.Text="Остановить"; stop.SetBounds(430,370,270,48); stop.Enabled=false;
        stop.Click += delegate { RequestStop("User stop"); }; Controls.Add(stop);
        status.SetBounds(24,440,830,85); status.Text="Не подключено."; Controls.Add(status);
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"explorer-ring-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
        log=new StreamWriter(path,false,new UTF8Encoding(true)); log.AutoFlush=true;
        Write("START version=0.4 dragRecovery=true rename=true desktop=true dynamicView=true session="+Process.GetCurrentProcess().SessionId+" abi="+Marshal.SizeOf(typeof(ExplorerRingNative.Event)));
        overlay=RingLab.CreateOverlay();
        overlay.Selected += delegate(int sector) {
            if(sector==3 && target!=null) { pendingRename=target; renameAt=DateTime.UtcNow.AddMilliseconds(300); result="Ожидается редактор имени…"; }
            else { result=sector==3 ? "Нет однозначного файла — действие отменено." : "Имитация: сектор "+sector; }
            Write("RESULT sector="+sector+" renameQueued="+(pendingRename!=null)); target=null;
        };
        overlay.OverlayCancelled += delegate(string why) { cancelledContact=true; target=null; pendingRename=null; Write("CANCEL "+why); };
        timer.Interval=20; timer.Tick += Tick;
        FormClosing += delegate(object sender,FormClosingEventArgs e) {
            if (running && nativeOwned!=0) { e.Cancel=true; RequestStop("Close deferred until release"); }
        };
        FormClosed += delegate { StopNow("Close"); overlay.Close(); overlay.Dispose(); log.Dispose(); };
    }
    private void Write(string s) { log.WriteLine(DateTime.Now.ToString("O")+" "+s); }
    private void Tick(object sender,EventArgs args)
    {
        try { TickCore(); }
        catch(Exception e) { Write("ERROR "+e.GetType().Name); RequestStop("Error"); }
    }
    private void TickCore()
    {
        if(waiting)
        {
            status.Text="Переключитесь в Проводник: "+Math.Max(0,(int)Math.Ceiling((armAt-DateTime.UtcNow).TotalSeconds))+" с.";
            if(DateTime.UtcNow<armAt) return;
            waiting=false; root=ExplorerRingNative.GetForegroundWindow();
            uint pid, thread; IntPtr view;
            if(!FindTarget(root,out view,out thread,out pid))
            { status.Text="Нужно активное окно папки или рабочий стол. Повторите."; arm.Enabled=true; timer.Stop(); return; }
            uint error=ExplorerRingNative.BeginBridge(thread,unchecked((ulong)root.ToInt64()),unchecked((ulong)view.ToInt64()));
            Write("ARM pid="+pid+" thread="+thread+" root=0x"+root.ToInt64().ToString("X")+" view=0x"+view.ToInt64().ToString("X")+" error="+error);
            if(error!=0) { status.Text="Подключение не удалось: "+error+". Закройте программу."; stopped=true; timer.Stop(); return; }
            running=true; stop.Enabled=true; stopAt=DateTime.UtcNow.AddMinutes(3);
            System.Media.SystemSounds.Asterisk.Play();
        }
        if(!running) return;
        IntPtr foregroundRoot=ExplorerRingNative.GetForegroundWindow();
        bool foreground=foregroundRoot==root;
        if(!foreground && nativeOwned==0 && !overlay.OverlayVisible && pendingRename==null && !stopping) {
            IntPtr nextView; uint nextThread,nextPid;
            if(FindTarget(foregroundRoot,out nextView,out nextThread,out nextPid)) {
                uint error=ExplorerRingNative.RetargetBridge(nextThread,unchecked((ulong)foregroundRoot.ToInt64()),unchecked((ulong)nextView.ToInt64()));
                Write("RETARGET error="+error+" thread="+nextThread);
                if(error!=0) { RequestStop("Retarget failed"); return; }
                root=foregroundRoot; foreground=true;
            }
        }
        nativeOwned=ExplorerRingNative.BridgePulse(!stopping && foreground && !overlay.OverlayVisible && pendingRename==null ? 1U : 0U);
        Drain();
        if(dropped!=oldDropped) { oldDropped=dropped; cancelledContact=true; overlay.DismissOverlay("Queue dropped input",true); RequestStop("Queue overflow"); }
        if(DateTime.UtcNow>=stopAt && !stopping) RequestStop("Three minute timeout");
        if(pendingRename!=null && !stopping && nativeOwned==0 && DateTime.UtcNow>=renameAt) {
            var command=pendingRename; pendingRename=null;
            try { command.BeginEdit(); result="Запрошен штатный редактор имени."; Write("RENAME_EDIT_REQUESTED"); }
            catch(Exception e) { result="Переименование отменено: цель не подтверждена."; Write("RENAME_REFUSED type="+e.GetType().Name); }
        }
        if(overlay.OverlayVisible && (!foreground || !ExplorerRingNative.IsWindow(root) || DateTime.UtcNow-ringAt>TimeSpan.FromSeconds(20)))
        { cancelledContact=true; overlay.DismissOverlay("Focus/window/20s timeout",true); }
        if(stopping && nativeOwned==0) StopNow("Contact drained");
        else if(stopping && DateTime.UtcNow-stoppingAt>TimeSpan.FromSeconds(2)) StopNow("Forced unhook after 2s; contact may still be down");
        if(running) status.Text=(stopping ? "Отключение: отпустите палец." : "Подключено. "+result)+"\nПропущено: "+dropped+". Осталось "+Math.Max(0,(int)(stopAt-DateTime.UtcNow).TotalSeconds)+" с.";
    }
    private void Drain()
    {
        uint n;
        do {
            n=ExplorerRingNative.DrainObservation(events,(uint)events.Length,out dropped);
            for(int i=0;i<n;i++)
            {
                var e=events[i];
                Write("EVENT tick="+e.Tick+" stage="+e.Stage+" msg=0x"+e.Message.ToString("X4")+" hwnd=0x"+e.Hwnd.ToString("X")+
                    " class="+ExplorerRingNative.ClassOf(new IntPtr(unchecked((long)e.Hwnd)))+" id="+e.Id+" type="+e.Type+
                    " flags=0x"+e.Flags.ToString("X")+" device="+e.SourceDevice+" origin="+e.SourceOrigin+
                    " extra=0x"+e.Extra.ToString("X")+" x="+e.X+" y="+e.Y+" error="+e.PointerError);
                if(e.Stage!=3) continue;
                var point=new Point(e.X,e.Y);
                if(e.Message==1) {
                    cancelledContact=stopping || ExplorerRingNative.GetForegroundWindow()!=root; ringAt=DateTime.UtcNow;
                    target=null;
                    if(!cancelledContact) {
                        ulong view=ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),e.Hwnd);
                        try { target=ExplorerRename.Capture(root,new IntPtr(unchecked((long)view)),point); Write("TARGET_CAPTURED"); }
                        catch(Exception ex) { Write("TARGET_REFUSED type="+ex.GetType().Name); result="Под пальцем нет однозначной файловой цели."; }
                        overlay.BeginOverlay(e.Id,point,ExplorerRingNative.GetDpiForWindow(root));
                    }
                    Write("TAKEOVER cleanup=WM_POINTERCAPTURECHANGED experimental=true");
                } else if(e.Message==2 && !cancelledContact) overlay.ForwardMove(e.Id,point,(e.Flags&0x8000)!=0);
                else if(e.Message==3 && !cancelledContact) overlay.ForwardUp(e.Id,point,(e.Flags&0x8000)!=0);
                else if(e.Message==4) { cancelledContact=true; overlay.DismissOverlay("Second contact",true); }
                else if(e.Message==6) Write("VIEW_REBOUND hwnd=0x"+e.Hwnd.ToString("X"));
                else if(e.Message==7) Write("UNOWNED_CONTACT_RECOVERED id="+e.Id+" reason=0x"+e.Flags.ToString("X"));
            }
        } while(n==events.Length);
    }
    private static bool FindTarget(IntPtr candidate,out IntPtr view,out uint thread,out uint pid)
    {
        view=IntPtr.Zero; thread=ExplorerRingNative.GetWindowThreadProcessId(candidate,out pid);
        string cls=ExplorerRingNative.ClassOf(candidate);
        if(cls!="CabinetWClass" && cls!="ExploreWClass" && cls!="Progman" && cls!="WorkerW") return false;
        try { using(var p=Process.GetProcessById((int)pid)) if(!p.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase)) return false; }
        catch { return false; }
        IntPtr found=IntPtr.Zero;
        ExplorerRingNative.EnumChildWindows(candidate,delegate(IntPtr h,IntPtr l) {
            if(ExplorerRingNative.ClassOf(h)=="SHELLDLL_DefView" && ExplorerRingNative.IsWindowVisible(h)) found=h;
            return true;
        },IntPtr.Zero);
        view=found; return view!=IntPtr.Zero;
    }
    private void RequestStop(string reason)
    {
        if(stopped) return;
        Write("STOP_REQUEST "+reason); waiting=false; target=null; pendingRename=null;
        if(!stopping) stoppingAt=DateTime.UtcNow;
        stopping=true;
        overlay.DismissOverlay("Stopping",false); cancelledContact=true;
        nativeOwned=ExplorerRingNative.BridgePulse(0);
        if(nativeOwned==0) StopNow(reason);
    }
    private void StopNow(string reason)
    {
        if(stopped) return;
        ExplorerRingNative.EndObservation();
        if(running) Drain();
        stopped=true; running=false; waiting=false; timer.Stop();
        arm.Enabled=stop.Enabled=false;
        Write("STOP "+reason+" dropped="+dropped);
        status.Text="Отключено. Журнал сохранён. "+result;
    }
    private sealed class SelfTestWindow : Form
    {
        protected override void WndProc(ref Message m)
        { if(m.Msg==0x2cc) { m.Result=new IntPtr(0x1234); return; } base.WndProc(ref m); }
    }
    private static class ViewTest
    {
        private delegate IntPtr WindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass
        {
            internal uint Style; internal WindowProc Proc; internal int ClassExtra,WindowExtra;
            internal IntPtr Instance,Icon,Cursor,Background;
            internal string Menu,Name;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClass(ref WindowClass wc);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string name,IntPtr instance);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        private static readonly WindowProc proc=DefWindowProc;
        private static ulong U(IntPtr h) { return unchecked((ulong)h.ToInt64()); }
        private static IntPtr Child(string cls,IntPtr root,IntPtr instance)
        {
            var h=CreateWindowEx(0,cls,"",0x40000000,0,0,10,10,root,IntPtr.Zero,instance,IntPtr.Zero);
            if(h==IntPtr.Zero) throw new Exception("Create test child failed="+Marshal.GetLastWin32Error());
            return h;
        }
        internal static void Run()
        {
            IntPtr instance=GetModuleHandle(null);
            var wc=new WindowClass { Proc=proc,Instance=instance,Name="SHELLDLL_DefView" };
            if(RegisterClass(ref wc)==0) throw new Exception("Register isolated view class failed="+Marshal.GetLastWin32Error());
            try {
                using(var root=new Form()) using(var other=new Form()) {
                    var r=root.Handle; var o=other.Handle;
                    var nav=Child("STATIC",r,instance);
                    var first=Child("SHELLDLL_DefView",r,instance);
                    var firstLeaf=Child("STATIC",first,instance);
                    if(ExplorerRingNative.ResolveBridgeView(U(r),U(firstLeaf))!=U(first)) throw new Exception("Initial view not resolved");
                    if(ExplorerRingNative.ResolveBridgeView(U(r),U(nav))!=0) throw new Exception("Navigation accepted");
                    // New view exists before destruction to guarantee a distinct handle.
                    var second=Child("SHELLDLL_DefView",r,instance);
                    var secondLeaf=Child("STATIC",second,instance);
                    DestroyWindow(first);
                    if(ExplorerRingNative.ResolveBridgeView(U(r),U(secondLeaf))!=U(second)) throw new Exception("Recreated view not resolved");
                    var foreign=Child("SHELLDLL_DefView",o,instance);
                    if(ExplorerRingNative.ResolveBridgeView(U(r),U(foreign))!=0) throw new Exception("Other root accepted");
                    if(ExplorerRingNative.ResolveBridgeView(U(r),U(r))!=0) throw new Exception("Root accepted as file view");
                    if(ExplorerRingNative.ResolveBridgeView(U(r),0)!=0) throw new Exception("Null accepted");
                }
            } finally { UnregisterClass("SHELLDLL_DefView",instance); }
        }
    }
    private static int SelfTest()
    {
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"explorer-bridge-self-test.log");
        try {
            ExplorerRename.SelfTest();
            ViewTest.Run();
            using(var f=new SelfTestWindow()) {
                var h=f.Handle;
                uint thread=ExplorerRingNative.GetCurrentThreadId();
                if(ExplorerRingNative.BeginBridge(thread,unchecked((ulong)h.ToInt64()),unchecked((ulong)h.ToInt64()))!=87)
                    throw new Exception("Same-process bridge target accepted");
                if(ExplorerRingNative.RetargetBridge(0,0,0)!=87) throw new Exception("Invalid retarget accepted");
                uint error=ExplorerRingNative.BeginObservation(thread);
                if(error!=0) throw new Exception("Native hooks error="+error);
                if(ExplorerRingNative.SendMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero)!=new IntPtr(0x1234)) throw new Exception("Changed response");
                ExplorerRingNative.PostMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero);
                Application.DoEvents();
                var items=new ExplorerRingNative.Event[128]; uint lost;
                uint n=ExplorerRingNative.DrainObservation(items,128,out lost);
                bool get=false,call=false;
                for(int i=0;i<n;i++) { if(items[i].Message!=0x2cc) throw new Exception("Unexpected event"); get|=items[i].Stage==1; call|=items[i].Stage==2; }
                if(!get || !call || lost!=0) throw new Exception("Missing hooks or lost events");
                ExplorerRingNative.EndObservation();
                ExplorerRingNative.SendMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero);
                if(ExplorerRingNative.DrainObservation(items,128,out lost)!=0) throw new Exception("Events after unhook");
                File.WriteAllText(path,"PASS v0.4: rename matching/ambiguity/virtual-item/identity guards; invalid retarget rejected; initial/recreated view resolution; reject navigation/other-root/null; bridge DLL load; GET/CALL; shared queue; preserved response; same-process takeover refused; unhook. Events="+n+"\r\n");
                return 0;
            }
        } catch(Exception e) { File.WriteAllText(path,"FAIL: "+e+"\r\n"); return 1; }
        finally { ExplorerRingNative.EndObservation(); }
    }
    [STAThread] private static int Main(string[] args)
    {
        ExplorerRingNative.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length==1 && args[0]=="--self-test") return SelfTest();
        Application.Run(new ExplorerRing());
        return 0;
    }
}
