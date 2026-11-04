using System.Net;

namespace RevealAIQuotaSample.Identity;

/// <summary>
/// Decides whether a request may use the development-only features (the X-User-Id identity, the
/// demo page and the /dev/quota endpoints). All of these must hold:
/// <list type="bullet">
/// <item>The TCP connection comes from a loopback address. This is the socket's own address:
/// the sample never enables the forwarded-headers middleware, so a client can't rewrite it.</item>
/// <item>The request carries no proxy forwarding headers. A request relayed by a local reverse
/// proxy or tunnel arrives from loopback but on behalf of someone else, so it is rejected.</item>
/// <item>The Host header names localhost, 127.0.0.1 or [::1]. This blocks DNS-rebinding pages
/// that reach the local server under another host name.</item>
/// </list>
/// </summary>
public static class LocalRequest
{
    /// <summary>Custom header the demo page sends on state-changing development calls. Browsers
    /// can't add it to a cross-site request without a CORS preflight, which this app never
    /// approves, so other sites can't trigger those calls.</summary>
    public const string DemoRequestHeader = "X-Demo-Request";

    private static readonly string[] ForwardingHeaders =
    [
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP", "X-Original-For",
    ];

    public static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return false;
        }

        foreach (var header in ForwardingHeaders)
        {
            if (context.Request.Headers.ContainsKey(header))
            {
                return false;
            }
        }

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || host == "127.0.0.1"
               || host == "[::1]" || host == "::1";
    }
}
