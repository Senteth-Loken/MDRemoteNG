using System.Reflection;
using System.Diagnostics;
using MDRemoteNG.Core;
using mRemoteNG.App;
using mRemoteNG.UI.Forms;

namespace MDRemoteNG.UI.Tests;

/// <summary>
/// Exercises the actual dialog actions without showing windows or loading the app.
/// Every profile is isolated beneath this test executable's output directory.
/// </summary>
internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The MDRemoteNG dialog checks require Windows.");
            return 1;
        }

        if (arguments.SequenceEqual(new[] { "--cancel-startup" }))
            return WindowsFormsStartup.InitializeAndUnlock(() => false, (_, _) => { }, (_, _) => { }) ? 1 : 0;

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ui-test-data"));
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckStartupWindowTransition();
            CheckSingleInstanceIdentity();
            CheckStartupAndSettings(Path.Combine(directory, "profile.dat"));
            CheckCancelledStartup();
            Console.WriteLine($"All {_passed} isolated startup and dialog checks passed. No windows were displayed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            return 1;
        }
        finally
        {
            // The generated profile contains test-only secrets; remove it even after a failure.
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CheckStartupWindowTransition()
    {
        bool unlocked = WindowsFormsStartup.InitializeAndUnlock(() =>
        {
            // A real PIN dialog creates native controls even after it is closed.
            // Exercise that lifecycle without showing a dialog or using a live PIN.
            using var pinWindow = new Form { ShowInTaskbar = false };
            Check(pinWindow.Handle != IntPtr.Zero,
                "The unlock callback can create its native window after WinForms initialization.");
            return true;
        }, (_, _) => { }, (_, _) => { });
        Check(unlocked, "Successful PIN unlock continues the startup gate.");
        using var mainWindow = new Form { ShowInTaskbar = false };
        Check(mainWindow.Handle != IntPtr.Zero,
            "The main window can be created after the PIN window has closed.");
    }

    private static void CheckSingleInstanceIdentity()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "instance-probe");
        string key = WindowsFormsStartup.SingleInstanceMutexName(path);
        Check(key.StartsWith(@"Local\MDRemoteNG.SingleInstance.", StringComparison.Ordinal),
            "MDRemoteNG has its own instance lock rather than sharing the upstream lock.");
        Check(key == WindowsFormsStartup.SingleInstanceMutexName(path.ToUpperInvariant() + Path.DirectorySeparatorChar),
            "Equivalent Windows installation paths share one instance lock.");
        Check(key != WindowsFormsStartup.SingleInstanceMutexName(path + "-other"),
            "Another MDRemoteNG installation cannot silently suppress this installation's window.");
    }

    private static void CheckCancelledStartup()
    {
        // WinForms initialization must run before this process has created controls.
        // A fresh process exercises the cancellation branch with the same production helper.
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--cancel-startup");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start cancellation check.");
        if (!child.WaitForExit(15000))
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The cancelled startup did not exit.");
        }
        Check(child.ExitCode == 0, "Cancelled PIN unlock stops startup without creating the main window.");
    }

    private static void CheckStartupAndSettings(string path)
    {
        var store = new ProfileStore(path);
        using (var dialog = new MDPinDialog(store))
        {
            SetText(dialog, "_pin", "abc");
            SetText(dialog, "_confirmation", "abc");
            InvokeAction(dialog, "OnAccept");
            Check(!store.Exists && dialog.UnlockedProfile == null,
                "An invalid PIN leaves startup locked and writes no profile.");

            SetText(dialog, "_pin", "Ab#1");
            SetText(dialog, "_confirmation", "aaaa");
            InvokeAction(dialog, "OnAccept");
            Check(!store.Exists && dialog.UnlockedProfile == null,
                "Mismatched confirmation creates no profile.");

            SetText(dialog, "_confirmation", "aB#1");
            InvokeAction(dialog, "OnAccept");
            Check(store.Exists && dialog.UnlockedProfile != null,
                "Case-insensitive confirmation accepts a PIN containing a symbol.");
        }

        using (var dialog = new MDPinDialog(store))
        {
            Check(dialog.UnlockedProfile == null,
                "Opening an existing profile requires a fresh unlock.");
            SetText(dialog, "_pin", "ab#1");
            InvokeAction(dialog, "OnAccept");
            Check(dialog.UnlockedProfile != null,
                "A matching case-insensitive PIN unlocks the existing profile.");
        }

        using (var settings = new MDNetworkSettingsDialog(store, store.Load()))
        {
            SetText(settings, "_displayName", "Isolated UI Test Computer");
            Field<CheckBox>(settings, "_share").Checked = true;
            SetText(settings, "_username", "TestLocalUser");
            SetText(settings, "_password", "test-only-endpoint-password");
            Field<CheckBox>(settings, "_changePin").Checked = true;
            SetText(settings, "_currentPin", "AB#1");
            SetText(settings, "_newPin", "xY$2");
            SetText(settings, "_confirmPin", "Xy$2");
            InvokeAction(settings, "OnSave");
            var endpoint = settings.SavedProfile?.SharedConnection;
            Check(endpoint?.Username == "TestLocalUser" && endpoint.Protocol == "RDP" &&
                endpoint.Password == "test-only-endpoint-password",
                "Settings saves the explicitly enrolled local endpoint credentials.");
        }

        var saved = store.Unlock("XY$2");
        Check(saved.DisplayName == "Isolated UI Test Computer" && saved.SharedConnection?.Username == "TestLocalUser",
            "PIN changes and settings persist together in the protected profile.");
    }

    private static void SetText(object dialog, string name, string value) => Field<TextBox>(dialog, name).Text = value;

    private static T Field<T>(object dialog, string name) where T : Control
    {
        var field = dialog.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Dialog field not found: " + name);
        return (T)field.GetValue(dialog);
    }

    private static void InvokeAction(Form dialog, string name)
    {
        // Showing a modal form would require interactive desktop automation. Invoke
        // its real button handler while the controls and form remain invisible.
        if (dialog.Visible || dialog.IsHandleCreated)
            throw new InvalidOperationException("Dialog checks must not create visible windows.");
        var action = dialog.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Dialog action not found: " + name);
        action.Invoke(dialog, new object[] { null, EventArgs.Empty });
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _passed++;
        Console.WriteLine("PASS " + description);
    }
}
