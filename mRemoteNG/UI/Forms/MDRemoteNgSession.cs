using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;
using MDRemoteNG.Core;

namespace mRemoteNG.UI.Forms
{
    /// <summary>The interactive process cannot access connections until this session is unlocked.</summary>
    public static class MDRemoteNgSession
    {
        private static readonly ProfileStore Store = new();
        public static Profile Current { get; private set; }
        public static bool IsUnlocked => Current != null;
        public static event EventHandler SettingsChanged;

        public static bool Unlock()
        {
            using var dialog = new MDPinDialog(Store);
            if (dialog.ShowDialog() != DialogResult.OK)
                return false;
            Current = dialog.UnlockedProfile;
            return IsUnlocked;
        }

        public static bool ShowSettings(IWin32Window owner)
        {
            if (!IsUnlocked) return false;
            try
            {
                // Read the latest profile so persisted failure counters and concurrent
                // helper settings are not overwritten by a stale in-memory snapshot.
                using var dialog = new MDNetworkSettingsDialog(Store, Store.Load());
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                Current = dialog.SavedProfile;
                SettingsChanged?.Invoke(null, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is CryptographicException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                MessageBox.Show(owner, "MDRemoteNG could not read or save its protected profile. " +
                    "Check access to your Windows user profile and try again.", "MDRemoteNG settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
    }
}
