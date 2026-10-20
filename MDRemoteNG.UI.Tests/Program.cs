using System.Reflection;
using System.Diagnostics;
using MDRemoteNG.Core;
using MDRemoteNG.Host;
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
            CheckHostConfiguration(Path.Combine(directory, "host-profile.dat"));
            CheckWindowsAccountConfiguration(Path.Combine(directory, "account-profile.dat"));
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

    private static void CheckHostConfiguration(string path)
    {
        var store = new ProfileStore(path);
        var profile = store.Create("UI#1");
        profile.DiscoveryEnabled = false;
        profile.AutoConnect = false;
        store.Save(profile);
        using (var empty = new MDNetworkSettingsDialog(store, store.Load()))
        {
            Check(Field<TextBox>(empty, "_username").Text == Environment.UserName &&
                Field<TextBox>(empty, "_domain").Text == Environment.UserDomainName &&
                Field<NumericUpDown>(empty, "_port").Value is >= 1 and <= 65535,
                "New host settings prefill the current Windows account and a valid local RDP port.");
            Check(Field<TextBox>(empty, "_password").Text.Length == 0 &&
                Field<Button>(empty, "_configureHostButton").Enabled,
                "A missing Windows password stays empty and host auto-configuration is available before sharing.");
        }

        profile = store.Load();
        profile.SharedConnection = new SharedConnection
        {
            Protocol = "SSH", Port = 2222, Username = "retained-user", Domain = "RETAINED", Password = "retained-password"
        };
        store.Save(profile);
        using (var existing = new MDNetworkSettingsDialog(store, store.Load()))
        {
            Check(Field<ComboBox>(existing, "_protocol").Text == "SSH" &&
                Field<NumericUpDown>(existing, "_port").Value == 2222 &&
                Field<TextBox>(existing, "_username").Text == "retained-user" &&
                Field<TextBox>(existing, "_domain").Text == "RETAINED" &&
                Field<TextBox>(existing, "_password").Text == "retained-password",
                "Opening settings preserves every previously enrolled endpoint field.");
        }

        var completion = new TaskCompletionSource<HostConfigurationResult>();
        int configureCalls = 0;
        int notifications = 0;
        using (var configured = new MDNetworkSettingsDialog(store, store.Load(), () =>
        {
            configureCalls++;
            return completion.Task;
        }))
        {
            configured.HostConfigured += (_, _) => notifications++;
            SetText(configured, "_displayName", "Unsaved display name");
            Task operation = InvokeHostSetup(configured);
            Check(!operation.IsCompleted && !Field<Button>(configured, "_save").Enabled &&
                !Field<Button>(configured, "_cancel").Enabled && !Field<Button>(configured, "_configureHostButton").Enabled &&
                !Field<Button>(configured, "_configureCurrentAccountButton").Enabled &&
                !Field<TextBox>(configured, "_password").Enabled && configured.UseWaitCursor,
                "An active host operation prevents concurrent saves and duplicate configuration clicks.");
            InvokeHostSetup(configured).GetAwaiter().GetResult();
            InvokeAction(configured, "OnSave");
            Check(configureCalls == 1 && store.Load().SharedConnection.Password == "retained-password",
                "Repeated configuration and save actions cannot change settings while setup is pending.");
            completion.SetResult(HostResult());
            CompleteUiTask(operation);
            Check(Field<ComboBox>(configured, "_protocol").Text == "RDP" &&
                Field<NumericUpDown>(configured, "_port").Value == 3391 &&
                Field<TextBox>(configured, "_username").Text == "mdrng_test" &&
                Field<TextBox>(configured, "_domain").Text == Environment.MachineName &&
                Field<TextBox>(configured, "_password").Text == "generated-test-password!23",
                "One completed host operation populates protocol, port, username, local domain and generated password.");
            Profile saved = store.Load();
            Check(saved.SharedConnection.Password == "generated-test-password!23" && saved.DiscoveryEnabled &&
                !saved.AutoConnect && saved.DisplayName != "Unsaved display name" && notifications == 1 &&
                configured.SavedProfile != null,
                "Host credentials and discovery are saved immediately without saving unrelated form edits.");
            Check(Field<Button>(configured, "_save").Enabled && Field<Button>(configured, "_cancel").Enabled &&
                Field<Button>(configured, "_configureHostButton").Enabled && !configured.UseWaitCursor &&
                Field<Label>(configured, "_hostStatus").Text.Contains("already saved", StringComparison.Ordinal) &&
                Field<Label>(configured, "_hostStatus").Text.Contains("Private", StringComparison.Ordinal),
                "Successful host setup restores controls and reports saved credentials and the network requirement.");
            configured.DialogResult = DialogResult.Cancel;
        }
        Check(store.Load().SharedConnection.Password == "generated-test-password!23",
            "Cancelling remaining dialog edits does not discard credentials already configured and saved.");

        using (var cancelled = new MDNetworkSettingsDialog(store, store.Load(), () =>
            Task.FromException<HostConfigurationResult>(new OperationCanceledException())))
        {
            CompleteUiTask(InvokeHostSetup(cancelled));
            Check(Field<TextBox>(cancelled, "_password").Text == "generated-test-password!23" &&
                Field<Label>(cancelled, "_hostStatus").Text.Contains("cancelled", StringComparison.Ordinal) &&
                Field<Button>(cancelled, "_cancel").Enabled &&
                store.Load().SharedConnection.Password == "generated-test-password!23",
                "Administrator-prompt cancellation preserves form and persisted credentials.");
        }
        using (var failed = new MDNetworkSettingsDialog(store, store.Load(), () =>
            Task.FromException<HostConfigurationResult>(new Exception("secret-password-must-not-appear"))))
        {
            CompleteUiTask(InvokeHostSetup(failed));
            Check(Field<TextBox>(failed, "_password").Text == "generated-test-password!23" &&
                !Field<Label>(failed, "_error").Text.Contains("secret-password-must-not-appear", StringComparison.Ordinal) &&
                Field<Button>(failed, "_configureHostButton").Enabled,
                "Unexpected setup failures preserve details and never display raw exception secrets.");
        }
        using (var unsupported = new MDNetworkSettingsDialog(store, store.Load(), () =>
            Task.FromException<HostConfigurationResult>(new HostSetupException("This Windows edition cannot host Remote Desktop."))))
        {
            CompleteUiTask(InvokeHostSetup(unsupported));
            Check(Field<Label>(unsupported, "_error").Text == "This Windows edition cannot host Remote Desktop." &&
                Field<Label>(unsupported, "_hostStatus").Text == Field<Label>(unsupported, "_error").Text,
                "Typed host setup failures provide an actionable safe error message.");
        }

        byte[] protectedProfile = File.ReadAllBytes(path);
        using (var retry = new MDNetworkSettingsDialog(store, store.Load(), () => Task.FromResult(HostResult())))
        {
            int retryNotifications = 0;
            retry.HostConfigured += (_, _) => retryNotifications++;
            File.Delete(path);
            CompleteUiTask(InvokeHostSetup(retry));
            Check(retry.SavedProfile == null && retryNotifications == 0 &&
                Field<TextBox>(retry, "_password").Text == "generated-test-password!23" &&
                Field<Label>(retry, "_error").Text.Contains("could not be saved", StringComparison.Ordinal) &&
                Field<Button>(retry, "_save").Enabled && Field<Button>(retry, "_cancel").Enabled,
                "A protected-profile save failure keeps generated credentials available and allows retry or cancellation.");
            File.WriteAllBytes(path, protectedProfile);
            InvokeAction(retry, "OnSave");
            Check(store.Load().SharedConnection.Username == "mdrng_test" && retryNotifications == 1,
                "A later Save retries the generated credentials and notifies discovery after persistence succeeds.");
        }
    }

    private static void CheckWindowsAccountConfiguration(string path)
    {
        const string testPassword = "test-only-existing-password!23";
        using (var prompt = new MDWindowsAccountPasswordDialog(@"TEST-PC\existing-user"))
        {
            Check(Field<TextBox>(prompt, "_password").UseSystemPasswordChar && prompt.Password.Length == 0,
                "The current-account password prompt masks input and starts with no captured password.");
            InvokeAction(prompt, "OnAccept");
            Check(prompt.DialogResult != DialogResult.OK && prompt.Password.Length == 0 &&
                Field<Label>(prompt, "_error").Text.Contains("Windows password", StringComparison.Ordinal),
                "An empty password cannot accept the current-account prompt.");
            SetText(prompt, "_password", testPassword);
            InvokeAction(prompt, "OnAccept");
            Check(prompt.DialogResult == DialogResult.OK && prompt.Password == testPassword,
                "The prompt returns only the explicitly entered Windows password for configuration.");
            prompt.Dispose();
            Check(prompt.Password.Length == 0 && Field<TextBox>(prompt, "_password").Text.Length == 0,
                "Disposing the password prompt clears both its input control and captured value.");
        }

        var store = new ProfileStore(path);
        var profile = store.Create("UI#1");
        profile.SharedConnection = new SharedConnection
        {
            Protocol = "SSH", Port = 2222, Username = "previous-user", Domain = "PREVIOUS", Password = "previous-password"
        };
        profile.DiscoveryEnabled = false;
        store.Save(profile);
        int configureCalls = 0;
        int promptCalls = 0;
        using (var cancelled = new MDNetworkSettingsDialog(store, store.Load(), configureCurrentAccount: _ =>
        {
            configureCalls++;
            return Task.FromResult(HostResult());
        }, requestCurrentAccountPassword: _ => { promptCalls++; return null; }))
        {
            CompleteUiTask(InvokeCurrentAccountSetup(cancelled));
            Check(promptCalls == 1 && configureCalls == 0 &&
                Field<TextBox>(cancelled, "_password").Text == "previous-password" &&
                Field<ComboBox>(cancelled, "_protocol").Text == "SSH" &&
                store.Load().SharedConnection.Password == "previous-password" &&
                !store.Load().DiscoveryEnabled &&
                Field<Label>(cancelled, "_hostStatus").Text.Contains("cancelled", StringComparison.Ordinal),
                "Cancelling the Windows-password prompt performs no host setup and preserves existing details.");
        }
        using (var empty = new MDNetworkSettingsDialog(store, store.Load(), configureCurrentAccount: _ =>
        {
            configureCalls++;
            return Task.FromResult(HostResult());
        }, requestCurrentAccountPassword: _ => ""))
        {
            CompleteUiTask(InvokeCurrentAccountSetup(empty));
            Check(configureCalls == 0 && Field<Label>(empty, "_error").Text.Contains("Windows Hello PIN", StringComparison.Ordinal),
                "An empty injected password is rejected before the configuration callback runs.");
        }

        var completion = new TaskCompletionSource<HostConfigurationResult>();
        string suppliedPassword = null;
        int notifications = 0;
        using (var configured = new MDNetworkSettingsDialog(store, store.Load(), configureCurrentAccount: password =>
        {
            configureCalls++;
            suppliedPassword = password;
            return completion.Task;
        }, requestCurrentAccountPassword: _ => testPassword))
        {
            configured.HostConfigured += (_, _) => notifications++;
            Task operation = InvokeCurrentAccountSetup(configured);
            Check(suppliedPassword == testPassword && configureCalls == 1 && !operation.IsCompleted &&
                !Field<Button>(configured, "_configureHostButton").Enabled &&
                !Field<Button>(configured, "_configureCurrentAccountButton").Enabled &&
                !Field<Button>(configured, "_save").Enabled && !Field<Button>(configured, "_cancel").Enabled,
                "Current-account setup passes the entered password once and disables both setup choices while working.");
            InvokeCurrentAccountSetup(configured).GetAwaiter().GetResult();
            Check(configureCalls == 1,
                "Repeated current-account setup cannot reopen the password prompt or run a second configuration.");
            completion.SetResult(new HostConfigurationResult
            {
                Protocol = "RDP", Port = 3392, Username = "existing-user", Domain = "TEST-PC", Password = testPassword,
                StatusMessage = "Use a Private or Domain network for discovery."
            });
            CompleteUiTask(operation);
            Profile saved = store.Load();
            Check(Field<ComboBox>(configured, "_protocol").Text == "RDP" &&
                Field<NumericUpDown>(configured, "_port").Value == 3392 &&
                Field<TextBox>(configured, "_username").Text == "existing-user" &&
                Field<TextBox>(configured, "_domain").Text == "TEST-PC" &&
                Field<TextBox>(configured, "_password").Text == testPassword &&
                saved.SharedConnection.Username == "existing-user" && saved.SharedConnection.Domain == "TEST-PC" &&
                saved.SharedConnection.Password == testPassword && saved.DiscoveryEnabled && notifications == 1,
                "Current-account setup fills every returned field and immediately saves credentials and discovery.");
            Check(Field<Label>(configured, "_hostStatus").Text.Contains("existing permissions", StringComparison.Ordinal) &&
                Field<Button>(configured, "_configureCurrentAccountButton").Enabled &&
                Field<Button>(configured, "_cancel").Enabled,
                "Current-account success explains existing permissions and restores the dialog actions.");
            configured.DialogResult = DialogResult.Cancel;
        }
        Check(store.Load().SharedConnection.Password == testPassword,
            "Cancelling remaining edits preserves the current-account credentials that were already saved.");

        using (var denied = new MDNetworkSettingsDialog(store, store.Load(), configureCurrentAccount: _ =>
            Task.FromException<HostConfigurationResult>(new OperationCanceledException()), requestCurrentAccountPassword: _ => testPassword))
        {
            CompleteUiTask(InvokeCurrentAccountSetup(denied));
            Check(Field<TextBox>(denied, "_password").Text == testPassword &&
                Field<Label>(denied, "_hostStatus").Text.Contains("cancelled", StringComparison.Ordinal) &&
                Field<Button>(denied, "_cancel").Enabled,
                "Cancelling current-account administrator approval preserves credentials and allows closing settings.");
        }
        using (var invalid = new MDNetworkSettingsDialog(store, store.Load(), configureCurrentAccount: _ =>
            Task.FromException<HostConfigurationResult>(new HostSetupException("The Windows password was not accepted.")),
            requestCurrentAccountPassword: _ => testPassword))
        {
            CompleteUiTask(InvokeCurrentAccountSetup(invalid));
            Check(Field<Label>(invalid, "_error").Text == "The Windows password was not accepted." &&
                Field<Label>(invalid, "_hostStatus").Text == Field<Label>(invalid, "_error").Text &&
                Field<TextBox>(invalid, "_password").Text == testPassword &&
                store.Load().SharedConnection.Password == testPassword &&
                Field<Button>(invalid, "_configureCurrentAccountButton").Enabled,
                "A rejected Windows password reports a safe error and preserves the saved connection.");
        }
    }

    private static HostConfigurationResult HostResult() => new()
    {
        Protocol = "RDP", Port = 3391, Username = "mdrng_test", Domain = Environment.MachineName,
        Password = "generated-test-password!23", StatusMessage = "Use a Private or Domain network for discovery."
    };

    private static Task InvokeHostSetup(Form dialog)
    {
        if (dialog.Visible || dialog.IsHandleCreated) throw new InvalidOperationException("Host checks must remain invisible.");
        return (Task)(dialog.GetType().GetMethod("ConfigureHostAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Host setup action not found.")).Invoke(dialog, null);
    }

    private static Task InvokeCurrentAccountSetup(Form dialog)
    {
        if (dialog.Visible || dialog.IsHandleCreated) throw new InvalidOperationException("Host checks must remain invisible.");
        return (Task)(dialog.GetType().GetMethod("ConfigureCurrentAccountHostAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Current-account setup action not found.")).Invoke(dialog, null);
    }

    private static void CompleteUiTask(Task operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!operation.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        if (!operation.IsCompleted) throw new TimeoutException("The simulated host operation did not complete.");
        operation.GetAwaiter().GetResult();
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
