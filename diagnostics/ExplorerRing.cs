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
    [StructLayout(LayoutKind.Sequential)] internal struct GuiInfo
    {
        internal uint Size,Flags;
        internal IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;
        internal int Left,Top,Right,Bottom;
    }
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        internal uint Tick, Thread, Stage, Message;
        internal ulong Hwnd, Extra;
        internal uint SourceDevice, SourceOrigin, Id, Type, Flags, PointerError;
        internal int X, Y;
        internal uint OsTime, Reserved;
    }
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint BeginBridge(uint thread, ulong root, ulong view);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint BeginObservation(uint thread);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern ulong ResolveBridgeView(ulong root, ulong hwnd);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint BridgePulse(uint enabled);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern void EndObservation();
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint DrainObservation([Out] Event[] events, uint capacity, out uint dropped);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint RetargetBridge(uint thread, ulong root, ulong view);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint BridgeDiagnosticDrops();
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint SetBridgeNotify(ulong hwnd);
    [DllImport("ExplorerBridge-v013.dll")] internal static extern uint TestBridgeWake(ulong hwnd);
    [DllImport("user32.dll")] internal static extern uint MsgWaitForMultipleObjectsEx(uint count,IntPtr handles,uint milliseconds,uint mask,uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
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
    [DllImport("user32.dll")] internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool AppendMenu(IntPtr menu,uint flags,UIntPtr id,string text);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(IntPtr menu);
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
    private bool processing;
    private DateTime flushedAt;
    private DateTime armAt, stopAt, ringAt, stoppingAt;
    private IntPtr root;
    private uint dropped, oldDropped, nativeOwned;
    private bool cancelledContact;
    private uint diagnosticDropped;
    private string stopReason="";
    private string result = "";
    private ExplorerRename target, pendingCommand;
    private int pendingSector;
    private DateTime commandAt;
    private bool nativeMenu, morePending;
    private DateTime moreUntil;
    private int menuPopups;

    internal ExplorerRing()
    {
        Text = "Ring menu 0.13 — удержание 300 мс · быстрый Ещё";
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(880, 540);
        StartPosition = FormStartPosition.CenterScreen;
        var intro = new Label(); intro.SetBounds(24,18,830,340);
        intro.Text = "Кольцо в папках и на рабочем столе · настоящие действия\n\n" +
            "1. Закройте предыдущие диагностические программы.\n" +
            "2. Откройте папку с ненужным тестовым файлом.\n" +
            "3. Нажмите «Подключить» и переключитесь в папку за 8 секунд.\n" +
            "4. Удержите файл: через 300 мс ожидается кольцо.\n" +
            "5. В центре: ведите в сектор и отпустите. У края: отпустите,\n" +
            "    затем коснитесь сектора. На пустом месте — другое меню.\n\n" +
            "Проверяем: нет второго штатного меню и перетаскивания;\n" +
            "короткий тап, быстрый drag, ПКМ мыши, Shift+F10 не сломаны.\n\n" +
            "DLL временно меняет обработку сенсорного жеста. Если что-то\n" +
            "пошло не так — отпустите палец и нажмите «Остановить».\n" +
            "Копировать / вырезать → пустое место → вставить.\n" +
            "Автоотключение через 3 минуты. Важные файлы не используйте.";
        Controls.Add(intro);
        arm.Text = "Подключить через 8 секунд"; arm.SetBounds(24,370,380,48);
        arm.Click += delegate { arm.Enabled=false; waiting=true; armAt=DateTime.UtcNow.AddSeconds(8); timer.Start(); };
        Controls.Add(arm);
        stop.Text="Остановить"; stop.SetBounds(430,370,270,48); stop.Enabled=false;
        stop.Click += delegate { RequestStop("User stop"); }; Controls.Add(stop);
        status.SetBounds(24,440,830,85); status.Text="Не подключено."; Controls.Add(status);
        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"explorer-ring-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log");
        log=new StreamWriter(path,false,new UTF8Encoding(true)); log.AutoFlush=false;
        Write("START version=0.13 gestureMenuReuse=true eventWake=true cachedReveal=true cachedRendering=true uniformInk=true compactMore=true labelBackings=false holdMs=300 adaptiveContrast=true localShellMenu=true alphaTransport=DIB touchMore=true acrylicRequested=true reveal=true shellActions=true background=true queuePriority=true dragRecovery=true desktop=true dynamicView=true session="+Process.GetCurrentProcess().SessionId+" abi="+Marshal.SizeOf(typeof(ExplorerRingNative.Event)));
        overlay=RingLab.CreateOverlay();
        var warmWatch=Stopwatch.StartNew();
        RingLab.WarmRenderer();
        Write("RENDER_WARMUP ms="+warmWatch.ElapsedMilliseconds+" hidden=true");
        overlay.Selected += delegate(int sector) {
            if(sector>=0 && target!=null && target.Enabled[sector]) {
                pendingCommand=target; pendingSector=sector; commandAt=DateTime.UtcNow.AddMilliseconds(sector==5 ? 0 : 300); result="Ожидается действие Windows…";
            } else { result="Отменено — действие не выбрано или цель не подтверждена."; if(target!=null) target.Dispose(); }
            Write("RESULT sector="+sector+" commandQueued="+(pendingCommand!=null)); target=null;
        };
        overlay.OverlayCancelled += delegate(string why) { cancelledContact=true; ClearTargets(); Write("CANCEL "+why); };
        timer.Interval=10; timer.Tick += Tick;
        FormClosing += delegate(object sender,FormClosingEventArgs e) {
            if (running && nativeOwned!=0) { e.Cancel=true; RequestStop("Close deferred until release"); }
        };
        FormClosed += delegate { StopNow("Close"); overlay.Close(); overlay.Dispose(); log.Dispose(); };
    }
    private void Write(string s) { log.WriteLine(DateTime.Now.ToString("O")+" "+s); }
    private void ClearTargets()
    {
        if(target!=null) { target.Dispose(); target=null; }
        if(pendingCommand!=null) { pendingCommand.Dispose(); pendingCommand=null; }
    }
    protected override void WndProc(ref Message m)
    {
        // A wake is only a hint: the native queue is the sole source of input.
        // Guard nested modal pumps; the fallback timer drains any missed wake.
        if(m.Msg==0x8053) { if(running && timer.Enabled && !processing) Tick(this,EventArgs.Empty); m.Result=IntPtr.Zero; return; }
        base.WndProc(ref m);
    }
    private void Tick(object sender,EventArgs args)
    {
        if(processing) return;
        processing=true;
        try { TickCore(); if(DateTime.UtcNow-flushedAt>TimeSpan.FromMilliseconds(250)) { log.Flush(); flushedAt=DateTime.UtcNow; } }
        catch(Exception e) { Write("ERROR "+e.GetType().Name); RequestStop("Error"); }
        finally { processing=false; }
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
            if(ExplorerRingNative.SetBridgeNotify(unchecked((ulong)Handle.ToInt64()))!=0) { RequestStop("Notify registration failed"); return; }
            System.Media.SystemSounds.Asterisk.Play();
        }
        if(!running) return;
        uint menuPid;
        var gui=new ExplorerRingNative.GuiInfo { Size=(uint)Marshal.SizeOf(typeof(ExplorerRingNative.GuiInfo)) };
        if(!ExplorerRingNative.GetGUIThreadInfo(ExplorerRingNative.GetWindowThreadProcessId(root,out menuPid),ref gui)) {
            RequestStop("Menu state unavailable"); return;
        }
        // Menu telemetry is diagnostic and can be lost. Use the actual GUI
        // thread state for gating, not an ENTER/EXIT latch that could stick.
        nativeMenu=(gui.Flags & 0x1c)!=0;
        IntPtr foregroundRoot=ExplorerRingNative.GetForegroundWindow();
        bool foreground=foregroundRoot==root;
        if(!foreground && nativeOwned==0 && !nativeMenu && !morePending && !overlay.OverlayVisible && pendingCommand==null && !stopping) {
            IntPtr nextView; uint nextThread,nextPid;
            if(FindTarget(foregroundRoot,out nextView,out nextThread,out nextPid)) {
                uint error=ExplorerRingNative.RetargetBridge(nextThread,unchecked((ulong)foregroundRoot.ToInt64()),unchecked((ulong)nextView.ToInt64()));
                Write("RETARGET error="+error+" thread="+nextThread);
                if(error!=0) { RequestStop("Retarget failed"); return; }
                root=foregroundRoot; foreground=true;
            }
        }
        if(morePending && !nativeMenu && DateTime.UtcNow>moreUntil) { morePending=false; Write("MORE_UNCONFIRMED noMenuLoopObserved=true"); }
        nativeOwned=ExplorerRingNative.BridgePulse(!stopping && foreground && !nativeMenu && !morePending && !overlay.OverlayVisible && pendingCommand==null ? 1U : 0U);
        Drain();
        uint diagnosticNow=ExplorerRingNative.BridgeDiagnosticDrops();
        if(diagnosticNow!=diagnosticDropped) { diagnosticDropped=diagnosticNow; Write("DIAGNOSTIC_DROPPED count="+diagnosticDropped+" controlLost="+dropped); }
        if(dropped!=oldDropped) { oldDropped=dropped; cancelledContact=true; overlay.DismissOverlay("Queue dropped input",true); RequestStop("Control event lost"); }
        if(DateTime.UtcNow>=stopAt && !stopping) RequestStop("Three minute timeout");
        if(pendingCommand!=null && !stopping && nativeOwned==0 && DateTime.UtcNow>=commandAt) {
            var command=pendingCommand; pendingCommand=null;
            string action=command.ActionName(pendingSector);
            // Modal Windows dialogs pump messages. Disable re-entrant Tick and
            // all new takeovers until this explicit Shell command returns.
            ExplorerRingNative.BridgePulse(0); timer.Stop();
            try {
                if(action=="more") { morePending=true; moreUntil=DateTime.UtcNow.AddSeconds(5); menuPopups=0; }
                command.Diagnostic=Write;
                command.Execute(pendingSector,this);
                if(action=="more" && !command.UsedNativeMenu) { morePending=false; Write("TOUCH_MORE_CLOSED nativeFallback=false"); }
                else if(action=="more") moreUntil=DateTime.UtcNow.AddSeconds(5);
                result="Запрошено: "+command.Labels[pendingSector]+"."; Write("COMMAND_REQUESTED action="+action);
            }
            catch(Exception e) { if(action=="more") morePending=false; result="Действие отменено или недоступно: "+command.Labels[pendingSector]+"."; Write("COMMAND_REFUSED action="+action+" type="+e.GetType().Name); }
            finally { command.Dispose(); if(running) timer.Start(); }
        }
        if(overlay.OverlayVisible && (!foreground || !ExplorerRingNative.IsWindow(root) || DateTime.UtcNow-ringAt>TimeSpan.FromSeconds(20)))
        { cancelledContact=true; overlay.DismissOverlay("Focus/window/20s timeout",true); }
        if(stopping && nativeOwned==0) StopNow("Contact drained");
        else if(stopping && DateTime.UtcNow-stoppingAt>TimeSpan.FromSeconds(2)) StopNow("Forced unhook after 2s; contact may still be down");
        if(running) status.Text=(stopping ? "Отключение: отпустите палец." : "Подключено. "+result)+"\nПотери управления: "+dropped+"; диагностики: "+diagnosticDropped+". Осталось "+Math.Max(0,(int)(stopAt-DateTime.UtcNow).TotalSeconds)+" с.";
    }
    private void Drain()
    {
        uint n;
        do {
            n=ExplorerRingNative.DrainObservation(events,(uint)events.Length,out dropped);
            for(int i=0;i<n;i++)
            {
                var e=events[i];
                if(e.Stage==2 && (e.Message==0x211 || e.Message==0x212 || e.Message==0x117)) {
                    if(e.Message==0x211) { Write("NATIVE_MENU_ENTER more="+morePending); }
                    else if(e.Message==0x117 && morePending) { menuPopups++; Write("MORE_POPUP_INIT ordinal="+menuPopups+" position="+e.Id+" itemsBeforeHandler="+e.Reserved); }
                    else if(e.Message==0x212) { Write("NATIVE_MENU_EXIT more="+morePending+" popups="+menuPopups); morePending=false; }
                }
                // Continuous moves are consumed normally but not formatted or
                // flushed to disk one by one on the input/UI thread.
                bool move=(e.Stage==3 && (e.Message==2 || (e.Message==5 && e.Flags==0x200))) || e.Message==0x245;
                if(!move) Write("EVENT tick="+e.Tick+" stage="+e.Stage+" msg=0x"+e.Message.ToString("X4")+" hwnd=0x"+e.Hwnd.ToString("X")+
                    " class="+ExplorerRingNative.ClassOf(new IntPtr(unchecked((long)e.Hwnd)))+" id="+e.Id+" type="+e.Type+
                    " flags=0x"+e.Flags.ToString("X")+" device="+e.SourceDevice+" origin="+e.SourceOrigin+
                    " extra=0x"+e.Extra.ToString("X")+" x="+e.X+" y="+e.Y+" error="+e.PointerError);
                if(e.Stage!=3) continue;
                var point=new Point(e.X,e.Y);
                if(e.Message==1) {
                    cancelledContact=stopping || ExplorerRingNative.GetForegroundWindow()!=root; ringAt=DateTime.UtcNow;
                    ClearTargets();
                    if(!cancelledContact) {
                        ulong view=ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),e.Hwnd);
                        try {
                            target=ExplorerRename.Capture(root,new IntPtr(unchecked((long)view)),point);
                            Write("TARGET_CAPTURED context="+(target.Background ? "background" : "item")+" enabled="+String.Join(",",target.Enabled));
                            overlay.Configure(target.Labels,target.Enabled,target.Background);
                            overlay.BeginOverlay(e.Id,point,ExplorerRingNative.GetDpiForWindow(root));
                        }
                        catch(Exception ex) { cancelledContact=true; ClearTargets(); overlay.DismissOverlay("Target/material unavailable",false); Write("TARGET_REFUSED type="+ex.GetType().Name); result="Цель не подтверждена; действие не выполнено."; }
                    }
                    Write("TAKEOVER cleanup=WM_POINTERCAPTURECHANGED experimental=true");
                } else if(e.Message==2 && !cancelledContact) {
                    // Render newest adjacent movement, never replay stale frames
                    // or coalesce across UP/cancel/contact/window boundaries.
                    while(i+1<n && events[i+1].Stage==3 && events[i+1].Message==2 &&
                        events[i+1].Id==e.Id && events[i+1].Hwnd==e.Hwnd && (e.Flags&0x8000)==0 && (events[i+1].Flags&0x8000)==0) e=events[++i];
                    overlay.ForwardMove(e.Id,new Point(e.X,e.Y),(e.Flags&0x8000)!=0);
                }
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
        Write("STOP_REQUEST "+reason); waiting=false; ClearTargets();
        if(!stopping) { stoppingAt=DateTime.UtcNow; stopReason=reason; }
        stopping=true;
        overlay.DismissOverlay("Stopping",false); cancelledContact=true;
        nativeOwned=ExplorerRingNative.BridgePulse(0);
        if(nativeOwned==0) StopNow(reason);
    }
    private void StopNow(string reason)
    {
        if(stopped) return;
        ClearTargets();
        ExplorerRingNative.EndObservation();
        if(running) Drain();
        stopped=true; running=false; waiting=false; timer.Stop();
        arm.Enabled=stop.Enabled=false;
        Write("STOP "+reason+" cause="+stopReason+" controlLost="+dropped+" diagnosticLost="+diagnosticDropped);
        log.Flush();
        status.Text="Отключено: "+(stopReason=="Control event lost" ? "защита — потеря события управления" :
            stopReason=="Three minute timeout" ? "истекли 3 минуты" : stopReason=="User stop" ? "по кнопке остановки" : reason)+".\nЖурнал сохранён. "+result;
    }
    private sealed class SelfTestWindow : Form
    {
        internal int Wakes;
        protected override void WndProc(ref Message m)
        { if(m.Msg==0x8053) { Wakes++; return; } if(m.Msg==0x2cc || m.Msg==0x117) { m.Result=new IntPtr(0x1234); return; } base.WndProc(ref m); }
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
            ShellCommands.SelfTest();
            RingLab.TestAlphaTransport();
            RingLab.TestContrast();
            ViewTest.Run();
            var gui=new ExplorerRingNative.GuiInfo { Size=(uint)Marshal.SizeOf(typeof(ExplorerRingNative.GuiInfo)) };
            if(gui.Size!=72 || !ExplorerRingNative.GetGUIThreadInfo(ExplorerRingNative.GetCurrentThreadId(),ref gui) || (gui.Flags & 0x1c)!=0)
                throw new Exception("GUI menu state ABI/query failed");
            using(var f=new SelfTestWindow()) {
                var h=f.Handle;
                uint thread=ExplorerRingNative.GetCurrentThreadId();
                if(ExplorerRingNative.BeginBridge(thread,unchecked((ulong)h.ToInt64()),unchecked((ulong)h.ToInt64()))!=87)
                    throw new Exception("Same-process bridge target accepted");
                if(ExplorerRingNative.RetargetBridge(0,0,0)!=87) throw new Exception("Invalid retarget accepted");
                if(ExplorerRingNative.BridgeDiagnosticDrops()!=0) throw new Exception("Unexpected diagnostic counter");
                uint error=ExplorerRingNative.BeginObservation(thread);
                if(error!=0) throw new Exception("Native hooks error="+error);
                if(ExplorerRingNative.SetBridgeNotify(1)!=87 || ExplorerRingNative.SetBridgeNotify(unchecked((ulong)h.ToInt64()))!=0)
                    throw new Exception("Wake window validation failed");
                if(ExplorerRingNative.SendMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero)!=new IntPtr(0x1234)) throw new Exception("Changed response");
                ExplorerRingNative.PostMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero);
                Application.DoEvents();
                var items=new ExplorerRingNative.Event[128]; uint lost;
                uint n=ExplorerRingNative.DrainObservation(items,128,out lost);
                bool get=false,call=false;
                for(int i=0;i<n;i++) { if(items[i].Message!=0x2cc) throw new Exception("Unexpected event"); get|=items[i].Stage==1; call|=items[i].Stage==2; }
                if(!get || !call || lost!=0) throw new Exception("Missing hooks or lost events");
                // No popup is displayed. Test count/position telemetry on an
                // owned dummy submenu without reading any real menu contents.
                IntPtr menu=ExplorerRingNative.CreatePopupMenu();
                if(menu==IntPtr.Zero) throw new Exception("Test popup creation failed");
                try {
                    if(!ExplorerRingNative.AppendMenu(menu,0,new UIntPtr(1),"Test") ||
                       !ExplorerRingNative.AppendMenu(menu,0,new UIntPtr(2),"Test2")) throw new Exception("Test popup fill failed");
                    ExplorerRingNative.SendMessage(h,0x117,menu,new IntPtr(3));
                    uint menuCount=ExplorerRingNative.DrainObservation(items,128,out lost);
                    if(menuCount!=1 || lost!=0 || items[0].Stage!=2 || items[0].Message!=0x117 || items[0].Reserved!=2 || items[0].Id!=3)
                        throw new Exception("Submenu telemetry failed");
                } finally { ExplorerRingNative.DestroyMenu(menu); }
                for(int round=1;round<=2;round++) {
                    if(ExplorerRingNative.TestBridgeWake(unchecked((ulong)h.ToInt64()))!=0) throw new Exception("Wake test refused");
                    Application.DoEvents();
                    if(f.Wakes!=round) throw new Exception("Wake missing/not coalesced");
                    uint batch=ExplorerRingNative.DrainObservation(items,128,out lost);
                    if(batch!=2 || lost!=0 || items[0].Message!=2 || items[0].X!=30 || items[1].Message!=3 || items[1].X!=50)
                        throw new Exception("Wake transport lost newest MOVE/UP ordering");
                }
                ExplorerRingNative.TestBridgeWake(unchecked((ulong)h.ToInt64())); Application.DoEvents();
                uint partial=ExplorerRingNative.DrainObservation(items,1,out lost);
                if(partial!=1 || items[0].Message!=2 || lost!=0) throw new Exception("Partial wake drain failed");
                Application.DoEvents();
                if(f.Wakes!=4 || ExplorerRingNative.DrainObservation(items,128,out lost)!=1 || items[0].Message!=3 || lost!=0)
                    throw new Exception("Partial drain did not rearm wake/preserve UP");
                ExplorerRingNative.EndObservation();
                ExplorerRingNative.SendMessage(h,0x2cc,IntPtr.Zero,IntPtr.Zero);
                if(ExplorerRingNative.DrainObservation(items,128,out lost)!=0) throw new Exception("Events after unhook");
                File.WriteAllText(path,"PASS v0.13: single-gesture menu reuse/ownership/disposal/clipboard invalidation; wake window validation; uniform foreground/luminance; Segoe UI availability; compact variable-height rows; swipe scroll without command; scroll clamping/last item; hidden submenu/back; DIB exact alpha/top-down/premultiplication; local Shell file/background menus without execution; target guards; coordinate packing; unknown verb rejection; invocation/menu state ABI; view resolution; bridge GET/CALL/shared queue/preserved response/submenu telemetry/unhook. Events="+n+"\r\n");
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
