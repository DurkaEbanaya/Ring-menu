using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// No filesystem rename and no F2/SendInput. Ask the exact Shell view to enter
// its normal label editor. Names/paths stay in memory, never in diagnostic logs.
internal sealed class ExplorerRename
{
    private IntPtr root, view;
    private Point point;
    private string folderPath, itemPath, label;
    private int[] runtimeId;
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
    private static AutomationElement Hit(Point p, IntPtr view)
    {
        var item=AutomationElement.FromPoint(new System.Windows.Point(p.X,p.Y));
        for(int i=0;item!=null && i<16;i++,item=TreeWalker.RawViewWalker.GetParent(item))
        {
            var c=item.Current;
            if(c.ControlType!=ControlType.ListItem && c.ControlType!=ControlType.DataItem) continue;
            if(!c.BoundingRectangle.Contains(new System.Windows.Point(p.X,p.Y))) return null;
            var parent=item;
            for(int j=0;parent!=null && j<24;j++,parent=TreeWalker.RawViewWalker.GetParent(parent)) {
                int h=parent.Current.NativeWindowHandle;
                if(h!=0 && new IntPtr(h)==view) return item;
            }
            return null;
        }
        return null;
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
        if(hit==null) throw new InvalidOperationException("Contact did not hit a file item");
        dynamic doc=Document(root);
        if((int)doc.SelectedItems().Count>1) throw new InvalidOperationException("Multiple selection refused");
        string name=hit.Current.Name;
        dynamic item=Match(doc,name);
        return new ExplorerRename { root=root,view=view,point=point,label=name,runtimeId=hit.GetRuntimeId(),
            folderPath=(string)doc.Folder.Self.Path,itemPath=(string)item.Path };
    }
    internal void BeginEdit()
    {
        if(ExplorerRingNative.GetForegroundWindow()!=root || !ExplorerRingNative.IsWindow(view) ||
           ExplorerRingNative.ResolveBridgeView(unchecked((ulong)root.ToInt64()),unchecked((ulong)view.ToInt64()))!=unchecked((ulong)view.ToInt64()))
            throw new InvalidOperationException("View changed");
        var hit=Hit(point,view);
        if(hit==null || !SameId(runtimeId,hit.GetRuntimeId()) || hit.Current.Name!=label)
            throw new InvalidOperationException("Item under contact changed");
        dynamic doc=Document(root);
        if((string)doc.Folder.Self.Path!=folderPath || (int)doc.SelectedItems().Count>1)
            throw new InvalidOperationException("Folder or selection changed");
        dynamic item=Match(doc,label);
        if(!String.Equals((string)item.Path,itemPath,StringComparison.OrdinalIgnoreCase) ||
           (!File.Exists(itemPath) && !Directory.Exists(itemPath)))
            throw new InvalidOperationException("Item no longer available");
        doc.SelectItem(item,0x1f); // EDIT | DESELECTOTHERS | ENSUREVISIBLE | FOCUSED
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
    }
}
