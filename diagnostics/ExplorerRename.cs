using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

// Exact file OR verified view background. Names/paths stay only in memory.
internal sealed class ExplorerRename
{
    private IntPtr root, view;
    private Point point;
    private string folderPath, itemPath, label;
    private int[] runtimeId;
    private uint clipboardSequence;
    internal bool Background { get { return itemPath==null; } }
    internal bool UsedNativeMenu { get; private set; }
    internal Action<string> Diagnostic;
    internal string[] Labels { get; private set; }
    internal bool[] Enabled { get; private set; }
    private static readonly string[] ItemVerbs={ "open","copy","cut","rename","recycle","more" };
    private static readonly string[] BackgroundVerbs={ "paste","pastelink","refresh","newfolder","properties","more" };
    internal string ActionName(int sector) { return sector<0 || sector>=6 ? "cancel" : (Background ? BackgroundVerbs : ItemVerbs)[sector]; }
    private static readonly Guid BrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid BrowserInterface = new Guid("000214E2-0000-0000-C000-000000000046");
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActiveView(IntPtr self, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ItemObject(IntPtr self, uint scope, ref Guid iid, [MarshalAs(UnmanagedType.IDispatch)] out object result);
    private ShellCommands ViewMenu()
    {
        // IContextMenu has no usable cross-process proxy on the test machine
        // (GetItemObject returns REGDB_E_IIDNOTREG). Build it in OUR STA from
        // the already verified filesystem target; never register a proxy.
        return new ShellCommands(Background ? folderPath : itemPath,Background,root);
    }
    private void Trace(string message) { if(Diagnostic!=null) Diagnostic(message); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RefreshView(IntPtr self);
    private static T Slot<T>(IntPtr p, int index) where T : class
    { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),index*IntPtr.Size),typeof(T)) as T; }
    private static object Document(IntPtr root)
    {
        dynamic windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
        string cls=ExplorerRingNative.ClassOf(root);
        if(cls=="CabinetWClass" || cls=="ExploreWClass")
        {
            int count=windows.Count;
            for(int i=0;i<count;i++) {
                dynamic w=windows.Item(i);
                if(new IntPtr((long)w.HWND)==root) return w.Document;
            }
            throw new InvalidOperationException("Folder window unavailable");
        }
        object location=0, unused=null; int hwnd;
        object desktop=windows.FindWindowSW(ref location,ref unused,8,out hwnd,1);
        var provider=(ServiceProvider)desktop;
        Guid service=BrowserService, iid=BrowserInterface;
        IntPtr browser=IntPtr.Zero, shellView=IntPtr.Zero;
        try {
            Marshal.ThrowExceptionForHR(provider.QueryService(ref service,ref iid,out browser));
            Marshal.ThrowExceptionForHR(Slot<ActiveView>(browser,15)(browser,out shellView));
            Guid dispatch=new Guid("00020400-0000-0000-C000-000000000046"); object doc;
            Marshal.ThrowExceptionForHR(Slot<ItemObject>(shellView,15)(shellView,0,ref dispatch,out doc));
            return doc;
        } finally { if(shellView!=IntPtr.Zero) Marshal.Release(shellView); if(browser!=IntPtr.Zero) Marshal.Release(browser); }
    }
    private void Refresh()
    {
        dynamic windows=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
        string cls=ExplorerRingNative.ClassOf(root);
        if(cls=="CabinetWClass" || cls=="ExploreWClass") {
            int count=windows.Count;
            for(int i=0;i<count;i++) { dynamic w=windows.Item(i); if(new IntPtr((long)w.HWND)==root) { w.Refresh(); return; } }
            throw new InvalidOperationException("Folder window unavailable");
        }
        object location=0,unused=null; int hwnd;
        var provider=(ServiceProvider)windows.FindWindowSW(ref location,ref unused,8,out hwnd,1);
        Guid service=BrowserService,iid=BrowserInterface;
        IntPtr browser=IntPtr.Zero,shellView=IntPtr.Zero;
        try {
            Marshal.ThrowExceptionForHR(provider.QueryService(ref service,ref iid,out browser));
            Marshal.ThrowExceptionForHR(Slot<ActiveView>(browser,15)(browser,out shellView));
            Marshal.ThrowExceptionForHR(Slot<RefreshView>(shellView,8)(shellView));
        } finally { if(shellView!=IntPtr.Zero) Marshal.Release(shellView); if(browser!=IntPtr.Zero) Marshal.Release(browser); }
    }
    private static AutomationElement Hit(Point p, IntPtr view)
    {
        var item=AutomationElement.FromPoint(new System.Windows.Point(p.X,p.Y));
        bool blank=true;
        for(int i=0;item!=null && i<40;i++,item=TreeWalker.RawViewWalker.GetParent(item))
        {
            var c=item.Current;
            if(c.NativeWindowHandle!=0 && new IntPtr(c.NativeWindowHandle)==view) {
                if(!blank) throw new InvalidOperationException("Not an empty view surface");
                return null;
            }
            if(c.ControlType!=ControlType.ListItem && c.ControlType!=ControlType.DataItem) {
                if(c.ControlType!=ControlType.List && c.ControlType!=ControlType.Pane &&
                   c.ControlType!=ControlType.Group && c.ControlType!=ControlType.Custom) blank=false;
                continue;
            }
            if(!c.BoundingRectangle.Contains(new System.Windows.Point(p.X,p.Y))) throw new InvalidOperationException("Outside item bounds");
            var parent=item;
            for(int j=0;parent!=null && j<24;j++,parent=TreeWalker.RawViewWalker.GetParent(parent)) {
                int h=parent.Current.NativeWindowHandle;
                if(h!=0 && new IntPtr(h)==view) return item;
            }
            throw new InvalidOperationException("Item outside target view");
        }
        throw new InvalidOperationException("View background not verified");
    }
    private static bool SameId(int[] a,int[] b)
    { if(a==null || b==null || a.Length!=b.Length) return false; for(int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false; return true; }
    private static object Match(dynamic doc,string name)
    {
        dynamic items=doc.Folder.Items(); object found=null;
        int count=items.Count;
        // Ambiguous display names (e.g. hidden extensions) are refused.
        for(int i=0;i<count;i++) {
            dynamic item=items.Item(i);
            if(!(bool)item.IsFileSystem) continue;
            string n=(string)item.Name, path=(string)item.Path;
            if(!String.Equals(name,n,StringComparison.Ordinal) &&
               !String.Equals(name,Path.GetFileName(path),StringComparison.Ordinal) &&
               !String.Equals(name,Path.GetFileNameWithoutExtension(path),StringComparison.Ordinal)) continue;
            if(found!=null) throw new InvalidOperationException("Ambiguous display name");
            found=item;
        }
        if(found==null) throw new InvalidOperationException("No filesystem item at contact");
        return found;
    }
    internal static ExplorerRename Capture(IntPtr root,IntPtr view,Point point)
    {
        var hit=Hit(point,view);
        dynamic doc=Document(root);
        string folder=(string)doc.Folder.Self.Path;
        if(!Directory.Exists(folder)) throw new InvalidOperationException("Not a filesystem folder");
        var target=new ExplorerRename { root=root,view=view,point=point,folderPath=folder };
        if(hit!=null) {
            if((int)doc.SelectedItems().Count>1) throw new InvalidOperationException("Multiple selection refused");
            target.label=hit.Current.Name; target.runtimeId=hit.GetRuntimeId();
            dynamic item=Match(doc,target.label); target.itemPath=(string)item.Path;
        }
        target.clipboardSequence=ShellCommands.GetClipboardSequenceNumber();
        target.Labels=target.Background ? new[] { "Вставить","Вставить ярлык","Обновить","Новая папка","Свойства","Ещё…" } :
            new[] { "Открыть","Копировать","Вырезать","Переименовать","В корзину","Ещё…" };
        target.Enabled=new bool[6];
        // Direct verbs target the folder object, not a detached background
        // renderer (which may incorrectly disable Paste without a view site).
        // The complete view-background menu is delegated to Explorer in More.
        using(var commands=new ShellCommands(target.Background ? folder : target.itemPath,false,root)) {
            for(int i=0;i<6;i++) {
                string verb=target.ActionName(i);
                target.Enabled[i]=verb=="more" || verb=="rename" || verb=="recycle" || verb=="newfolder" || verb=="refresh" || commands.Has(verb);
            }
        }
        return target;
    }
    private object Validate()
    {
        if(ExplorerRingNative.GetForegroundWindow()!=root || !ExplorerRingNative.IsWindow(view) ||
           ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),unchecked((ulong)view.ToInt64()))!=unchecked((ulong)view.ToInt64()))
            throw new InvalidOperationException("View changed");
        var hit=Hit(point,view);
        if(Background ? hit!=null : (hit==null || !SameId(runtimeId,hit.GetRuntimeId()) || hit.Current.Name!=label))
            throw new InvalidOperationException("Item under contact changed");
        dynamic doc=Document(root);
        if((string)doc.Folder.Self.Path!=folderPath || !Directory.Exists(folderPath) || (!Background && (int)doc.SelectedItems().Count>1))
            throw new InvalidOperationException("Folder or selection changed");
        if(Background) return doc;
        dynamic item=Match(doc,label);
        if(!String.Equals((string)item.Path,itemPath,StringComparison.OrdinalIgnoreCase) ||
           (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
            throw new InvalidOperationException("Item no longer available");
        return doc;
    }
    internal void Execute(int sector,IWin32Window owner)
    {
        if(sector<0 || sector>=6 || !Enabled[sector]) throw new InvalidOperationException("Disabled action");
        dynamic doc=Validate();
        string verb=ActionName(sector);
        if(verb=="more") {
            if(!Background) doc.SelectItem(Match(doc,label),0x1d);
            else {
                // A blank-area RMB must not show the old selected file's menu.
                dynamic selected=doc.SelectedItems();
                int count=selected.Count;
                for(int i=count-1;i>=0;i--) doc.SelectItem(selected.Item(i),0);
            }
            ShellCommands menu;
            try { menu=ViewMenu(); }
            catch(Exception e) { Trace("MORE_CONTEXT_FAILED type="+e.GetType().Name+" hr=0x"+e.HResult.ToString("X8")); ShowOriginalMenu(); return; }
            using(menu) {
                menu.Diagnostic=Diagnostic;
                if(Background) menu.Prefix=BackgroundEntries(doc);
                Trace("TOUCH_MORE_OPEN context="+(Background ? "background" : "item")+" source=localShell");
                bool original;
                var chosen=RingLab.ShowTouchMenu(menu,point,root,out original);
                Validate();
                if(original) { ShowOriginalMenu(); return; }
                if(chosen==null) return;
                if(chosen.LocalAction!=null) { ApplyViewAction(doc,chosen.LocalAction); return; }
                string selectedVerb=chosen.Verb ?? "";
                if(selectedVerb=="delete" && !Background) {
                    if(MessageBox.Show(owner,"Переместить объект в корзину? Если Windows предложит безвозвратное удаление — отмените.","Ring menu",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK) return;
                    ExplorerRingNative.SetForegroundWindow(root); Validate(); ShellCommands.Recycle(itemPath,root); return;
                }
                if(selectedVerb=="rename" && !Background) { doc.SelectItem(Match(doc,label),0x1f); return; }
                if((selectedVerb=="paste" || selectedVerb=="pastelink") && clipboardSequence!=ShellCommands.GetClipboardSequenceNumber()) throw new InvalidOperationException("Clipboard changed");
                // Unknown extension commands may be destructive. Require an
                // explicit confirmation, never auto-dispatch an opaque verb.
                if(String.IsNullOrEmpty(selectedVerb) || (selectedVerb!="open" && selectedVerb!="copy" && selectedVerb!="cut" && selectedVerb!="paste" && selectedVerb!="pastelink" && selectedVerb!="properties" && selectedVerb!="refresh")) {
                    if(MessageBox.Show(owner,"Выполнить выбранную команду Shell?\n"+chosen.Text,"Ring menu",MessageBoxButtons.OKCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.OK) return;
                    ExplorerRingNative.SetForegroundWindow(root); Validate();
                }
                // Selected-items menu must still describe exactly the one
                // originally verified item when dispatching its numeric ID.
                if(!Background) {
                    dynamic selected=doc.SelectedItems();
                    if((int)selected.Count!=1 || !String.Equals((string)selected.Item(0).Path,itemPath,StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Menu selection changed");
                }
                menu.Execute(chosen);
            }
            return;
        }
        if(Background) {
            if(verb=="refresh") { Refresh(); return; }
            if(verb=="newfolder") {
                string name="Новая папка"; int suffix=2;
                while(File.Exists(Path.Combine(folderPath,name)) || Directory.Exists(Path.Combine(folderPath,name))) {
                    name="Новая папка ("+(suffix++)+")";
                    if(suffix>10000) throw new InvalidOperationException("No unused folder name");
                }
                doc.Folder.NewFolder(name,0);
                // Do not resolve by name and edit afterwards: an external
                // creator could win the race and that would edit its folder.
                return;
            }
            if((verb=="paste" || verb=="pastelink") && clipboardSequence!=ShellCommands.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Clipboard changed while menu open");
        } else {
            dynamic item=Match(doc,label);
            if(verb=="recycle") {
                if(MessageBox.Show(owner,"Переместить выбранный под пальцем объект в корзину?\nЕсли Windows предупредит, что корзина недоступна, отмените операцию.",
                    "Ring menu — корзина",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK) throw new OperationCanceledException();
                // Dialog changes foreground; restore/check the original root and
                // validate identity again before any destructive Shell request.
                ExplorerRingNative.SetForegroundWindow(root);
                Validate(); ShellCommands.Recycle(itemPath,root); return;
            }
            doc.SelectItem(item,verb=="rename" ? 0x1f : 0x1d);
            if(verb=="rename") return;
        }
        using(var commands=new ShellCommands(Background ? folderPath : itemPath,false,root)) {
            if((verb=="paste" || verb=="pastelink") && clipboardSequence!=ShellCommands.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Clipboard changed during Shell query");
            commands.Execute(verb);
        }
    }
    private static ShellCommands.Entry Local(string text,string action,bool check)
    { return new ShellCommands.Entry { Text=text,Enabled=true,LocalAction=action,Checked=check }; }
    private System.Collections.Generic.List<ShellCommands.Entry> BackgroundEntries(dynamic doc)
    {
        var prefix=new System.Collections.Generic.List<ShellCommands.Entry>();
        // View-specific commands cannot be obtained from the detached Shell
        // menu. Use the documented automation interface of the LIVE view.
        try {
            uint mode=doc.CurrentViewMode; int size=doc.IconSize;
            var views=new System.Collections.Generic.List<ShellCommands.Entry>();
            views.Add(Local("Крупные значки","view:1:96",mode==1 && size>=64));
            views.Add(Local("Обычные значки","view:1:48",mode==1 && size<64));
            views.Add(Local("Мелкие значки","view:2:16",mode==2));
            string cls=ExplorerRingNative.ClassOf(root);
            if(cls=="CabinetWClass" || cls=="ExploreWClass") {
                views.Add(Local("Список","view:3:16",mode==3)); views.Add(Local("Таблица","view:4:16",mode==4));
            }
            prefix.Add(new ShellCommands.Entry { Text="Вид",Enabled=true,Children=views });
        } catch { /* Unsupported automation: leave exact native menu reachable. */ }
        try {
            string current=doc.SortColumns;
            var sorts=new System.Collections.Generic.List<ShellCommands.Entry>();
            string[] labels={"Имя","Дата изменения","Тип","Размер"};
            string[] keys={"System.ItemNameDisplay","System.DateModified","System.ItemTypeText","System.Size"};
            for(int i=0;i<keys.Length;i++) {
                sorts.Add(Local(labels[i]+" ↑","sort:prop:"+keys[i],current=="prop:"+keys[i]+";" || current=="prop:"+keys[i]));
                sorts.Add(Local(labels[i]+" ↓","sort:prop:-"+keys[i],current=="prop:-"+keys[i]+";" || current=="prop:-"+keys[i]));
            }
            prefix.Add(new ShellCommands.Entry { Text="Сортировка",Enabled=true,Children=sorts });
        } catch { }
        return prefix;
    }
    private static void ApplyViewAction(dynamic doc,string action)
    {
        if(action.StartsWith("view:",StringComparison.Ordinal)) {
            string[] parts=action.Split(':'); int mode,size;
            if(parts.Length!=3 || !Int32.TryParse(parts[1],out mode) || !Int32.TryParse(parts[2],out size) ||
                mode<1 || mode>4 || (size!=16 && size!=48 && size!=96)) throw new InvalidOperationException("View action refused");
            doc.CurrentViewMode=(uint)mode; doc.IconSize=size;
        } else if(action.StartsWith("sort:prop:",StringComparison.Ordinal)) {
            string key=action.Substring(10).TrimStart('-');
            if(key!="System.ItemNameDisplay" && key!="System.DateModified" && key!="System.ItemTypeText" && key!="System.Size")
                throw new InvalidOperationException("Sort action refused");
            doc.SortColumns=action.Substring(5);
        } else throw new InvalidOperationException("Unknown view action");
    }
    private void ShowOriginalMenu()
    {
        // Native Explorer builds the exact menu, including lazy extension
        // submenus. No synthesized mouse click and no item-selection fallback.
        IntPtr child=ExplorerRingNative.WindowFromPoint(point);
        if(ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),unchecked((ulong)child.ToInt64()))!=unchecked((ulong)view.ToInt64()))
            throw new InvalidOperationException("Menu target changed");
        if(!ExplorerRingNative.PostMessage(child,0x7b,child,ContextCoordinates(point))) throw new InvalidOperationException("Context menu request failed");
        UsedNativeMenu=true;
    }
    private static IntPtr ContextCoordinates(Point p)
    {
        if(p.X<short.MinValue || p.X>short.MaxValue || p.Y<short.MinValue || p.Y>short.MaxValue)
            throw new InvalidOperationException("Context coordinates out of range");
        // WM_CONTEXTMENU uses signed 16-bit screen coordinates, including
        // monitors left/above the primary. (-1,-1) is reserved for keyboard.
        if(p.X==-1 && p.Y==-1) throw new InvalidOperationException("Reserved context coordinates");
        return new IntPtr((long)(ushort)p.X|((long)(ushort)p.Y<<16));
    }
    public sealed class FakeItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsFileSystem { get; set; }
    }
    public sealed class FakeItems
    {
        public FakeItem[] Values;
        public int Count { get { return Values.Length; } }
        public FakeItem Item(int i) { return Values[i]; }
        public FakeItems Items() { return this; }
    }
    public sealed class FakeDocument { public FakeItems Folder { get; set; } }
    public sealed class FakeView
    {
        public uint CurrentViewMode { get; set; }
        public int IconSize { get; set; }
        public string SortColumns { get; set; }
    }
    internal static void SelfTest()
    {
        var fakeView=new FakeView();
        ApplyViewAction(fakeView,"view:4:16");
        ApplyViewAction(fakeView,"sort:prop:-System.DateModified");
        if(fakeView.CurrentViewMode!=4 || fakeView.IconSize!=16 || fakeView.SortColumns!="prop:-System.DateModified")
            throw new Exception("Documented view action dispatch regression");
        bool viewRefused=false;
        try { ApplyViewAction(fakeView,"sort:prop:System.Untrusted"); } catch(InvalidOperationException) { viewRefused=true; }
        if(!viewRefused) throw new Exception("Unknown view property accepted");
        foreach(var p in new[] { new Point(120,240),new Point(-120,-240),new Point(-1,40) }) {
            long packed=ContextCoordinates(p).ToInt64();
            if((short)(packed & 65535)!=p.X || (short)((packed>>16)&65535)!=p.Y) throw new Exception("Context coordinate packing failed");
        }
        bool bad=false;
        try { ContextCoordinates(new Point(-1,-1)); } catch(InvalidOperationException) { bad=true; }
        if(!bad) throw new Exception("Keyboard context sentinel accepted");
        bad=false;
        try { ContextCoordinates(new Point(40000,0)); } catch(InvalidOperationException) { bad=true; }
        if(!bad) throw new Exception("Context coordinate truncation accepted");
        var a=new FakeItem { Name="alpha.txt",Path=@"C:\test\alpha.txt",IsFileSystem=true };
        var b=new FakeItem { Name="alpha.log",Path=@"C:\test\alpha.log",IsFileSystem=true };
        var doc=new FakeDocument { Folder=new FakeItems { Values=new[] { a,b } } };
        if(!Object.ReferenceEquals(Match(doc,"alpha.txt"),a)) throw new Exception("Exact match failed");
        bool refused=false;
        try { Match(doc,"alpha"); } catch(InvalidOperationException) { refused=true; }
        if(!refused) throw new Exception("Ambiguous hidden extension accepted");
        a.IsFileSystem=false; refused=false;
        try { Match(doc,"alpha.txt"); } catch(InvalidOperationException) { refused=true; }
        if(!refused) throw new Exception("Virtual item accepted");
        if(!SameId(new[] { 1,2 },new[] { 1,2 }) || SameId(new[] { 1,2 },new[] { 1,3 }) || SameId(null,new int[0]))
            throw new Exception("Runtime identity guard failed");
        var background=new ExplorerRename();
        var file=new ExplorerRename { itemPath=@"C:\test\alpha.txt" };
        if(!background.Background || file.Background || background.ActionName(0)!="paste" ||
           file.ActionName(0)!="open" || background.ActionName(3)!="newfolder" || file.ActionName(3)!="rename" ||
           background.ActionName(5)!="more" || background.ActionName(-1)!="cancel")
            throw new Exception("Context action mapping failed");
    }
}
