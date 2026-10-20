using System;
using System.Runtime.Versioning;
using System.Security;
using System.Xml.Linq;
using mRemoteNG.Connection;
using mRemoteNG.Security;

namespace mRemoteNG.Config.Serializers.ConnectionSerializers.Xml
{
    // ReSharper disable once InconsistentNaming
    [SupportedOSPlatform("windows")]
    public class XmlConnectionNodeSerializer29(ICryptographyProvider cryptographyProvider,
                                         SecureString encryptionKey,
                                         SaveFilter saveFilter)
        : XmlConnectionNodeSerializer28(cryptographyProvider, encryptionKey, saveFilter)
    {
        // Keep the XML reader's supported ceiling aligned with the current XML writer.
        // ConnectionsFileInfo.ConnectionFileVersion tracks the separate SQL format.
        public static Version CurrentVersion { get; } = new Version(2, 9);
        public override Version Version => CurrentVersion;

        protected override void SetElementAttributes(XContainer element, ConnectionInfo connectionInfo)
        {
            base.SetElementAttributes(element, connectionInfo);
            element.Add(new XAttribute("TerminalBackend", connectionInfo.TerminalBackend));
        }

        protected override void SetInheritanceAttributes(XContainer element, IInheritable connectionInfo)
        {
            base.SetInheritanceAttributes(element, connectionInfo);

            if (SaveInheritance && connectionInfo.Inheritance.TerminalBackend)
                element.Add(new XAttribute("InheritTerminalBackend", connectionInfo.Inheritance.TerminalBackend.ToString().ToLowerInvariant()));
        }
    }
}
