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
            ShowOriginalMenu(); return;
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
    private void ShowOriginalMenu()
    {
        // Native Explorer builds the exact menu, including lazy extension
        // submenus. No synthesized mouse click and no item-selection fallback.
        IntPtr child=ExplorerRingNative.WindowFromPoint(point);
        if(ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),unchecked((ulong)child.ToInt64()))!=unchecked((ulong)view.ToInt64()))
            throw new InvalidOperationException("Menu target changed");
        long packed=(long)(ushort)point.X|((long)(ushort)point.Y<<16);
        if(!ExplorerRingNative.PostMessage(child,0x7b,child,new IntPtr(packed))) throw new InvalidOperationException("Context menu request failed");
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
    internal static void SelfTest()
    {
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
