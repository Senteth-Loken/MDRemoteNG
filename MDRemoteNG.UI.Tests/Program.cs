using System.Reflection;
using MDRemoteNG.Core;
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
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The MDRemoteNG dialog checks require Windows.");
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ui-test-data"));
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            CheckStartupAndSettings(Path.Combine(directory, "profile.dat"));
            Console.WriteLine($"All {_passed} isolated dialog checks passed. No windows were displayed.");
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
