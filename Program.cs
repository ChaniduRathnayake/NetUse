using System;
using System.Runtime.InteropServices;

// NetUse's own P/Invokes only ever load Windows DLLs from System32, never a same-named DLL dropped next to NetUse.exe
// (a portable copy can sit in a folder that any program running as you can write to).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace NetUse;

public static class Program
{
    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: the program's folder, System32 and explicitly added folders. Never the current
    // folder or PATH, where folders that standard users can write to (C:\Python3xx...) are common.
    [DllImport("kernel32.dll")] private static extern bool SetDefaultDllDirectories(uint flags);

    [STAThread]
    public static int Main(string[] args)
    {
        SetDefaultDllDirectories(0x1000);

        // "Start with Windows": the logon task runs this as SYSTEM, it starts the real app in the user's session.
        // Nothing of WPF is loaded in that mode.
        if (args.Length == 2 && args[0] == Launcher.Arg) return Launcher.Run(args[1]);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
