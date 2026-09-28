using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    /// <summary>
    /// Asks the running core which outbound a destination would take, through the RoutingService
    /// the config builder exposes on loopback. gRPC is spoken directly over HttpClient: HTTP/2
    /// with prior knowledge (h2c), one unary call, protobuf handled by <see cref="RouteTestProtocol"/>.
    /// </summary>
    public static class RouteTestService
    {
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            // Loopback only, but never let a system proxy (possibly this app's own) sit in between.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            // Long enough for a domainStrategy that resolves the domain before matching IP rules.
            Timeout = TimeSpan.FromSeconds(8),
        };

        public static async Task<RouteTestResult> TestAsync(
            RouteTestEndpoint endpoint, RouteTestTarget target)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}{RouteTestProtocol.TestRoutePath}")
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new ByteArrayContent(
                        RouteTestProtocol.EncodeTestRouteRequest(target, endpoint.InboundTag)),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));

                using var response = await Http.SendAsync(request);
                var body = await response.Content.ReadAsByteArrayAsync();

                if (!response.IsSuccessStatusCode)
                    return Failed($"HTTP {(int)response.StatusCode}");

                var status = GrpcHeader(response, "grpc-status");
                if (status is null)
                    return Failed("no grpc-status in response");

                if (status != "0")
                {
                    var message = GrpcHeader(response, "grpc-message") is { } raw
                        ? Uri.UnescapeDataString(raw)
                        : string.Empty;

                    return message.Contains(RouteTestProtocol.NoClueMessage, StringComparison.Ordinal)
                        ? new RouteTestResult(RouteTestOutcome.NoRuleMatched, endpoint.DefaultOutboundTag)
                        : Failed(message.Length > 0 ? message : $"grpc-status {status}");
                }

                return RouteTestProtocol.TryDecodeOutboundTag(body, out var outboundTag)
                    ? new RouteTestResult(RouteTestOutcome.Matched, outboundTag)
                    : Failed("malformed TestRoute response");
            }
            catch (OperationCanceledException)
            {
                // HttpClient.Timeout surfaces as a cancellation.
                return Failed("timed out");
            }
            catch (HttpRequestException ex)
            {
                return Failed(ex.Message);
            }
        }

        private static RouteTestResult Failed(string detail) => new(RouteTestOutcome.Failed, detail);

        // A failed call is usually trailers-only: its status arrives in the one and only header
        // block rather than in trailers, so both are searched.
        private static string? GrpcHeader(HttpResponseMessage response, string name) =>
            HeaderValue(response.TrailingHeaders, name) ?? HeaderValue(response.Headers, name);

        private static string? HeaderValue(HttpHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
