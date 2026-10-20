using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;

// Run the production reflection loaders against a deliberately older settings schema.
// Small connection/message fixtures keep these checks independent of COM and live user settings.
int passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine("PASS " + message);
    passed++;
}

var source = new LegacyConnectionDefaults { ConDefaultRedirectDiskDrives = false, ConDefaultProtocol = "SSH2" };
var defaults = DefaultConnectionInfo.Instance;
defaults.Color = "preserved";
defaults.LoadFrom(source, name => "ConDefault" + name);
Check(defaults.RedirectDiskDrives == RDPDiskDrives.None, "Legacy false drive redirection loads as None");
Check(defaults.Protocol == TestProtocol.SSH2, "Existing enum defaults continue to load");
Check(defaults.Color == "preserved", "Missing newer default fields preserve initialized values");
source.ConDefaultRedirectDiskDrives = true;
defaults.LoadFrom(source, name => "ConDefault" + name);
Check(defaults.RedirectDiskDrives == RDPDiskDrives.All, "Legacy true drive redirection loads as All");

defaults.RedirectDiskDrives = RDPDiskDrives.None;
defaults.SaveTo(source, name => "ConDefault" + name);
Check(!source.ConDefaultRedirectDiskDrives, "None saves to the legacy false drive setting");
defaults.RedirectDiskDrives = RDPDiskDrives.All;
defaults.SaveTo(source, name => "ConDefault" + name);
Check(source.ConDefaultRedirectDiskDrives, "All saves to the legacy true drive setting");

var inheritance = DefaultConnectionInheritance.Instance;
inheritance.Color = true;
var inheritanceSettings = new LegacyInheritanceDefaults { InhDefaultDomain = true };
inheritance.LoadFrom(inheritanceSettings, name => "InhDefault" + name);
Check(inheritance.Domain && inheritance.Color, "Older inheritance settings load existing fields and preserve newer defaults");
inheritance.Domain = false;
inheritance.SaveTo(inheritanceSettings, name => "InhDefault" + name);
Check(!inheritanceSettings.InhDefaultDomain, "Existing inheritance settings still save");
Check(Runtime.MessageCollector.Errors.Count == 0, "Missing optional settings and legacy drive booleans produce no startup errors");

Console.WriteLine($"All {passed} isolated default-settings checks passed.");

internal sealed class LegacyConnectionDefaults
{
    public bool ConDefaultRedirectDiskDrives { get; set; }
    public string ConDefaultProtocol { get; set; } = "RDP";
}

internal sealed class LegacyInheritanceDefaults
{
    public bool InhDefaultDomain { get; set; }
}
