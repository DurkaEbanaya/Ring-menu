using System;
using System.IO;
using System.Xml.Serialization;
using Microsoft.Win32;

public sealed class RingPreferences
{
    public int HoldMs=200, CommandDelayMs=300, MoreDelayMs=0, MoveToleranceDip=14, SelectionTimeoutSeconds=20;
    public bool Acrylic=true;
    public int AcrylicTint=44, SurfaceOpacity=13, RevealIntensity=100, GrainIntensity=100;
    internal static RingPreferences Current=new RingPreferences();
    internal RingPreferences Copy() { return (RingPreferences)MemberwiseClone(); }
    private static int Clamp(int n,int lo,int hi) { return Math.Max(lo,Math.Min(hi,n)); }
    internal void Normalize()
    {
        HoldMs=Clamp(HoldMs,100,1500); CommandDelayMs=Clamp(CommandDelayMs,0,1000); MoreDelayMs=Clamp(MoreDelayMs,0,1000);
        MoveToleranceDip=Clamp(MoveToleranceDip,6,40); SelectionTimeoutSeconds=Clamp(SelectionTimeoutSeconds,5,120);
        AcrylicTint=Clamp(AcrylicTint,0,100); SurfaceOpacity=Clamp(SurfaceOpacity,5,100);
        RevealIntensity=Clamp(RevealIntensity,0,100); GrainIntensity=Clamp(GrainIntensity,0,100);
    }
    internal static string SettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RingMenu","settings.xml"); } }
    internal static bool LoadFailed;
    internal static RingPreferences Load()
    {
        try {
            if(!File.Exists(SettingsPath)) return new RingPreferences();
            var info=new FileInfo(SettingsPath); if(info.Length>65536) throw new InvalidDataException();
            using(var reader=System.Xml.XmlReader.Create(SettingsPath,new System.Xml.XmlReaderSettings { DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null })) {
                var p=(RingPreferences)new XmlSerializer(typeof(RingPreferences)).Deserialize(reader);
                if(p==null) throw new InvalidOperationException("Empty settings"); p.Normalize(); return p;
            }
        } catch(IOException) { LoadFailed=true; }
          catch(UnauthorizedAccessException) { LoadFailed=true; }
          catch(InvalidOperationException) { LoadFailed=true; }
          catch(System.Xml.XmlException) { LoadFailed=true; }
        return new RingPreferences();
    }
    internal void Save()
    { SaveTo(SettingsPath); }
    private void SaveTo(string path)
    {
        Normalize(); var folder=Path.GetDirectoryName(path); Directory.CreateDirectory(folder);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(RingPreferences)).Serialize(stream,this);
            if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
        } finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void SelfTest()
    {
        var p=new RingPreferences { HoldMs=-1,CommandDelayMs=2000,SurfaceOpacity=0,AcrylicTint=1000,MoveToleranceDip=99 };
        p.Normalize(); if(p.HoldMs!=100 || p.CommandDelayMs!=1000 || p.SurfaceOpacity!=5 || p.AcrylicTint!=100 || p.MoveToleranceDip!=40) throw new Exception("Settings bounds failed");
        var serializer=new XmlSerializer(typeof(RingPreferences));
        using(var stream=new MemoryStream()) {
            serializer.Serialize(stream,p); stream.Position=0;
            var loaded=(RingPreferences)serializer.Deserialize(stream);
            if(loaded.HoldMs!=p.HoldMs || loaded.AcrylicTint!=p.AcrylicTint || loaded.Acrylic!=p.Acrylic) throw new Exception("Settings roundtrip failed");
        }
        var copy=p.Copy(); copy.HoldMs=400; if(p.HoldMs==copy.HoldMs) throw new Exception("Settings draft aliases live settings");
        string folder=Path.Combine(Path.GetTempPath(),"Ring-menu-settings-test-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(folder,"settings.xml");
        try {
            p.SaveTo(path); p.HoldMs=450; p.SaveTo(path);
            using(var stream=File.OpenRead(path)) {
                if(((RingPreferences)serializer.Deserialize(stream)).HoldMs!=450) throw new Exception("Atomic settings replace failed");
            }
            if(Directory.GetFiles(folder).Length!=1) throw new Exception("Settings temporary file leaked");
        } finally { if(File.Exists(path)) File.Delete(path); if(Directory.Exists(folder)) Directory.Delete(folder); }
    }
}

internal static class RingStartup
{
    private const string Run=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Expected { get { return "\""+System.Windows.Forms.Application.ExecutablePath+"\" --background"; } }
    internal static bool Enabled
    { get { using(var key=Registry.CurrentUser.OpenSubKey(Run)) return key!=null && String.Equals(key.GetValue("RingMenu") as string,Expected,StringComparison.Ordinal); } }
    internal static void Set(bool enabled)
    {
        using(var key=Registry.CurrentUser.CreateSubKey(Run)) {
            var existing=key.GetValue("RingMenu") as string;
            if(existing!=null && existing!=Expected) throw new InvalidOperationException("Autostart belongs to another location");
            if(enabled) key.SetValue("RingMenu",Expected,RegistryValueKind.String);
            else {
                // Never erase an unrelated value or another installation.
                var value=key.GetValue("RingMenu") as string;
                if(value!=null && value!=Expected) throw new InvalidOperationException("Autostart belongs to another location");
                key.DeleteValue("RingMenu",false);
            }
        }
    }
}
