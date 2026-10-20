using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// Local Shell COM objects; never send COM pointers between processes. Windows
// supplies the data object/drop effect for copy, cut and paste (not File.Copy).
internal sealed class ShellCommands : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Bind(IntPtr self,IntPtr pidl,IntPtr bc,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateView(IntPtr self,IntPtr owner,ref Guid iid,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UiObject(IntPtr self,IntPtr owner,uint count,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)] IntPtr[] pidls,ref Guid iid,IntPtr reserved,out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryMenu(IntPtr self,IntPtr menu,uint index,uint first,uint last,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CommandString(IntPtr self,UIntPtr id,uint flags,IntPtr reserved,IntPtr text,uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(IntPtr self,ref InvokeInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct InvokeInfo
    {
        internal uint Size,Mask; internal IntPtr Owner,Verb,Parameters,Directory;
        internal int Show; internal uint HotKey; internal IntPtr Icon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name,IntPtr bc,out IntPtr pidl,uint attrs,out uint result);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl,ref Guid iid,out IntPtr parent,out IntPtr child);
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IntPtr folder);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr menu,int index);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr menu,int index);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr menu,uint id,uint flags);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    private IntPtr context,menu;
    private readonly Dictionary<string,uint> commands=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
    private readonly IntPtr owner;
    private static T Slot<T>(IntPtr p,int index) where T:class
    { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),index*IntPtr.Size),typeof(T)) as T; }
    private static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    internal ShellCommands(string path,bool background,IntPtr owner)
    {
        this.owner=owner;
        IntPtr pidl=IntPtr.Zero,parent=IntPtr.Zero,folder=IntPtr.Zero;
        try {
            uint attrs; Check(SHParseDisplayName(path,IntPtr.Zero,out pidl,0,out attrs));
            Guid sf=new Guid("000214E6-0000-0000-C000-000000000046"), cm=new Guid("000214E4-0000-0000-C000-000000000046");
            if(background) {
                Check(SHGetDesktopFolder(out parent));
                Check(Slot<Bind>(parent,5)(parent,pidl,IntPtr.Zero,ref sf,out folder));
                Check(Slot<CreateView>(folder,8)(folder,owner,ref cm,out context));
            } else {
                IntPtr child; Check(SHBindToParent(pidl,ref sf,out parent,out child));
                Check(Slot<UiObject>(parent,10)(parent,owner,1,new[] { child },ref cm,IntPtr.Zero,out context));
            }
            menu=CreatePopupMenu(); if(menu==IntPtr.Zero) throw new InvalidOperationException("Menu allocation failed");
            Check(Slot<QueryMenu>(context,3)(context,menu,0,1,0x7fff,0));
            ReadMenu(menu,0);
        } catch { Dispose(); throw; }
        finally {
            if(folder!=IntPtr.Zero) Marshal.Release(folder);
            if(parent!=IntPtr.Zero) Marshal.Release(parent);
            if(pidl!=IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }
    private void ReadMenu(IntPtr m,int depth)
    {
        if(depth>8) return;
        int n=GetMenuItemCount(m);
        for(int i=0;i<n;i++) {
            uint id=GetMenuItemID(m,i);
            if(id>=1 && id<0x8000 && (GetMenuState(m,(uint)i,0x400)&3)==0) {
                IntPtr text=Marshal.AllocHGlobal(512);
                try {
                    Marshal.WriteInt16(text,0);
                    int hr=Slot<CommandString>(context,5)(context,new UIntPtr(id-1),4,IntPtr.Zero,text,256);
                    if(hr>=0) { string verb=Marshal.PtrToStringUni(text); if(!String.IsNullOrEmpty(verb)) commands[verb]=id-1; }
                } finally { Marshal.FreeHGlobal(text); }
            }
            IntPtr sub=GetSubMenu(m,i); if(sub!=IntPtr.Zero) ReadMenu(sub,depth+1);
        }
    }
    internal bool Has(string verb) { return commands.ContainsKey(verb); }
    internal static void SelfTest()
    {
        if(Marshal.SizeOf(typeof(InvokeInfo))!=56) throw new Exception("Invoke ABI mismatch");
        string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Ring-menu-shell-test-"+Guid.NewGuid().ToString("N"));
        string file=System.IO.Path.Combine(path,"sample.txt");
        System.IO.Directory.CreateDirectory(path);
        try {
            System.IO.File.WriteAllText(file,"isolated menu query only");
            using(var commands=new ShellCommands(file,false,IntPtr.Zero)) {
                if(!commands.Has("copy") || !commands.Has("cut")) throw new Exception("File canonical clipboard verbs absent");
                bool refused=false;
                try { commands.Execute("ring-menu-nonexistent-verb"); } catch(InvalidOperationException) { refused=true; }
                if(!refused) throw new Exception("Unknown verb accepted");
            }
            using(var commands=new ShellCommands(path,false,IntPtr.Zero)) {
                if(!commands.Has("properties")) throw new Exception("Folder properties verb absent");
            }
        } finally { if(System.IO.File.Exists(file)) System.IO.File.Delete(file); System.IO.Directory.Delete(path); }
    }
    internal void Execute(string verb)
    {
        uint id;
        if(!commands.TryGetValue(verb,out id)) throw new InvalidOperationException("Shell command unavailable");
        // Invoke only an enabled command discovered in THIS menu. No localized
        // verb guessing and no fallback to a different verb/default operation.
        var info=new InvokeInfo { Size=(uint)Marshal.SizeOf(typeof(InvokeInfo)),Mask=0x100,Owner=owner,Verb=new IntPtr(id),Show=1 };
        Check(Slot<Invoke>(context,4)(context,ref info)); // CMIC_MASK_NOASYNC
    }
    public void Dispose()
    { if(menu!=IntPtr.Zero) { DestroyMenu(menu); menu=IntPtr.Zero; } if(context!=IntPtr.Zero) { Marshal.Release(context); context=IntPtr.Zero; } }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFlags(IntPtr self,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetOwner(IntPtr self,IntPtr owner);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeleteItem(IntPtr self,IntPtr item,IntPtr sink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Perform(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Aborted(IntPtr self,[MarshalAs(UnmanagedType.Bool)] out bool value);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid cls,IntPtr outer,uint context,ref Guid iid,out IntPtr result);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name,IntPtr bc,ref Guid iid,out IntPtr item);
    internal static void Recycle(string path,IntPtr owner)
    {
        IntPtr operation=IntPtr.Zero,item=IntPtr.Zero;
        try {
            Guid cls=new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), iid=new Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
            Check(CoCreateInstance(ref cls,IntPtr.Zero,1,ref iid,out operation));
            // Recycle, record undo, and show a warning if recycling is impossible.
            // Never request silent permanent deletion or suppress error UI.
            Check(Slot<SetFlags>(operation,5)(operation,0x00080000|0x40|0x4000|0x20000000));
            Check(Slot<SetOwner>(operation,9)(operation,owner));
            iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Check(SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out item));
            Check(Slot<DeleteItem>(operation,18)(operation,item,IntPtr.Zero));
            Check(Slot<Perform>(operation,21)(operation));
            bool aborted; Check(Slot<Aborted>(operation,22)(operation,out aborted));
            if(aborted) throw new OperationCanceledException();
        } finally { if(item!=IntPtr.Zero) Marshal.Release(item); if(operation!=IntPtr.Zero) Marshal.Release(operation); }
    }
}
