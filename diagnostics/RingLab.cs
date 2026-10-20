using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// An isolated interaction prototype, not an Explorer extension.
internal sealed partial class RingLab : Form
{
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr Device, Target;
        public PointNative Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, History;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChange;
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetPointerInfo(uint id, out PointerInfo info);

    private readonly string[] names = { "Открыть", "Копировать", "Вырезать", "Переименовать", "В корзину", "Ещё…" };
    private readonly bool[] enabled = { true,true,true,true,true,true };
    private string contextLabel="Файл";
    private readonly Timer timer = new Timer();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly StreamWriter log;
    private float scale = 1, radius, inner, margin, guard;
    private PointF anchor, center, last;
    private uint active;
    private long downAt;
    private bool armed, visibleRing, fixedMode, waitInitialUp;
    private int hover = -1, pressed = -1;
    private string message = "Удержите палец на любом значке файла.";
    private RectangleF close;

    private RingLab() : this(true) { }
    private RingLab(bool writeLog)
    {
        Text = "Ring Lab — тест жестов, без операций с файлами";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen.WorkingArea;
        BackColor = Color.FromArgb(239, 243, 247);
        DoubleBuffered = true;
        KeyPreview = true;
        log = writeLog ? new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "ring-lab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"), false, System.Text.Encoding.UTF8) : new StreamWriter(Stream.Null);
        log.AutoFlush = true;
        Shown += delegate {
            scale = GetDpiForWindow(Handle) / 96f;
            radius = 148 * scale; inner = 58 * scale; margin = 14 * scale; guard = 26 * scale;
            Log("START bounds=" + Bounds + " dpi=" + GetDpiForWindow(Handle));
            Invalidate();
        };
        timer.Interval = 20;
        timer.Tick += delegate {
            if (armed && clock.ElapsedMilliseconds - downAt >= 450) OpenRing();
            else if (armed) Invalidate();
        };
        timer.Start();
        KeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Escape) { if (visibleRing || armed) Cancel("Отменено"); else Close(); }
        };
        MouseUp += delegate(object sender, MouseEventArgs e) { if (close.Contains(e.Location)) Close(); };
        FormClosed += delegate { timer.Stop(); Log("STOP"); log.Dispose(); };
    }

    private void Log(string s) { log.WriteLine(clock.ElapsedMilliseconds.ToString("D8") + " " + s); }
    private static double Distance(PointF a, PointF b)
    { double x = a.X - b.X, y = a.Y - b.Y; return Math.Sqrt(x*x + y*y); }
    private static float Clamp(float x, float min, float max) { return Math.Max(min, Math.Min(max, x)); }
    private PointF Fit(PointF p)
    { return new PointF(Clamp(p.X, margin + radius, ClientSize.Width - margin - radius), Clamp(p.Y, margin + radius, ClientSize.Height - margin - radius)); }

    private void OpenRing()
    {
        armed = false;
        if (ClientSize.Width < 2*(radius+margin) || ClientSize.Height < 2*(radius+margin))
        { Cancel("Недостаточно места для кольца. Измените ориентацию экрана."); return; }
        center = Fit(anchor);
        fixedMode = Distance(center, anchor) > 0.5;
        if (fixedMode)
        {
            // Nearest feasible center outside the finger's circular exclusion zone.
            // Circle/rectangle intersections cover the constrained nearest-point cases.
            double d = radius + guard;
            var candidates = new System.Collections.Generic.List<PointF>();
            for (int i = 0; i < 720; i++)
            {
                double a = i * Math.PI / 360;
                candidates.Add(Fit(new PointF(anchor.X + (float)(Math.Cos(a)*d), anchor.Y + (float)(Math.Sin(a)*d))));
            }
            float[] xs = { margin + radius, ClientSize.Width - margin - radius };
            float[] ys = { margin + radius, ClientSize.Height - margin - radius };
            foreach (float x in xs)
            {
                double rest = d*d - (x-anchor.X)*(x-anchor.X);
                if (rest >= 0) foreach (int sign in new int[] { -1, 1 })
                    candidates.Add(Fit(new PointF(x, anchor.Y + sign*(float)Math.Sqrt(rest))));
            }
            foreach (float y in ys)
            {
                double rest = d*d - (y-anchor.Y)*(y-anchor.Y);
                if (rest >= 0) foreach (int sign in new int[] { -1, 1 })
                    candidates.Add(Fit(new PointF(anchor.X + sign*(float)Math.Sqrt(rest), y)));
            }
            double best = double.MaxValue;
            bool found = false;
            foreach (PointF p in candidates)
            {
                double dist = Distance(p, anchor);
                if (dist + 0.01 >= d && dist < best) { best = dist; center = p; found = true; }
            }
            if (!found) { Cancel("Недостаточно места для кольца. Измените ориентацию экрана."); return; }
        }
        visibleRing = true; waitInitialUp = fixedMode;
        hover = pressed = -1;
        message = fixedMode ? "У края: отпустите палец, затем коснитесь пункта." : "Ведите палец в сектор и отпустите. Центр — отмена.";
        Log("OPEN mode=" + (fixedMode ? "FIXED" : "DRAG") + " anchor=" + anchor + " center=" + center);
        Invalidate();
    }

    private int Hit(PointF p)
    {
        double d = Distance(p, center);
        if (d < inner || d > radius) return -1;
        double angle = Math.Atan2(p.Y-center.Y, p.X-center.X)*180/Math.PI;
        double normalized = (angle + 120 + 360) % 360;
        // Small dead zone at sector boundaries avoids ambiguous selections.
        double within = normalized % 60;
        if (within < 1.5 || within > 58.5) return -1;
        int sector=(int)(normalized / 60);
        return enabled[sector] ? sector : -1;
    }

    private void Cancel(string reason)
    {
        armed = visibleRing = waitInitialUp = false;
        hover = pressed = -1; active = 0;
        message = reason; Log("CANCEL " + reason); Invalidate();
    }

    private void Finish(int sector)
    {
        string result = sector < 0 ? "Отмена — действие не выбрано." : "Выбрано: «" + names[sector] + "». Это имитация, файлы не затронуты.";
        Log("RESULT sector=" + sector + " mode=" + (fixedMode ? "FIXED" : "DRAG"));
        armed = visibleRing = waitInitialUp = false; active = 0; pressed = hover = -1;
        message = result; Invalidate();
#if EXPLORER_RING
        if (integrated) CompleteOverlay(sector);
#endif
    }

    private void TouchDown(uint id, PointF p)
    {
        if (close.Contains(p)) { Close(); return; }
        if (active != 0) { Cancel("Второе касание: отмена."); return; }
        if (visibleRing)
        {
            if (!fixedMode || waitInitialUp) return;
            pressed = Hit(p);
            if (pressed < 0) { Cancel("Меню закрыто касанием вне секторов."); return; }
            active = id; last = p; hover = pressed; Invalidate(); return;
        }
        active = id; anchor = last = p; downAt = clock.ElapsedMilliseconds; armed = true;
        message = "Удерживайте…"; Log("DOWN x=" + p.X + " y=" + p.Y); Invalidate();
    }

    private void TouchMove(uint id, PointF p)
    {
        if (id != active) return;
        last = p;
        if (armed && Distance(anchor, p) > 14*scale) { Cancel("Движение до удержания: меню не вызвано."); return; }
        if (visibleRing && !waitInitialUp) { hover = Hit(p); Invalidate(); }
    }

    private void TouchUp(uint id, PointF p)
    {
        if (id != active) return;
        if (visibleRing && fixedMode && waitInitialUp)
        {
            active = 0; waitInitialUp = false; hover = -1;
            message = "Кольцо закреплено. Коснитесь пункта или снаружи для отмены.";
            Log("INITIAL_UP_IGNORED"); Invalidate(); return;
        }
        if (visibleRing) { int hit = Hit(p); Finish(fixedMode && hit != pressed ? -1 : hit); }
        else { armed = false; active = 0; message = "Короткое касание: меню не вызвано."; Invalidate(); }
    }

    protected override void WndProc(ref Message m)
    {
#if EXPLORER_RING
        if (integrated && OverlayMessage(ref m)) return;
#endif
        if (m.Msg == 0x2CC) { m.Result = new IntPtr(0x10001); return; } // Disable native hold/right-tap only in this test window.
        if (m.Msg == 0x246 || m.Msg == 0x245 || m.Msg == 0x247)
        {
            uint id = unchecked((uint)m.WParam.ToInt64()) & 0xffff;
            PointerInfo info;
            if (GetPointerInfo(id, out info) && info.Type == 2)
            {
                Point p = PointToClient(new Point(info.Pixel.X, info.Pixel.Y));
                if ((info.Flags & 0x8000) != 0) Cancel("Касание отменено системой.");
                else if (m.Msg == 0x246) TouchDown(id, p);
                else if (m.Msg == 0x247) TouchUp(id, p);
                else TouchMove(id, p);
                m.Result = IntPtr.Zero; return;
            }
        }
        if (m.Msg == 0x24C && active != 0) Cancel("Потерян захват касания.");
        if (m.Msg == 0x7B) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    private void DrawText(Graphics g, string value, float x, float y, float size, Color color, bool centered)
    {
        using (var font = new Font("Segoe UI", size*scale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat())
        { if (centered) { format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center; } g.DrawString(value, font, brush, x, y, format); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
#if EXPLORER_RING
        if(integrated) return; // Manual alpha surface must not be painted opaque.
#endif
        base.OnPaintBackground(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
#if EXPLORER_RING
        if (integrated) { PresentOverlay(); return; }
#endif
        base.OnPaint(e);
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawText(g, "Кольцо · тест взаимодействия", 24*scale, 20*scale, 25, Color.FromArgb(32,42,52), false);
        DrawText(g, "Без подключения к Проводнику. Все действия — имитация.", 24*scale, 58*scale, 14, Color.DimGray, false);
        close = new RectangleF(ClientSize.Width-142*scale, 18*scale, 120*scale, 48*scale);
        using (var brush = new SolidBrush(Color.White)) g.FillRectangle(brush, close);
        DrawText(g, "Закрыть", close.X+close.Width/2, close.Y+close.Height/2, 16, Color.FromArgb(0,100,175), true);
        float[] xs = { 34*scale, ClientSize.Width/2f, ClientSize.Width-34*scale };
        float[] ys = { 128*scale, ClientSize.Height/2f, ClientSize.Height-48*scale };
        foreach (float x in xs) foreach (float y in ys)
        {
            using (var brush = new SolidBrush(Color.FromArgb(218,230,241))) g.FillRectangle(brush, x-17*scale,y-24*scale,34*scale,42*scale);
            DrawText(g, "Файл", x,y+34*scale,12,Color.SlateGray,true);
        }
        DrawText(g, message, ClientSize.Width/2f, 98*scale, 15, Color.FromArgb(0,100,175), true);
        if (armed)
        {
            float progress = Math.Min(1, (clock.ElapsedMilliseconds-downAt)/450f);
            using (var pen = new Pen(Color.FromArgb(0,120,212), 3*scale))
                g.DrawArc(pen, anchor.X-22*scale,anchor.Y-22*scale,44*scale,44*scale,-90,360*progress);
        }
        DrawRing(g);
    }

    private void DrawRing(Graphics g)
    {
        if (!visibleRing) return;
        if (fixedMode)
        {
            using (var pen = new Pen(Color.FromArgb(135,160,180), scale)) g.DrawLine(pen, anchor,center);
            using (var pen = new Pen(Color.FromArgb(0,120,212), scale)) g.DrawEllipse(pen,anchor.X-guard,anchor.Y-guard,guard*2,guard*2);
        }
        using (var shadow = new SolidBrush(Color.FromArgb(28,0,0,0))) g.FillEllipse(shadow,center.X-radius+4*scale,center.Y-radius+7*scale,radius*2,radius*2);
        for (int i=0;i<6;i++)
        {
            float start = -120+i*60;
            using (var path = new GraphicsPath())
            {
                path.AddArc(center.X-radius,center.Y-radius,radius*2,radius*2,start+1,58);
                path.AddArc(center.X-inner,center.Y-inner,inner*2,inner*2,start+59,-58); path.CloseFigure();
                using (var brush = new SolidBrush(hover==i ? Color.FromArgb(211,234,251) : Color.FromArgb(252,252,252))) g.FillPath(brush,path);
                using (var pen = new Pen(hover==i ? Color.FromArgb(0,120,212) : Color.FromArgb(196,207,216),scale)) g.DrawPath(pen,path);
            }
            double angle = (start+30)*Math.PI/180;
            float r=(inner+radius)/2;
            float x=center.X+(float)Math.Cos(angle)*r,y=center.Y+(float)Math.Sin(angle)*r;
            DrawText(g,names[i],x,y, names[i].Length>12 ? 10 : i==3 ? 11 : 12,enabled[i] ? Color.FromArgb(35,53,67) : Color.FromArgb(157,163,168),true);
        }
        using (var brush = new SolidBrush(Color.White)) g.FillEllipse(brush,center.X-inner+3*scale,center.Y-inner+3*scale,(inner-3*scale)*2,(inner-3*scale)*2);
        DrawText(g,contextLabel+"\nОтмена",center.X,center.Y,14,Color.SlateGray,true);
        DrawText(g,fixedMode ? "КАСАНИЕ" : "ЖЕСТ",center.X,center.Y+radius+22*scale,12,Color.FromArgb(0,100,175),true);
    }

#if !EXPLORER_RING
    [STAThread] private static void Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RingLab());
    }
#endif
}
