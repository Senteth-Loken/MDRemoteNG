using System.Reflection;

namespace mRemoteNG.Connection
{
    public enum TestProtocol { RDP, SSH2 }

    public class ConnectionInfo
    {
        public bool IsDefault { get; set; }
        public ConnectionInfoInheritance Inheritance { get; set; }
        public string Color { get; set; } = "";
        public Protocol.RDP.RDPDiskDrives RedirectDiskDrives { get; set; }
        public TestProtocol Protocol { get; set; }

        public IEnumerable<PropertyInfo> GetSerializableProperties() => typeof(ConnectionInfo).GetProperties()
            .Where(property => property.Name is not (nameof(IsDefault) or nameof(Inheritance)));
    }

    public class ConnectionInfoInheritance
    {
        public bool Color { get; set; }
        public bool Domain { get; set; }
        public ConnectionInfoInheritance(ConnectionInfo parent, bool ignoreDefaultInheritance) { }
        public IEnumerable<PropertyInfo> GetProperties() => typeof(ConnectionInfoInheritance).GetProperties();
    }
}

namespace mRemoteNG.App
{
    public static class Runtime
    {
        public static TestMessageCollector MessageCollector { get; } = new();
    }

    public sealed class TestMessageCollector
    {
        public List<string> Errors { get; } = [];
        public void AddMessage(Messages.MessageClass kind, string message, bool popup = false)
        {
            if (kind == Messages.MessageClass.ErrorMsg) Errors.Add(message);
        }
        public void AddExceptionStackTrace(string message, Exception error) => Errors.Add(message + ": " + error.Message);
    }
}

namespace mRemoteNG.Messages
{
    public enum MessageClass { DebugMsg, ErrorMsg }
}

namespace mRemoteNG.Tools
{
    public static class LocalizedAttributes
    {
        [AttributeUsage(AttributeTargets.Field)]
        public sealed class LocalizedDescriptionAttribute(string description) : Attribute
        {
            public string Description { get; } = description;
        }
    }
}

namespace mRemoteNG.Resources.Language
{
    public static class Language
    {
        public const string RdpDrivesNone = "None";
        public const string RdpDrivesLocal = "Local";
        public const string RdpDrivesAll = "All";
        public const string RdpDrivesCustom = "Custom";
    }
}
