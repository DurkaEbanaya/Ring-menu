using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// One explicitly armed Explorer GUI thread; native callbacks only observe.
internal static class ObserverNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Event
    {
        internal uint Tick, Thread, Stage, Message;
        internal ulong Hwnd, Extra;
        internal uint SourceDevice, SourceOrigin, Id, Type, Flags, PointerError;
        internal int X, Y;
        internal uint OsTime, Reserved;
    }
    [DllImport("ExplorerObserver.dll")] internal static extern uint BeginObservation(uint thread);
    [DllImport("ExplorerObserver.dll")] internal static extern void EndObservation();
    [DllImport("ExplorerObserver.dll")] internal static extern uint DrainObservation([Out] Event[] events, uint capacity, out uint dropped);
    [DllImport("ExplorerObserver.dll", CharSet=CharSet.Unicode)] internal static extern int ObserverClass(ulong hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
}

internal sealed class ExplorerProbe : Form
{
    private readonly Button arm = new Button();
    private readonly Button stop = new Button();
    private readonly Label status = new Label();
    private readonly Timer timer = new Timer();
    private readonly StreamWriter log;
    private readonly ObserverNative.Event[] events = new ObserverNative.Event[128];
    private DateTime armAt, stopAt;
    private bool waiting, observing, finished;
    private uint count, dropped;

    internal ExplorerProbe()
    {
        Text = "Explorer Probe — временное наблюдение";
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(850, 510);
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        var text = new Label();
        text.SetBounds(24, 20, 800, 310);
        text.Text = "Проверка ранних касаний в одном окне Проводника\n\n" +
            "1. Откройте Проводник и папку с тестовым файлом.\n" +
            "2. Нажмите «Подключить» и за 8 секунд переключитесь в Проводник.\n" +
            "3. Удержите файл 2 секунды, затем отпустите. Закройте меню.\n" +
            "4. Повторите: удержите, проведите вправо, отпустите.\n" +
            "    Выберите отмену, если появится меню перетаскивания.\n" +
            "5. Если есть мышь: обычный ПКМ. Если есть клавиатура: Shift+F10.\n" +
            "6. Вернитесь сюда и нажмите «Остановить».\n\n" +
            "В выбранный поток загрузится диагностическая DLL. Она не меняет\n" +
            "события и не вызывает команды. Автоотключение через 3 минуты.";
        Controls.Add(text);
        arm.Text = "Подключить через 8 секунд";
        arm.SetBounds(24, 340, 370, 48);
        arm.Click += delegate { waiting = true; arm.Enabled = false; armAt = DateTime.UtcNow.AddSeconds(8); timer.Start(); };
        Controls.Add(arm);
        stop.Text = "Остановить";
        stop.SetBounds(420, 340, 260, 48);
        stop.Enabled = false;
        stop.Click += delegate { Stop(); };
        Controls.Add(stop);
        status.SetBounds(24, 410, 800, 80);
        status.Text = "Ожидание. Наблюдение ещё не подключено.";
        Controls.Add(status);
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "explorer-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        log = new StreamWriter(path, false, new UTF8Encoding(true));
        Write("START session=" + Process.GetCurrentProcess().SessionId + " eventBytes=" + Marshal.SizeOf(typeof(ObserverNative.Event)));
        timer.Interval = 100;
        timer.Tick += Tick;
        FormClosed += delegate { Stop(); log.Dispose(); };
    }

    private void Write(string value) { log.WriteLine(DateTime.Now.ToString("O") + " " + value); log.Flush(); }

    private void Tick(object sender, EventArgs args)
    {
        if (waiting)
        {
            var remaining = armAt - DateTime.UtcNow;
            status.Text = "Переключитесь в нужное окно Проводника. Осталось " + Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds)) + " с.";
            if (remaining.TotalMilliseconds > 0) return;
            waiting = false;
            IntPtr hwnd = ObserverNative.GetForegroundWindow();
            uint pid;
            uint thread = ObserverNative.GetWindowThreadProcessId(hwnd, out pid);
            string name;
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { name = "unknown"; }
            var cls = new StringBuilder(256);
            ObserverNative.ObserverClass(unchecked((ulong)hwnd.ToInt64()), cls, cls.Capacity);
            // Do not arm the shell desktop/taskbar or arbitrary applications.
            if (!name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                (cls.ToString() != "CabinetWClass" && cls.ToString() != "ExploreWClass"))
            {
                Write("REFUSED process=" + name + " class=" + cls);
                status.Text = "Не подключено: активным должно быть окно папки Проводника. Попробуйте снова.";
                arm.Enabled = true; timer.Stop(); return;
            }
            uint error = ObserverNative.BeginObservation(thread);
            Write("ARM thread=" + thread + " pid=" + pid + " class=" + cls + " error=" + error);
            if (error != 0)
            {
                status.Text = "Не удалось подключить наблюдатель. Код " + error + ". Закройте программу.";
                finished = true; timer.Stop(); return;
            }
            observing = true;
            stopAt = DateTime.UtcNow.AddMinutes(3);
            stop.Enabled = true;
            System.Media.SystemSounds.Asterisk.Play();
        }
        if (observing)
        {
            Drain();
            status.Text = "Наблюдение включено: " + count + " событий, пропущено " + dropped + ". Осталось " +
                Math.Max(0, (int)(stopAt - DateTime.UtcNow).TotalSeconds) + " с. Можно тестировать в Проводнике.";
            if (DateTime.UtcNow >= stopAt) Stop();
        }
    }

    private void Drain()
    {
        uint n;
        do
        {
            n = ObserverNative.DrainObservation(events, (uint)events.Length, out dropped);
            for (uint i = 0; i < n; i++)
            {
                var e = events[i];
                var cls = new StringBuilder(256);
                ObserverNative.ObserverClass(e.Hwnd, cls, cls.Capacity);
                Write("EVENT tick=" + e.Tick + " thread=" + e.Thread + " stage=" + (e.Stage == 1 ? "GET" : "CALL") +
                    " msg=0x" + e.Message.ToString("X4") + " hwnd=0x" + e.Hwnd.ToString("X") + " class=" + cls +
                    " device=" + e.SourceDevice + " origin=" + e.SourceOrigin + " extra=0x" + e.Extra.ToString("X") +
                    " pointer=" + e.Id + " type=" + e.Type + " flags=0x" + e.Flags.ToString("X") +
                    " pointerError=" + e.PointerError + " x=" + e.X + " y=" + e.Y + " osTime=" + e.OsTime);
                count++;
            }
        } while (n == events.Length);
    }

    private void Stop()
    {
        if (finished) return;
        waiting = false;
        ObserverNative.EndObservation();
        if (observing) Drain();
        observing = false; finished = true;
        timer.Stop(); arm.Enabled = stop.Enabled = false;
        Write("STOP events=" + count + " dropped=" + dropped);
        status.Text = "Наблюдение отключено. Событий: " + count + ". Журнал сохранён рядом с программой.";
    }

    private sealed class SelfTestWindow : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x2cc) { m.Result = new IntPtr(0x1234); return; }
            base.WndProc(ref m);
        }
    }

    private static int SelfTest()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "explorer-observer-self-test.log");
        try
        {
            if (Marshal.SizeOf(typeof(ObserverNative.Event)) != 72) throw new Exception("Event ABI mismatch");
            using (var form = new SelfTestWindow())
            {
                IntPtr hwnd = form.Handle; // Never shown, no Explorer connection.
                uint error = ObserverNative.BeginObservation(ObserverNative.GetCurrentThreadId());
                if (error != 0) throw new Exception("BeginObservation error=" + error);
                if (ObserverNative.SendMessage(hwnd, 0x2cc, IntPtr.Zero, IntPtr.Zero) != new IntPtr(0x1234))
                    throw new Exception("Window response was not preserved");
                ObserverNative.PostMessage(hwnd, 0x2cc, IntPtr.Zero, IntPtr.Zero);
                Application.DoEvents();
                var items = new ObserverNative.Event[128];
                uint lost;
                uint n = ObserverNative.DrainObservation(items, (uint)items.Length, out lost);
                bool get = false, call = false;
                for (int i = 0; i < n; i++)
                {
                    if (items[i].Message != 0x2cc || items[i].Thread != ObserverNative.GetCurrentThreadId())
                        throw new Exception("Unexpected record");
                    get |= items[i].Stage == 1; call |= items[i].Stage == 2;
                }
                if (!get || !call || lost != 0) throw new Exception("Missing GET/CALL stage or dropped records");
                ObserverNative.EndObservation();
                ObserverNative.SendMessage(hwnd, 0x2cc, IntPtr.Zero, IntPtr.Zero);
                if (ObserverNative.DrainObservation(items, (uint)items.Length, out lost) != 0)
                    throw new Exception("Recorded after stop");
                File.WriteAllText(path, "PASS: x64 ABI; native GET/CALL; shared queue; stop. Events=" + n + "\r\n");
                return 0;
            }
        }
        catch (Exception e) { File.WriteAllText(path, "FAIL: " + e + "\r\n"); return 1; }
        finally { ObserverNative.EndObservation(); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        ObserverNative.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        Application.Run(new ExplorerProbe());
        return 0;
    }
}
