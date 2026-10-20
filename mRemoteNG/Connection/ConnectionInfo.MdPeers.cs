using System.ComponentModel;

namespace mRemoteNG.Connection
{
    public partial class ConnectionInfo
    {
        // Only authenticated, transient MDRemoteNG peer connections populate this pin.
        [Browsable(false)]
        public string MdVncCertificateFingerprint { get; set; } = "";
    }
}
