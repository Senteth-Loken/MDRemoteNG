using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MDRemoteNG.Discovery;

internal sealed record LocalSubnet(IPAddress Address, IPAddress Mask, int InterfaceIndex)
{
    public IPAddress Broadcast
    {
        get
        {
            byte[] address = Address.GetAddressBytes(), mask = Mask.GetAddressBytes();
            return new IPAddress(address.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
        }
    }
    public bool Contains(IPAddress candidate)
    {
        if (!NetworkScope.IsPrivateAddress(candidate)) return false;
        byte[] address = Address.GetAddressBytes(), mask = Mask.GetAddressBytes(), other = candidate.GetAddressBytes();
        return Enumerable.Range(0, 4).All(i => (address[i] & mask[i]) == (other[i] & mask[i]));
    }
}

internal static class NetworkScope
{
    // GUID return values cannot be marshalled through dynamic IDispatch. Keep
    // the native INetworkConnection vtable order when querying adapter IDs.
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        void GetNetwork([MarshalAs(UnmanagedType.Interface)] out object network);
        void GetIsConnectedToInternet([MarshalAs(UnmanagedType.VariantBool)] out bool connected);
        void GetIsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);
        void GetConnectivity(out int connectivity);
        void GetConnectionId(out Guid connectionId);
        void GetAdapterId(out Guid adapterId);
        void GetDomainType(out int domainType);
    }

    internal sealed record NetworkCategoryReport(bool Succeeded, int ConnectedNetworks, int PrivateNetworks,
        int DomainNetworks, HashSet<Guid> PrivateAdapters);
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
    }

    public static IReadOnlyList<LocalSubnet> GetAllowedSubnets()
    {
        if (!OperatingSystem.IsWindows()) return [];
        HashSet<Guid> privateAdapters = InspectNetworkCategories().PrivateAdapters;
        var result = new List<LocalSubnet>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !Guid.TryParse(adapter.Id, out Guid adapterId) || !privateAdapters.Contains(adapterId)) continue;
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                int index = properties.GetIPv4Properties().Index;
                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    if (IsPrivateAddress(address.Address) && address.IPv4Mask is not null &&
                        !address.IPv4Mask.Equals(IPAddress.Any) && !address.IPv4Mask.Equals(IPAddress.Broadcast))
                        result.Add(new LocalSubnet(address.Address, address.IPv4Mask, index));
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }

    // Windows' network category is checked in addition to RFC1918 addresses: public Wi-Fi
    // commonly also uses private IP addresses. Failure to determine a category disables LAN IO.
    internal static NetworkCategoryReport InspectNetworkCategories()
    {
        var result = new HashSet<Guid>();
        object? manager = null;
        int connected = 0, privateNetworks = 0, domainNetworks = 0;
        bool succeeded = false;
        try
        {
            Type? type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return new(false, 0, 0, 0, result);
            manager = Activator.CreateInstance(type);
            dynamic networkList = manager!;
            object networksObject = networkList.GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED
            try
            {
                foreach (object networkObject in (IEnumerable)networksObject)
                {
                    try
                    {
                        dynamic network = networkObject;
                        int category = (int)network.GetCategory();
                        connected++;
                        if (category == 1) privateNetworks++;
                        if (category == 2) domainNetworks++;
                        if (category is not (1 or 2)) continue; // Private or domain authenticated
                        object connections = network.GetNetworkConnections();
                        try
                        {
                            foreach (object connectionObject in (IEnumerable)connections)
                            {
                                try
                                {
                                    ((INetworkConnection)connectionObject).GetAdapterId(out Guid adapterId);
                                    result.Add(adapterId);
                                }
                                finally { Release(connectionObject); }
                            }
                        }
                        finally { Release(connections); }
                    }
                    finally { Release(networkObject); }
                }
            }
            finally { Release(networksObject); }
            succeeded = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or
                                  Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            result.Clear();
        }
        finally { if (manager is not null) Release(manager); }
        return new(succeeded, connected, privateNetworks, domainNetworks, result);
    }

    private static void Release(object value)
    {
        if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
