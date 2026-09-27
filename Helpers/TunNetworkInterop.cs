using System;
using System.Net;
using System.Runtime.InteropServices;

namespace XrayUI.Helpers
{
    /// <summary>
    /// In-process IP Helper calls for TUN cleanup. <see cref="XrayUI.Services.TunService"/> used to
    /// do all of this by spawning netsh / route.exe, which costs ~115 ms / ~50 ms per command —
    /// about a second per TUN stop or node switch. Every call here is sub-millisecond. Deleting
    /// routes and changing adapter DNS need elevation; callers keep their netsh batch as the
    /// fallback for the unelevated (UAC) path and for any failure reported here.
    /// </summary>
    internal static unsafe partial class TunNetworkInterop
    {
        /// <summary>One active route: owning interface plus destination prefix.</summary>
        public readonly record struct RouteEntry(ulong InterfaceLuid, IPAddress Destination, int PrefixLength);

        private const ushort AF_UNSPEC = 0;
        private const ushort AF_INET = 2;
        private const ushort AF_INET6 = 23;
        private const uint ERROR_NOT_FOUND = 1168;

        // MIB_IPFORWARD_TABLE2 is { ULONG NumEntries; MIB_IPFORWARD_ROW2 Table[]; }, and the row
        // starts with a NET_LUID (8-byte aligned), so the first row sits at offset 8.
        private const int TableRowsOffset = 8;

        /// <summary>
        /// The fields of MIB_IPFORWARD_ROW2 this class reads (netioapi.h). DestinationPrefix is an
        /// IP_ADDRESS_PREFIX at offset 12: a SOCKADDR_INET (28 bytes) followed by PrefixLength.
        /// Inside the SOCKADDR_INET, an IPv4 sin_addr sits at +4 and an IPv6 sin6_addr at +8.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 104)]
        private struct MIB_IPFORWARD_ROW2
        {
            [FieldOffset(0)] public ulong InterfaceLuid;
            [FieldOffset(12)] public ushort DestinationFamily;
            [FieldOffset(16)] public fixed byte DestinationV4[4];
            [FieldOffset(20)] public fixed byte DestinationV6[16];
            [FieldOffset(40)] public byte DestinationPrefixLength;
        }

        private const uint DNS_INTERFACE_SETTINGS_VERSION1 = 1;
        private const ulong DNS_SETTING_IPV6 = 0x0001;
        private const ulong DNS_SETTING_NAMESERVER = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct DNS_INTERFACE_SETTINGS
        {
            public uint Version;
            public ulong Flags;
            public char* Domain;
            public char* NameServer;
            public char* SearchList;
            public uint RegistrationEnabled;
            public uint RegisterAdapterName;
            public uint EnableLLMNR;
            public uint QueryAdapterName;
            public char* ProfileNameServer;
        }

        /// <summary>
        /// Resolves an adapter alias (e.g. "xray-tun") to its LUID. False when no such adapter
        /// exists — the normal state before xray has created the TUN adapter. Windows reports an
        /// unknown alias as a generic ERROR_INVALID_PARAMETER, so any failure is read as absent.
        /// </summary>
        public static bool TryGetInterfaceLuid(string alias, out ulong luid)
        {
            ulong value;
            var status = ConvertInterfaceAliasToLuid(alias, &value);
            luid = status == 0 ? value : 0;
            return status == 0;
        }

        /// <summary>
        /// Deletes every active IPv4/IPv6 route <paramref name="match"/> accepts. Returns false
        /// when the route table can't be read or a matching route can't be deleted (a route that
        /// vanished in between counts as deleted); <paramref name="deleted"/> is set either way.
        /// </summary>
        public static bool TryDeleteRoutes(Func<RouteEntry, bool> match, out int deleted)
        {
            deleted = 0;
            byte* table;
            if (GetIpForwardTable2(AF_UNSPEC, &table) != 0)
                return false;

            var ok = true;
            try
            {
                var count = *(uint*)table;
                var rows = (MIB_IPFORWARD_ROW2*)(table + TableRowsOffset);
                for (var i = 0; i < count; i++)
                {
                    var row = rows + i;
                    if (!TryReadRoute(row, out var route) || !match(route))
                        continue;

                    var status = DeleteIpForwardEntry2(row);
                    if (status == 0 || status == ERROR_NOT_FOUND)
                        deleted++;
                    else
                        ok = false;
                }
            }
            finally
            {
                FreeMibTable(table);
            }

            return ok;
        }

        private static bool TryReadRoute(MIB_IPFORWARD_ROW2* row, out RouteEntry route)
        {
            IPAddress destination;
            switch (row->DestinationFamily)
            {
                case AF_INET:
                    destination = new IPAddress(new ReadOnlySpan<byte>(row->DestinationV4, 4));
                    break;
                case AF_INET6:
                    destination = new IPAddress(new ReadOnlySpan<byte>(row->DestinationV6, 16));
                    break;
                default:
                    route = default;
                    return false;
            }

            route = new RouteEntry(row->InterfaceLuid, destination, row->DestinationPrefixLength);
            return true;
        }

        /// <summary>
        /// Clears the statically configured IPv4 and IPv6 DNS servers on an adapter, handing it
        /// back to DHCP — what `netsh ... set dnsservers source=dhcp` does. False when
        /// SetInterfaceDnsSettings is unavailable (it is missing on older Windows 10 builds) or
        /// either call fails.
        /// </summary>
        public static bool TryClearDnsServers(ulong luid)
        {
            var setDns = SetInterfaceDnsSettingsExport.Value;
            if (setDns == IntPtr.Zero)
                return false;

            Guid guid;
            if (ConvertInterfaceLuidToGuid(&luid, &guid) != 0)
                return false;

            var fn = (delegate* unmanaged[Stdcall]<Guid, DNS_INTERFACE_SETTINGS*, uint>)setDns;
            char empty = '\0';
            var settings = new DNS_INTERFACE_SETTINGS
            {
                Version = DNS_INTERFACE_SETTINGS_VERSION1,
                Flags = DNS_SETTING_NAMESERVER,
                NameServer = &empty,
            };
            if (fn(guid, &settings) != 0)
                return false;

            settings.Flags = DNS_SETTING_NAMESERVER | DNS_SETTING_IPV6;
            return fn(guid, &settings) == 0;
        }

        // Looked up at runtime rather than bound with LibraryImport: the export does not exist on
        // every Windows version this app supports, and a missing entry point must degrade to the
        // netsh fallback instead of failing the call site.
        private static readonly Lazy<IntPtr> SetInterfaceDnsSettingsExport = new(() =>
            NativeLibrary.TryLoad("iphlpapi.dll", out var lib)
            && NativeLibrary.TryGetExport(lib, "SetInterfaceDnsSettings", out var export)
                ? export
                : IntPtr.Zero);

        [LibraryImport("iphlpapi.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint ConvertInterfaceAliasToLuid(string interfaceAlias, ulong* interfaceLuid);

        [LibraryImport("iphlpapi.dll")]
        private static partial uint ConvertInterfaceLuidToGuid(ulong* interfaceLuid, Guid* interfaceGuid);

        [LibraryImport("iphlpapi.dll")]
        private static partial uint GetIpForwardTable2(ushort family, byte** table);

        [LibraryImport("iphlpapi.dll")]
        private static partial uint DeleteIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

        [LibraryImport("iphlpapi.dll")]
        private static partial void FreeMibTable(void* memory);
    }
}
