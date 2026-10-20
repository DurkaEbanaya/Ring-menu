using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class ExplorerRing
{
    private NotifyIcon tray;
    private bool exitRequested,autoConnect,settingsPending,closed;
    private SettingsPreview preview;
    private RingPreferences draft;
    private Label savedStatus;
    private Button startupButton;
    private CheckBox acrylicToggle;
    private TabControl settingsTabs;
    private bool gallery;
    private readonly System.Collections.Generic.List<Action> refreshFields=new System.Collections.Generic.List<Action>();
    private void BuildSettingsWindow()
    {
        Text="Ring menu · Настройки"; Font=new Font("Segoe UI",10);
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(1080,740); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        BackColor=Color.FromArgb(246,247,249); StartPosition=FormStartPosition.CenterScreen;
        draft=RingPreferences.Current.Copy();
        var title=new Label { Text="Ring menu",Font=new Font("Segoe UI",25,FontStyle.Bold),AutoSize=true,Location=new Point(30,22) }; Controls.Add(title);
        var subtitle=new Label { Text="Сенсорное меню для Проводника и рабочего стола",AutoSize=true,Location=new Point(33,75),ForeColor=Color.DimGray }; Controls.Add(subtitle);
        var tabs=new TabControl { Location=new Point(30,118),Size=new Size(580,480),Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left };
        settingsTabs=tabs;
        var gestures=new TabPage("Жесты и задержки") { BackColor=Color.White };
        var visuals=new TabPage("Оформление") { BackColor=Color.White };
        var system=new TabPage("Программа") { BackColor=Color.White }; tabs.TabPages.AddRange(new[] { gestures,visuals,system }); Controls.Add(tabs);
        AddNumber(gestures,"Порог удержания", "Через сколько начинать вызов кольца",24,100,1500,25,delegate { return draft.HoldMs; },delegate(int n) { draft.HoldMs=n; },"мс");
        AddNumber(gestures,"Задержка команды", "После отпускания; не относится к «Ещё»",110,0,1000,25,delegate { return draft.CommandDelayMs; },delegate(int n) { draft.CommandDelayMs=n; },"мс");
        AddNumber(gestures,"Задержка «Ещё»", "0 мс — открыть сразу после завершения контакта",196,0,1000,25,delegate { return draft.MoreDelayMs; },delegate(int n) { draft.MoreDelayMs=n; },"мс");
        AddNumber(gestures,"Порог движения", "Раннее движение отменяет удержание",282,6,40,1,delegate { return draft.MoveToleranceDip; },delegate(int n) { draft.MoveToleranceDip=n; },"DIP");
        AddNumber(gestures,"Ожидание выбора", "Закрыть оставленное без выбора кольцо",368,5,120,5,delegate { return draft.SelectionTimeoutSeconds; },delegate(int n) { draft.SelectionTimeoutSeconds=n; },"с");
        acrylicToggle=new CheckBox { Text="Размытие Acrylic",Location=new Point(22,18),AutoSize=true,Checked=draft.Acrylic };
        acrylicToggle.CheckedChanged+=delegate { draft.Acrylic=acrylicToggle.Checked; UpdatePreview(); }; visuals.Controls.Add(acrylicToggle);
        AddSlider(visuals,"Плотность тонировки Acrylic",60,delegate { return draft.AcrylicTint; },delegate(int n) { draft.AcrylicTint=n; });
        AddSlider(visuals,"Непрозрачность поверхности",148,delegate { return draft.SurfaceOpacity; },delegate(int n) { draft.SurfaceOpacity=n; },5);
        AddSlider(visuals,"Интенсивность Reveal",236,delegate { return draft.RevealIntensity; },delegate(int n) { draft.RevealIntensity=n; });
        AddSlider(visuals,"Текстура материала",324,delegate { return draft.GrainIntensity; },delegate(int n) { draft.GrainIntensity=n; });
        var explanation=new Label { Text="Радиус размытия задаёт Windows. При энергосбережении,\nвысоком контрасте или отключённой прозрачности — сплошной фон.",Location=new Point(22,407),Size=new Size(525,44),ForeColor=Color.DimGray }; visuals.Controls.Add(explanation);
        var help=new Label { Text="Фоновая работа\n\nЗакрытие окна сворачивает Ring menu в трей.\nДля завершения: значок в трее → «Выход».\n\nСеанс больше не ограничен тремя минутами.\nПри ошибке управления перехват остановится.\nПКМ мыши и клавиатура не перехватываются.\n\nАвтозапуск включается только вашей кнопкой,\nдля текущего пользователя, без прав администратора.\nПосле входа программа запускается свёрнутой.\nЕсли переместить EXE, отключите старый автозапуск\nиз прежней папки и включите из новой.",Location=new Point(24,24),Size=new Size(530,310) }; system.Controls.Add(help);
        startupButton=new Button { Location=new Point(24,350),Size=new Size(495,44),FlatStyle=FlatStyle.Flat };
        startupButton.Click+=delegate {
            try { RingStartup.Set(!RingStartup.Enabled); UpdateStartup(); }
            catch(Exception e) { MessageBox.Show(this,"Не удалось изменить автозапуск: "+e.GetType().Name,"Ring menu",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
        }; system.Controls.Add(startupButton); UpdateStartup();
        var version=new Label { Text="Версия 0.15 · экспериментальная интеграция Explorer\nНастройки: %LOCALAPPDATA%\\RingMenu\\settings.xml",Location=new Point(24,409),Size=new Size(535,44),ForeColor=Color.DimGray }; system.Controls.Add(version);
        var previewTitle=new Label { Text="Живой пример",Font=new Font("Segoe UI",15,FontStyle.Bold),Location=new Point(650,125),AutoSize=true }; Controls.Add(previewTitle);
        preview=new SettingsPreview { Location=new Point(638,169),Size=new Size(402,402),Preferences=draft.Copy() }; Controls.Add(preview);
        var previewHelp=new Label { Text="Параметры обновляются сразу. Проведите по примеру.\nРазмытие здесь имитируется на безопасном тестовом фоне.",Location=new Point(638,580),Size=new Size(420,44),ForeColor=Color.DimGray }; Controls.Add(previewHelp);
        var test=new Button { Text="Показать настоящее кольцо",Location=new Point(638,630),Size=new Size(402,40),FlatStyle=FlatStyle.Flat };
        test.Click+=delegate {
            if(nativeOwned!=0 || processing) return;
            ClearTargets(); overlay.DismissOverlay("Preview",false);
            RingLab.ShowMaterialDemo(draft.Copy(),preview.PointToScreen(new Point(preview.Width/2,preview.Height/2)),ExplorerRingNative.GetDpiForWindow(Handle));
        }; Controls.Add(test);
        var save=new Button { Text="Сохранить",Location=new Point(30,620),Size=new Size(180,46),BackColor=Color.FromArgb(0,103,192),ForeColor=Color.White,FlatStyle=FlatStyle.Flat };
        save.Click+=delegate {
            try { draft.Normalize(); draft.Save(); RingPreferences.Current=draft.Copy(); settingsPending=true; savedStatus.Text="Сохранено. Применится между жестами."; }
            catch(Exception e) { savedStatus.Text="Не удалось сохранить: "+e.GetType().Name; }
        }; Controls.Add(save);
        var reset=new Button { Text="По умолчанию",Location=new Point(225,620),Size=new Size(180,46),FlatStyle=FlatStyle.Flat };
        reset.Click+=delegate { draft=new RingPreferences(); acrylicToggle.Checked=draft.Acrylic; foreach(var refresh in refreshFields) refresh(); UpdatePreview(); savedStatus.Text="Восстановлены значения. Нажмите «Сохранить»."; }; Controls.Add(reset);
        savedStatus=new Label { Text=RingPreferences.LoadFailed ? "Файл настроек недоступен: загружены значения по умолчанию." : "Изменения применяются кнопкой «Сохранить». Автозапуск — только по вашему выбору.",Location=new Point(30,681),Size=new Size(1000,24),ForeColor=Color.DimGray }; Controls.Add(savedStatus);
        arm.Text="Включить"; arm.SetBounds(438,620,172,46); arm.Click+=delegate { StartMonitoring(); }; Controls.Add(arm);
        stop.Visible=false;
        status.SetBounds(30,710,1000,25); status.Text="Готово. Переключитесь в папку или на рабочий стол."; Controls.Add(status);
        Icon=CreateTrayIcon();
        tray=new NotifyIcon { Icon=(Icon)Icon.Clone(),Text="Ring menu · сенсорное меню",Visible=!gallery };
        var menu=new ContextMenuStrip(); menu.Items.Add("Настройки",null,delegate { ShowSettings(); });
        menu.Items.Add("Включить",null,delegate { StartMonitoring(); }); menu.Items.Add("Приостановить",null,delegate { autoConnect=false; RequestStop("User pause"); });
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("Выход",null,delegate { ExitApplication(); }); tray.ContextMenuStrip=menu;
        tray.DoubleClick+=delegate { ShowSettings(); };
        tray.MouseClick+=delegate(object sender,MouseEventArgs e) { if(e.Button==MouseButtons.Left) ShowSettings(); };
        FormClosing+=delegate(object s,FormClosingEventArgs e) {
            if(e.CloseReason==CloseReason.UserClosing && !exitRequested) { e.Cancel=true; Hide(); }
        };
        Shown+=delegate { preview.RefreshNow(); if(!gallery) StartMonitoring(); if(startHidden) Hide(); };
    }
    private void UpdateStartup() { startupButton.Text=RingStartup.Enabled ? "Убрать из автозапуска" : "Добавить в автозапуск"; }
    private void ShowSettings() { Show(); WindowState=FormWindowState.Normal; Activate(); }
    private void UpdatePreview() { if(preview!=null) { preview.Preferences=draft.Copy(); preview.QueueRefresh(); } }
    private static Icon CreateTrayIcon()
    {
        using(var bitmap=new Bitmap(32,32)) {
            using(var g=Graphics.FromImage(bitmap)) {
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using(var pen=new Pen(Color.FromArgb(0,120,215),6)) g.DrawEllipse(pen,5,5,22,22);
                using(var pen=new Pen(Color.White,2)) for(int i=0;i<6;i++) {
                    double a=i*Math.PI/3; g.DrawLine(pen,16+(float)Math.Cos(a)*8,16+(float)Math.Sin(a)*8,16+(float)Math.Cos(a)*14,16+(float)Math.Sin(a)*14);
                }
            }
            IntPtr h=bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private void AddNumber(Control page,string label,string hint,int y,int min,int max,int step,Func<int> get,Action<int> set,string unit)
    {
        page.Controls.Add(new Label { Text=label,Location=new Point(22,y),AutoSize=true,Font=new Font("Segoe UI",11,FontStyle.Bold) });
        page.Controls.Add(new Label { Text=hint,Location=new Point(22,y+31),Size=new Size(380,40),ForeColor=Color.DimGray });
        var box=new NumericUpDown { Minimum=min,Maximum=max,Increment=step,Value=get(),Location=new Point(400,y),Size=new Size(100,32) };
        box.ValueChanged+=delegate { set((int)box.Value); }; page.Controls.Add(box);
        page.Controls.Add(new Label { Text=unit,Location=new Point(505,y+5),AutoSize=true }); refreshFields.Add(delegate { box.Value=get(); });
    }
    private void AddSlider(Control page,string label,int y,Func<int> get,Action<int> set,int min=0)
    {
        var value=new Label { Text=get()+"%",Location=new Point(476,y),Size=new Size(62,25),TextAlign=ContentAlignment.MiddleRight };
        page.Controls.Add(new Label { Text=label,Location=new Point(22,y),AutoSize=true }); page.Controls.Add(value);
        var slider=new TrackBar { Minimum=min,Maximum=100,Value=get(),TickStyle=TickStyle.None,Location=new Point(18,y+28),Size=new Size(528,40),LargeChange=10,SmallChange=1 };
        slider.ValueChanged+=delegate { set(slider.Value); value.Text=slider.Value+"%"; UpdatePreview(); }; page.Controls.Add(slider);
        refreshFields.Add(delegate { slider.Value=get(); });
    }
    private static int CaptureSettings()
    {
        try {
            using(var app=new ExplorerRing(false,true)) {
                var folder=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"screenshots"); System.IO.Directory.CreateDirectory(folder);
                app.Show(); app.ClientSize=new Size(1080,740); Application.DoEvents();
                if(app.running || app.waiting || app.autoConnect) throw new Exception("Gallery connected transport");
                app.draft.HoldMs=475;
                if(RingPreferences.Current.HoldMs==475) throw new Exception("UI draft changed live configuration");
                app.draft=RingPreferences.Current.Copy();
                // Exercise close-to-tray without Explorer hooks/autostart writes.
                app.Close(); Application.DoEvents();
                if(app.IsDisposed || app.Visible) throw new Exception("Close-to-tray failed");
                app.Show(); Application.DoEvents();
                string[] names={ "settings-gestures.png","settings-appearance.png","settings-background.png","settings-solid.png" };
                for(int i=0;i<names.Length;i++) {
                    app.settingsTabs.SelectedIndex=i<3 ? i : 1;
                    if(i==3) { app.draft.Acrylic=false; app.draft.RevealIntensity=35; app.acrylicToggle.Checked=false; foreach(var refresh in app.refreshFields) refresh(); app.UpdatePreview(); }
                    app.PerformLayout(); app.preview.RefreshNow(); Application.DoEvents();
                    using(var bitmap=new Bitmap(app.Width,app.Height)) {
                        app.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                        Point client=app.PointToScreen(Point.Empty); client.Offset(-app.Left,-app.Top);
                        var crop=new Rectangle(client,app.ClientSize); crop.Intersect(new Rectangle(Point.Empty,bitmap.Size));
                        using(var cropped=bitmap.Clone(crop,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                            cropped.Save(System.IO.Path.Combine(folder,names[i]),System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                if(app.running) throw new Exception("Screenshot mode activated hooks");
                app.exitRequested=true;
                app.Close();
            }
            return 0;
        } catch(Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-capture-error.log"),e.ToString()); return 1; }
    }
}
