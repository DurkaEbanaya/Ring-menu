using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Passive, temporary input diagnostic. Never suppresses or injects input.
// No keyboard hook, file enumeration, window titles, or Explorer injection.
internal static class Native
{
    internal delegate IntPtr MouseProc(int code, IntPtr wp, IntPtr lp);
    internal delegate void EventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData
    { internal Point Position; internal uint Data, Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { internal uint Device, Origin; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
    {
        internal uint Type, Id, FrameId, Flags;
        internal IntPtr Device, Target;
        internal Point Pixel, Himetric, RawPixel, RawHimetric;
        internal uint Time, History;
        internal int InputData;
        internal uint KeyStates;
        internal ulong PerformanceCount;
        internal uint ButtonChange;
    }
    [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] internal static extern bool GetCurrentInputMessageSource(out Source source);
    [DllImport("user32.dll")] internal static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] internal static extern bool GetPointerInfo(uint id, out PointerInfo info);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, EventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
}

internal sealed class ProbeForm : Form
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Queue<string> pending = new Queue<string>();
    private readonly Dictionary<uint, string> processes = new Dictionary<uint, string>();
    private readonly Native.MouseProc mouseProc;
    private readonly Native.EventProc eventProc;
    private readonly StreamWriter log;
    private readonly Timer timer = new Timer();
    private readonly Label status = new Label();
    private IntPtr mouseHook, eventHook;
    private long lastMove = -1000;
    private int count;
    private bool stopped;
    private string logPath;

    internal ProbeForm()
    {
        logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "touch-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        log = new StreamWriter(logPath, false, new UTF8Encoding(true));
        Text = "Touch Probe — временная диагностика";
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(840, 570);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 480);
        BackColor = Color.FromArgb(242, 245, 248);
        var intro = new Label();
        intro.Text = "Тест сенсора — меню пока не заменяется\n\n" +
            "1. Удержите палец в белой области 2 секунды, затем отпустите.\n" +
            "2. Повторите: удержите, проведите пальцем вправо, отпустите.\n" +
            "3. Сверните окно. В Проводнике удержите файл до появления меню.\n" +
            "    Заметьте: меню появилось ДО или ПОСЛЕ отпускания?\n" +
            "4. Закройте меню. Повторите удержание с движением вправо.\n" +
            "5. Если есть мышь: вызовите обычный ПКМ на том же файле.\n\n" +
            "Не выбирайте команды меню; для теста достаточно отмены.";
        intro.SetBounds(22, 18, 795, 270);
        intro.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(intro);
        var canvas = new TouchCanvas(this);
        canvas.SetBounds(22, 300, 795, 160);
        canvas.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(canvas);
        status.SetBounds(22, 477, 795, 70);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        status.Font = new Font("Segoe UI", 9);
        Controls.Add(status);
        mouseProc = ObserveMouse;
        eventProc = ObserveEvent;
        timer.Interval = 250;
        timer.Tick += delegate { Flush(); if (clock.Elapsed.TotalMinutes >= 10) Stop(); };
        Shown += delegate { Start(); };
        FormClosed += delegate { Stop(); log.Dispose(); };
    }

    internal void Record(string value)
    {
        if (stopped) return;
        pending.Enqueue(clock.ElapsedMilliseconds.ToString("D8") + " " + value);
        count++;
    }

    private string ProcessAt(IntPtr hwnd)
    {
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        string name;
        if (!processes.TryGetValue(pid, out name))
        {
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { name = "unknown"; }
            processes[pid] = name;
        }
        return name;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var b = new StringBuilder(256);
        Native.GetClassName(hwnd, b, b.Capacity);
        return b.ToString();
    }

    private IntPtr ObserveMouse(int code, IntPtr wp, IntPtr lp)
    {
        // Hooks only enqueue. No disk writes or UI operations in the callback.
        try
        {
            if (code >= 0 && !stopped)
            {
                var d = (Native.MouseData)Marshal.PtrToStructure(lp, typeof(Native.MouseData));
                uint msg = unchecked((uint)wp.ToInt64());
                bool move = msg == 0x200;
                ulong extra = d.Extra.ToUInt64();
                bool tablet = (extra & 0xffffff00UL) == 0xff515700UL;
                if (!move || (tablet && clock.ElapsedMilliseconds - lastMove >= 60))
                {
                    var hwnd = Native.WindowFromPoint(d.Position);
                    string process = ProcessAt(hwnd);
                    if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase) || process.Equals("TouchProbe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (move) lastMove = clock.ElapsedMilliseconds;
                        string source = tablet ? ((extra & 0x80) != 0 ? "TOUCH" : "PEN") : "MOUSE_OR_UNTAGGED";
                        Record("LL msg=0x" + msg.ToString("X4") + " source=" + source + " x=" + d.Position.X + " y=" + d.Position.Y +
                            " flags=0x" + d.Flags.ToString("X") + " extra=0x" + extra.ToString("X") + " osTime=" + d.Time +
                            " process=" + process + " class=" + ClassOf(hwnd));
                    }
                }
            }
        }
        catch { /* The observer must never interfere with system input. */ }
        return Native.CallNextHookEx(mouseHook, code, wp, lp);
    }

    private void ObserveEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        try
        {
            if (ProcessAt(hwnd).Equals("explorer", StringComparison.OrdinalIgnoreCase))
                Record("MENU " + (ev == 6 ? "OPEN" : "CLOSE") + " osTime=" + time + " class=" + ClassOf(hwnd));
        }
        catch { }
    }

    private void Start()
    {
        Record("START local=" + DateTime.Now.ToString("O") + " session=" + Process.GetCurrentProcess().SessionId + " os=" + Environment.OSVersion);
        foreach (Screen s in Screen.AllScreens)
            Record("SCREEN bounds=" + s.Bounds + " work=" + s.WorkingArea + " primary=" + s.Primary);
        Record("DPI=" + Native.GetDpiForWindow(Handle) + " maxTouch=" + Native.GetSystemMetrics(95));
        mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
        int error = Marshal.GetLastWin32Error();
        Record("MOUSE_HOOK=" + (mouseHook != IntPtr.Zero) + " error=" + error);
        eventHook = Native.SetWinEventHook(6, 7, IntPtr.Zero, eventProc, 0, 0, 0);
        Record("MENU_HOOK=" + (eventHook != IntPtr.Zero));
        timer.Start();
        Flush();
    }

    private void Flush()
    {
        while (pending.Count > 0) log.WriteLine(pending.Dequeue());
        log.Flush();
        status.Text = (stopped ? "Запись остановлена." : "Пассивная запись: " + count + " событий. Автостоп через 10 минут.") +
            "\nЗакрытие окна отключает наблюдение. Журнал: " + Path.GetFileName(logPath);
    }

    private void Stop()
    {
        if (stopped) return;
        Record("STOP");
        stopped = true;
        timer.Stop();
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (eventHook != IntPtr.Zero) Native.UnhookWinEvent(eventHook);
        mouseHook = eventHook = IntPtr.Zero;
        Flush();
    }

    private sealed class TouchCanvas : Panel
    {
        private readonly ProbeForm owner;
        private long lastUpdate = -1000;
        internal TouchCanvas(ProbeForm owner)
        {
            this.owner = owner;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            DoubleBuffered = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.DrawString("Удержание и движение — тестируйте здесь", Font, Brushes.DimGray, 22, 25);
            e.Graphics.DrawString("Действия с файлами не выполняются", Font, Brushes.Gray, 22, 62);
        }
        protected override void WndProc(ref Message m)
        {
            int msg = m.Msg;
            bool pointer = msg == 0x245 || msg == 0x246 || msg == 0x247 || msg == 0x24C;
            bool mouse = msg >= 0x201 && msg <= 0x209;
            if (pointer || mouse || msg == 0x7B || msg == 0x119 || msg == 0x2CC)
            {
                bool update = msg == 0x245;
                if (!update || owner.clock.ElapsedMilliseconds - lastUpdate >= 60)
                {
                    if (update) lastUpdate = owner.clock.ElapsedMilliseconds;
                    Native.Source source;
                    Native.GetCurrentInputMessageSource(out source);
                    string details = "";
                    if (pointer)
                    {
                        uint id = unchecked((uint)m.WParam.ToInt64()) & 0xffff;
                        Native.PointerInfo info;
                        if (Native.GetPointerInfo(id, out info))
                            details = " pointer=" + id + " type=" + info.Type + " flags=0x" + info.Flags.ToString("X") + " x=" + info.Pixel.X + " y=" + info.Pixel.Y;
                    }
                    owner.Record("OWN msg=0x" + msg.ToString("X4") + " device=" + source.Device + " origin=" + source.Origin +
                        " extra=0x" + Native.GetMessageExtraInfo().ToInt64().ToString("X") + details);
                }
            }
            base.WndProc(ref m);
        }
    }

    [STAThread]
    private static void Main()
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProbeForm());
    }
}
