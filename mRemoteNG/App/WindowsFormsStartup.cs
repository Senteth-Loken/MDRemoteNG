using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace mRemoteNG.App
{
    /// <summary>Configure WinForms before the PIN gate creates its first control.</summary>
    internal static class WindowsFormsStartup
    {
        internal static string SingleInstanceMutexName(string installationDirectory)
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory)).ToUpperInvariant();
            using var identity = WindowsIdentity.GetCurrent();
            string account = identity.User?.Value ?? Environment.UserName;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account + "\n" + directory)))[..24];
            return @"Local\MDRemoteNG.SingleInstance." + key;
        }

        internal static bool InitializeAndUnlock(Func<bool> unlock,
            ThreadExceptionEventHandler threadExceptionHandler,
            UnhandledExceptionEventHandler unhandledExceptionHandler)
        {
            ArgumentNullException.ThrowIfNull(unlock);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += threadExceptionHandler;
            AppDomain.CurrentDomain.UnhandledException += unhandledExceptionHandler;
            return unlock();
        }
    }
}
