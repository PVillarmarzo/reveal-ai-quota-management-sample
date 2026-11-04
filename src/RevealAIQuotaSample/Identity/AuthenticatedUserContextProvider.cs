using System.Security.Claims;
using Reveal.Sdk;
using RevealAIQuotaSample.Quota;

namespace RevealAIQuotaSample.Identity;

/// <summary>
/// Production-style user context: the user id comes from the authenticated principal that your
/// ASP.NET Core authentication (cookies, JWT bearer, OpenID Connect, ...) established for the
/// request. Unauthenticated requests get the anonymous id, which the quota gate denies.
/// This sample does not configure an authentication scheme; add your own in Program.cs.
/// </summary>
public sealed class AuthenticatedUserContextProvider : IRVUserContextProvider
{
    public IRVUserContext GetUserContext(HttpContext aspnetContext)
    {
        var user = aspnetContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return new RVUserContext(DailyTokenQuotaGate.AnonymousUserId);
        }

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? user.FindFirstValue("sub")
                     ?? user.Identity.Name
                     ?? DailyTokenQuotaGate.AnonymousUserId;
        return new RVUserContext(userId);
    }
}
