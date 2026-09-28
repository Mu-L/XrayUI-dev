using System.Net;
using System.Text;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class RouteTestProtocolTests
    {
        // ── Input parsing ─────────────────────────────────────────────────────

        [Theory]
        [InlineData("google.com", "google.com", 443)]
        [InlineData("  Example.COM.  ", "example.com", 443)]
        [InlineData("example.com:8080", "example.com", 8080)]
        [InlineData("https://www.google.com/a?b=1#c", "www.google.com", 443)]
        [InlineData("socks5://user:pw@Host.Example:1080", "host.example", 1080)]
        [InlineData("_dmarc.example.com", "_dmarc.example.com", 443)]
        [InlineData("中文.com", "xn--fiq228c.com", 443)]
        public void TryParseTarget_Domain(string input, string domain, int port)
        {
            Assert.True(RouteTestProtocol.TryParseTarget(input, out var target));
            Assert.Equal(domain, target.Domain);
            Assert.Null(target.Ip);
            Assert.Equal(port, target.Port);
        }

        [Theory]
        [InlineData("1.1.1.1", "1.1.1.1", 443)]
        [InlineData("223.5.5.5:53", "223.5.5.5", 53)]
        [InlineData("http://8.8.8.8/dns-query", "8.8.8.8", 80)]
        [InlineData("http://[2001:db8::1]/", "2001:db8::1", 80)]
        [InlineData("2001:db8::1", "2001:db8::1", 443)]
        [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
        [InlineData("[::ffff:1.2.3.4]", "1.2.3.4", 443)]
        public void TryParseTarget_Ip(string input, string ip, int port)
        {
            Assert.True(RouteTestProtocol.TryParseTarget(input, out var target));
            Assert.Equal(IPAddress.Parse(ip), target.Ip);
            Assert.Null(target.Domain);
            Assert.Equal(port, target.Port);
        }

        [Theory]
        [InlineData("http://example.com", 80)]
        [InlineData("HTTP://example.com/path", 80)]
        [InlineData("ws://example.com", 80)]
        [InlineData("https://example.com", 443)]
        [InlineData("wss://example.com", 443)]
        [InlineData("ftp://example.com", 443)]
        [InlineData("http://example.com:8080", 8080)]
        [InlineData("https://example.com:80", 80)]
        public void TryParseTarget_UrlWithoutPort_UsesTheSchemesPort(string input, int port)
        {
            Assert.True(RouteTestProtocol.TryParseTarget(input, out var target));
            Assert.Equal(port, target.Port);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://")]
        [InlineData("a b.com")]
        [InlineData("a..b.com")]
        [InlineData("example.com:0")]
        [InlineData("example.com:65536")]
        [InlineData("example.com:http")]
        [InlineData("1.2.3.4:")]
        [InlineData("[2001:db8::1")]
        [InlineData("[1.2.3.4]:80")]
        [InlineData("2001:db8::zz")]
        public void TryParseTarget_RejectsInvalid(string? input)
        {
            Assert.False(RouteTestProtocol.TryParseTarget(input, out _));
        }

        // ── Encoding ──────────────────────────────────────────────────────────

        [Fact]
        public void EncodeTestRouteRequest_Domain_MatchesHandEncodedProtobuf()
        {
            var target = new RouteTestTarget("a.com", null, 443);

            var context = Concat(
                [0x0A, 0x08], Ascii("mixed-in"),   // 1 InboundTag
                [0x10, 0x02],                      // 2 Network = TCP
                [0x30, 0xBB, 0x03],                // 6 TargetPort = 443
                [0x3A, 0x05], Ascii("a.com"));     // 7 TargetDomain
            var message = Concat(
                [0x0A, (byte)context.Length], context,  // 1 RoutingContext
                [0x12, 0x08], Ascii("outbound"));       // 2 FieldSelectors

            Assert.Equal(
                Frame(message),
                RouteTestProtocol.EncodeTestRouteRequest(target, "mixed-in"));
        }

        [Fact]
        public void EncodeTestRouteRequest_Ipv4_WritesFourAddressBytesAndOmitsEmptyInboundTag()
        {
            var target = new RouteTestTarget(null, IPAddress.Parse("1.2.3.4"), 443);

            var context = Concat(
                [0x10, 0x02],                      // 2 Network = TCP
                [0x22, 0x04, 1, 2, 3, 4],          // 4 TargetIPs
                [0x30, 0xBB, 0x03]);               // 6 TargetPort = 443
            var message = Concat(
                [0x0A, (byte)context.Length], context,
                [0x12, 0x08], Ascii("outbound"));

            Assert.Equal(
                Frame(message),
                RouteTestProtocol.EncodeTestRouteRequest(target, null));
        }

        [Fact]
        public void EncodeTestRouteRequest_Ipv6_WritesSixteenAddressBytes()
        {
            var ip = IPAddress.Parse("2001:db8::1");
            var frame = RouteTestProtocol.EncodeTestRouteRequest(new RouteTestTarget(null, ip, 80), "tun-in");

            var expectedIpField = Concat([0x22, 0x10], ip.GetAddressBytes());
            Assert.True(frame.AsSpan().IndexOf(expectedIpField) > 0);
        }

        // ── Decoding ──────────────────────────────────────────────────────────

        [Fact]
        public void TryDecodeOutboundTag_SkipsOtherFieldsOfEveryWireType()
        {
            var message = Concat(
                [0x0A, 0x08], Ascii("mixed-in"),         // 1 InboundTag (length-delimited)
                [0x10, 0x02],                            // 2 Network (varint)
                [0x30, 0xBB, 0x03],                      // 6 TargetPort (multi-byte varint)
                [0x5A, 0x02], Ascii("g1"),               // 11 OutboundGroupTags
                [0x62, 0x06], Ascii("direct"),           // 12 OutboundTag
                [0xA5, 0x01, 1, 2, 3, 4],                // 20, fixed32 (unknown)
                [0xA9, 0x01, 1, 2, 3, 4, 5, 6, 7, 8]);   // 21, fixed64 (unknown)

            Assert.True(RouteTestProtocol.TryDecodeOutboundTag(Frame(message), out var tag));
            Assert.Equal("direct", tag);
        }

        [Fact]
        public void TryDecodeOutboundTag_RejectsMalformedFrames()
        {
            var valid = Concat([0x62, 0x05], Ascii("proxy"));

            Assert.False(RouteTestProtocol.TryDecodeOutboundTag([], out _));
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag([0, 0, 0], out _));
            // Compressed flag set.
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag(Concat([1, 0, 0, 0, (byte)valid.Length], valid), out _));
            // Declared length runs past the end of the buffer.
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag(Concat([0, 0, 0, 0, 0x20], valid), out _));
            // Field length runs past the end of the message.
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag(Frame([0x62, 0x09, (byte)'p']), out _));
            // Unterminated varint.
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag(Frame([0x10, 0x80]), out _));
            // Well-formed but without an OutboundTag.
            Assert.False(RouteTestProtocol.TryDecodeOutboundTag(Frame([0x10, 0x02]), out _));
        }

        // ── Classification ────────────────────────────────────────────────────

        [Theory]
        [InlineData("proxy", RouteTestOutboundKind.Proxy)]
        [InlineData("chain-entry", RouteTestOutboundKind.Proxy)]
        [InlineData("direct", RouteTestOutboundKind.Direct)]
        [InlineData("block", RouteTestOutboundKind.Block)]
        [InlineData("hk-balanced-1", RouteTestOutboundKind.Other)]
        public void Classify_MapsInjectedTags(string tag, RouteTestOutboundKind kind)
        {
            Assert.Equal(kind, RouteTestProtocol.Classify(tag));
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Frame(byte[] message) =>
            Concat([0, 0, 0, 0, (byte)message.Length], message);
    }
}
