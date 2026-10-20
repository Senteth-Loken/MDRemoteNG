using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace MDRemoteNG.Connection.Tests;

// Load the built app instead of referencing its COM-dependent project. This harness
// compiles independently and exercises the production XML reader/writer in memory.
internal static class Program
{
    private static int _passed;
    private static Assembly _application = null!;

    [STAThread]
    private static int Main(string[] arguments)
    {
        string folder = Path.GetFullPath(arguments.FirstOrDefault() ??
            Path.Combine(Directory.GetCurrentDirectory(), "mRemoteNG", "bin", "x64", "Publish Self-Contained"));
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string directory in new[] { folder, Path.Combine(folder, "Assemblies"), Path.Combine(folder, "Plugins") })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
            }
            return null;
        };
        try
        {
            _application = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "mRemoteNG.dll"));
            CheckSupportedCeiling();
            CheckRoundTrip(false, false);
            CheckRoundTrip(true, false);
            CheckRoundTrip(false, true);
            Console.WriteLine($"All {_passed} production connection-format checks passed. No connection files were written.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception.GetBaseException().Message);
            return 1;
        }
    }

    private static Type AppType(string name) => _application.GetType("mRemoteNG." + name, throwOnError: true)!;
    private static dynamic Create(string name, params object?[] parameters) =>
        Activator.CreateInstance(AppType(name), parameters) ?? throw new InvalidOperationException("Cannot create " + name);

    private static void CheckRoundTrip(bool fullEncryption, bool legacy28)
    {
        dynamic model = Create("Tree.ConnectionTreeModel");
        object rootKind = Enum.Parse(AppType("Tree.Root.RootNodeType"), "Connection");
        dynamic root = Create("Tree.Root.RootNodeInfo", rootKind);
        root.Name = "format-regression-root";
        dynamic folder = Create("Container.ContainerInfo");
        folder.Name = "nested-test-folder";
        dynamic connection = Create("Connection.ConnectionInfo");
        connection.Name = "format-regression-connection";
        connection.Hostname = "192.0.2.10";
        connection.Username = "test-format-user";
        connection.Password = "test-only-format-password";
        connection.Domain = "TEST";
        connection.Panel = "format-test-panel";
        connection.EnvironmentTags = "regression,xml";
        object terminalBackend = Enum.Parse(AppType("Connection.Protocol.Terminal.TerminalBackend"), "WindowsTerminal");
        AppType("Connection.ConnectionInfo").GetProperty("TerminalBackend")!.SetValue(connection, terminalBackend);
        model.AddRootNode(root);
        root.AddChild(folder);
        folder.AddChild(connection);
        dynamic provider = Create("Security.SymmetricEncryption.AeadCryptographyProvider");
        dynamic factory = Create("Config.Serializers.ConnectionSerializers.Xml.XmlConnectionSerializerFactory");
        dynamic serializer = factory.Build(provider, model, null, fullEncryption);
        string xml = serializer.Serialize(model);
        var document = XDocument.Parse(xml);
        string version = document.Root?.Attribute("ConfVersion")?.Value ?? "";
        Check(version == "2.9", "The current XML writer advertises format 2.9.");
        if (legacy28)
        {
            // Preserve the XML declaration expected by legacy full-file detection.
            xml = xml.Replace("ConfVersion=\"2.9\"", "ConfVersion=\"2.8\"", StringComparison.Ordinal);
        }
        dynamic reader = Create("Config.Serializers.ConnectionSerializers.Xml.XmlConnectionsDeserializer", new object?[] { null });
        dynamic restored = reader.Deserialize(xml, true);
        Check(restored != null, "The production XML reader accepts " + (legacy28 ? "legacy 2.8" : "its current 2.9") +
            (fullEncryption ? " with full-file encryption." : "."));
        dynamic restoredRoot = restored!.RootNodes[0];
        dynamic restoredFolder = restoredRoot.Children[0];
        dynamic restoredConnection = restoredFolder.Children[0];
        Check(restoredRoot.Name == root.Name && restoredFolder.Name == folder.Name &&
            restoredConnection.Name == connection.Name && restoredConnection.Hostname == connection.Hostname,
            "The XML round trip preserves nesting and the endpoint.");
        Check(restoredConnection.Username == connection.Username && restoredConnection.Password == connection.Password &&
            restoredConnection.Domain == connection.Domain, "The XML round trip preserves encrypted credentials.");
        Check(restoredConnection.TerminalBackend.Equals(terminalBackend) && restoredConnection.EnvironmentTags == connection.EnvironmentTags,
            "The XML round trip preserves terminal backend and environment tags.");
    }

    private static void CheckSupportedCeiling()
    {
        Type reader = AppType("Config.Serializers.ConnectionSerializers.Xml.XmlConnectionsDeserializer");
        object? raw = reader.GetField("MaxSupportedConfVersion", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        double ceiling = raw is double value ? value : throw new InvalidOperationException("XML compatibility ceiling unavailable.");
        Check(ceiling >= 2.9, "The production XML reader accepts the format emitted by the current writer.");
        var writerVersion = (Version)AppType("Config.Serializers.ConnectionSerializers.Xml.XmlConnectionNodeSerializer29")
            .GetProperty("CurrentVersion", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Check(ceiling == double.Parse(writerVersion.ToString(2), System.Globalization.CultureInfo.InvariantCulture),
            "The XML reader ceiling is tied to the current XML writer.");
        Check(ceiling < 3.0, "Newer XML formats remain unsupported; the SQL 3.2 format is separate.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _passed++;
        Console.WriteLine("PASS " + description);
    }
}
