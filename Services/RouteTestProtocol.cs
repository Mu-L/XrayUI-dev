using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace XrayUI.Services
{
    /// <summary>
    /// Where a running core answers route tests, plus two facts read back from the built config:
    /// the inbound tag test requests claim to arrive on, so inboundTag-scoped rules see the tag
    /// real traffic has (null when that inbound is untagged), and the first outbound's tag, which
    /// is where xray sends anything no rule matches. A class so XrayService can swap it from the
    /// process-exit callback and the UI thread can read it without tearing.
    /// </summary>
    public sealed record RouteTestEndpoint(int Port, string? InboundTag, string DefaultOutboundTag);

    /// <summary>A destination to ask the router about. Exactly one of Domain and Ip is set.</summary>
    public readonly record struct RouteTestTarget(string? Domain, IPAddress? Ip, int Port);

    public enum RouteTestOutcome
    {
        /// <summary>A rule matched. Detail is its outbound tag.</summary>
        Matched,
        /// <summary>No rule matched. Detail is the default outbound xray falls back to.</summary>
        NoRuleMatched,
        /// <summary>Detail is the error text.</summary>
        Failed,
    }

    /// <summary>What an outbound tag means to the user: the badge a result gets.</summary>
    public enum RouteTestOutboundKind
    {
        Proxy,
        Direct,
        Block,
        /// <summary>Some other outbound, e.g. one a config profile's balancer picked.</summary>
        Other,
    }

    public readonly record struct RouteTestResult(RouteTestOutcome Outcome, string Detail);

    /// <summary>
    /// Wire format for xray's RoutingService.TestRoute, which runs a destination through the live
    /// router — the same PickRoute real connections use, including domainStrategy DNS resolution
    /// and balancer selection. Protobuf is encoded by hand: one request and one field of the
    /// response do not justify Grpc.Tools codegen plus two runtime packages under AOT.
    ///
    /// Field numbers are from app/router/command/command.proto in Xray-core.
    /// </summary>
    public static class RouteTestProtocol
    {
        public const string TestRoutePath = "/xray.app.router.command.RoutingService/TestRoute";

        /// <summary>common.ErrNoClue, which PickRoute returns when no rule matches.</summary>
        public const string NoClueMessage = "not enough information for making a decision";

        private const int DefaultPort = 443;

        // xray.common.net.Network. Rules with network "tcp,udp" (the proxy fallback) only match
        // a context that names one of the two.
        private const ulong NetworkTcp = 2;

        // RoutingContext
        private const int CtxInboundTag   = 1;
        private const int CtxNetwork      = 2;
        private const int CtxTargetIps    = 4;
        private const int CtxTargetPort   = 6;
        private const int CtxTargetDomain = 7;
        private const int CtxOutboundTag  = 12;

        // TestRouteRequest
        private const int ReqRoutingContext = 1;
        private const int ReqFieldSelectors = 2;

        private const int WireVarint  = 0;
        private const int WireFixed64 = 1;
        private const int WireLength  = 2;
        private const int WireFixed32 = 5;

        /// <summary>
        /// Accepts what people paste: a bare domain or IP, host:port, [v6]:port, or a whole URL.
        /// A bare v6 address keeps all its colons; only the bracketed form carries a port.
        /// Without an explicit port a URL gets its scheme's port, so port rules see the one a
        /// real connection would use; a bare host gets <see cref="DefaultPort"/>.
        /// </summary>
        public static bool TryParseTarget(string? input, out RouteTestTarget target)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var s = input.Trim();
            var port = DefaultPort;

            var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                port = DefaultPortForScheme(s[..schemeEnd]);
                s = s[(schemeEnd + 3)..];
            }

            var pathStart = s.IndexOfAny(['/', '?', '#']);
            if (pathStart >= 0) s = s[..pathStart];

            var at = s.LastIndexOf('@');
            if (at >= 0) s = s[(at + 1)..];

            if (s.Length == 0) return false;

            if (s[0] == '[')
            {
                var close = s.IndexOf(']');
                if (close < 0) return false;

                var rest = s[(close + 1)..];
                if (rest.Length > 0 && (rest[0] != ':' || !TryParsePort(rest[1..], out port)))
                    return false;

                return TryParseV6(s[1..close], port, out target);
            }

            var firstColon = s.IndexOf(':');
            if (firstColon >= 0 && firstColon != s.LastIndexOf(':'))
                return TryParseV6(s, port, out target);

            var host = s;
            if (firstColon >= 0)
            {
                host = s[..firstColon];
                if (!TryParsePort(s[(firstColon + 1)..], out port)) return false;
            }

            if (IsDottedQuad(host) && IPAddress.TryParse(host, out var v4))
            {
                target = new RouteTestTarget(null, v4, port);
                return true;
            }

            if (!TryNormalizeDomain(host, out var domain)) return false;

            target = new RouteTestTarget(domain, null, port);
            return true;
        }

        public static RouteTestOutboundKind Classify(string outboundTag) => outboundTag switch
        {
            XrayConfigConstants.ProxyOutboundTag      => RouteTestOutboundKind.Proxy,
            XrayConfigConstants.ChainEntryOutboundTag => RouteTestOutboundKind.Proxy,
            XrayConfigConstants.DirectOutboundTag     => RouteTestOutboundKind.Direct,
            XrayConfigConstants.BlockOutboundTag      => RouteTestOutboundKind.Block,
            _                                         => RouteTestOutboundKind.Other,
        };

        /// <summary>
        /// A complete gRPC message frame (uncompressed flag + big-endian length + TestRouteRequest)
        /// asking for the outbound fields only. The context is always TCP: the question is where
        /// a connection to the target goes, and UDP-only rules (TUN's QUIC block) are a transport
        /// detail rather than a routing answer.
        /// </summary>
        public static byte[] EncodeTestRouteRequest(RouteTestTarget target, string? inboundTag)
        {
            var context = new List<byte>();
            if (!string.IsNullOrEmpty(inboundTag))
                WriteString(context, CtxInboundTag, inboundTag);
            WriteVarintField(context, CtxNetwork, NetworkTcp);
            if (target.Ip is { } ip)
                WriteBytes(context, CtxTargetIps, ip.GetAddressBytes());
            WriteVarintField(context, CtxTargetPort, (ulong)target.Port);
            if (!string.IsNullOrEmpty(target.Domain))
                WriteString(context, CtxTargetDomain, target.Domain);

            var message = new List<byte>();
            WriteBytes(message, ReqRoutingContext, CollectionsMarshal.AsSpan(context));
            WriteString(message, ReqFieldSelectors, "outbound");

            var frame = new byte[5 + message.Count];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Count);
            message.CopyTo(frame, 5);
            return frame;
        }

        /// <summary>
        /// Reads RoutingContext.OutboundTag out of a gRPC response frame, skipping every other
        /// field. Returns false instead of throwing on anything truncated or malformed.
        /// </summary>
        public static bool TryDecodeOutboundTag(ReadOnlySpan<byte> frame, out string outboundTag)
        {
            outboundTag = string.Empty;
            if (frame.Length < 5 || frame[0] != 0) return false;

            var length = BinaryPrimitives.ReadUInt32BigEndian(frame[1..5]);
            if (length > (uint)(frame.Length - 5)) return false;

            var message = frame.Slice(5, (int)length);
            string? found = null;

            while (message.Length > 0)
            {
                if (!TryReadVarint(ref message, out var key)) return false;

                switch ((int)(key & 7))
                {
                    case WireVarint:
                        if (!TryReadVarint(ref message, out _)) return false;
                        break;
                    case WireFixed64:
                        if (message.Length < 8) return false;
                        message = message[8..];
                        break;
                    case WireLength:
                        if (!TryReadVarint(ref message, out var size) || size > (ulong)message.Length) return false;
                        if (key >> 3 == CtxOutboundTag)
                            found = Encoding.UTF8.GetString(message[..(int)size]);
                        message = message[(int)size..];
                        break;
                    case WireFixed32:
                        if (message.Length < 4) return false;
                        message = message[4..];
                        break;
                    default:
                        return false;
                }
            }

            if (string.IsNullOrEmpty(found)) return false;

            outboundTag = found;
            return true;
        }

        // Schemes that don't use 443 by default. Anything unrecognised is treated like a bare host.
        private static int DefaultPortForScheme(string scheme) => scheme.ToLowerInvariant() switch
        {
            "http" or "ws" => 80,
            _              => DefaultPort,
        };

        private static bool TryParsePort(string text, out int port) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;

        // IPAddress.TryParse also takes shorthand like "10.1" or a bare "123" as v4 addresses.
        // Only the full four-part form counts as an IP here; anything else is read as a domain.
        private static bool IsDottedQuad(string host)
        {
            var dots = 0;
            foreach (var c in host)
            {
                if (c == '.') dots++;
                else if (c is < '0' or > '9') return false;
            }
            return dots == 3;
        }

        /// <summary>A v6 literal (the bracketed or bare form), IPv4-mapped ones reduced to v4.</summary>
        private static bool TryParseV6(string text, int port, out RouteTestTarget target)
        {
            target = default;
            if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            target = new RouteTestTarget(null, ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, port);
            return true;
        }

        private static bool TryNormalizeDomain(string host, out string domain)
        {
            domain = string.Empty;
            var name = host.TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0) return false;

            if (!Ascii.IsValid(name))
            {
                // geosite lists and xray's matchers work on the ASCII (punycode) form.
                try { name = new IdnMapping().GetAscii(name); }
                catch (ArgumentException) { return false; }
            }

            foreach (var label in name.Split('.'))
            {
                if (label.Length == 0) return false;
                foreach (var c in label)
                {
                    // Underscores are not valid in hostnames but do occur in real DNS names.
                    if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
                }
            }

            domain = name;
            return true;
        }

        private static void WriteVarint(List<byte> buffer, ulong value)
        {
            while (value >= 0x80)
            {
                buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }
            buffer.Add((byte)value);
        }

        private static void WriteKey(List<byte> buffer, int field, int wireType) =>
            WriteVarint(buffer, (ulong)((field << 3) | wireType));

        private static void WriteVarintField(List<byte> buffer, int field, ulong value)
        {
            WriteKey(buffer, field, WireVarint);
            WriteVarint(buffer, value);
        }

        private static void WriteBytes(List<byte> buffer, int field, ReadOnlySpan<byte> bytes)
        {
            WriteKey(buffer, field, WireLength);
            WriteVarint(buffer, (ulong)bytes.Length);
            buffer.AddRange(bytes);
        }

        private static void WriteString(List<byte> buffer, int field, string value) =>
            WriteBytes(buffer, field, Encoding.UTF8.GetBytes(value));

        private static bool TryReadVarint(ref ReadOnlySpan<byte> span, out ulong value)
        {
            value = 0;
            for (var i = 0; i < 10 && i < span.Length; i++)
            {
                var b = span[i];
                value |= (ulong)(b & 0x7F) << (7 * i);
                if (b < 0x80)
                {
                    span = span[(i + 1)..];
                    return true;
                }
            }
            return false;
        }
    }
}
