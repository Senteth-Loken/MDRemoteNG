using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string appDirectory = Path.GetFullPath(args.Length > 0
            ? args[0]
            : Path.Combine("mRemoteNG", "bin", "x64", "Publish Self-Contained"));
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string filename = new AssemblyName(request.Name).Name + ".dll";
            foreach (string candidate in new[] { Path.Combine(appDirectory, filename), Path.Combine(appDirectory, "Assemblies", filename) })
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            return null;
        };
        LicenseManager.CurrentContext = new TestDesignContext();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        var panels = new List<Form>();
        Type mainType = null;
        try
        {
            Assembly app = Assembly.LoadFrom(Path.Combine(appDirectory, "mRemoteNG.dll"));
            Type runtime = app.GetType("mRemoteNG.App.Runtime", true);
            object list = runtime.GetProperty("WindowList").GetValue(null);
            if (list == null) throw new Exception("Window registry is null before any panel/layout load.");
            Console.WriteLine("PASS Window registry exists before first panel/layout load.");
            mainType = app.GetType("mRemoteNG.UI.Forms.FrmMain", true);
            Type adderType = app.GetType("mRemoteNG.UI.Panels.PanelAdder", true);
            object adder = Activator.CreateInstance(adderType);
            Form panel = (Form)adderType.GetMethod("AddPanel").Invoke(adder, new object[] { "General", false });
            if (panel == null) throw new Exception("First panel creation failed.");
            panels.Add(panel);
            if (!(bool)adderType.GetMethod("DoesPanelExist").Invoke(adder, new object[] { "General" }))
                throw new Exception("The first General panel is not registered for reuse.");
            Console.WriteLine("PASS First General panel registers and can be found for reuse.");
            Form reused = (Form)adderType.GetMethod("AddPanel").Invoke(adder, new object[] { "gEnErAl", false });
            if (reused != null && !panels.Contains(reused)) panels.Add(reused);
            if (!ReferenceEquals(panel, reused))
                throw new Exception("A second General request created a duplicate panel.");
            Console.WriteLine("PASS Repeated General requests reuse the original panel and its contents.");
            Form defaultPanel = (Form)adderType.GetMethod("AddPanel").Invoke(adder, new object[] { "", false });
            if (defaultPanel != null && !panels.Contains(defaultPanel)) panels.Add(defaultPanel);
            if (!ReferenceEquals(panel, defaultPanel))
                throw new Exception("An unnamed panel request duplicated the General panel.");
            Console.WriteLine("PASS The default panel request also reuses General.");
            if (panel.Visible) throw new Exception("Probe unexpectedly displayed a panel.");
            Console.WriteLine("No windows shown; design-time theming avoids user settings writes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (Form panel in panels)
                if (!panel.IsDisposed) panel.Dispose();
            if (mainType != null && (bool)mainType.GetProperty("IsCreated").GetValue(null))
                ((Form)mainType.GetProperty("Default").GetValue(null)).Dispose();
        }
    }

    private sealed class TestDesignContext : LicenseContext
    {
        public override LicenseUsageMode UsageMode => LicenseUsageMode.Designtime;
    }
}
