using System;
using System.Collections.Generic;
using System.Linq;

namespace XrayUI.Services;

/// <summary>Only a node ID crosses the Shell boundary; never a node's credentials or config.</summary>
public sealed record JumpListRequest(string ServerId)
{
    private const string ArgumentPrefix = "--connect-node=";

    public string ToArguments() => ArgumentPrefix + Uri.EscapeDataString(ServerId);

    public static JumpListRequest? Parse(IEnumerable<string> arguments)
    {
        var matches = arguments.Where(a => a.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        // An invalid explicit request must not turn into an ordinary/auto-connect launch: it
        // still yields a request, with an empty ID that matches no server.
        var value = matches.Length == 1 ? matches[0][ArgumentPrefix.Length..] : string.Empty;
        // UnescapeDataString leaves malformed escapes as-is rather than throwing.
        var id = value.Length <= 2048 ? Uri.UnescapeDataString(value) : string.Empty;
        return new(id.Length <= 512 && !id.Any(char.IsControl) ? id : string.Empty);
    }
}
