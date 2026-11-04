using System.Text.RegularExpressions;
using Reveal.Sdk;
using RevealAIQuotaSample.Quota;

namespace RevealAIQuotaSample.Identity;

/// <summary>
/// DEVELOPMENT ONLY. Takes the user id from the X-User-Id request header so you can simulate
/// several users locally. Any client can send any header value, so this is not authentication:
/// <list type="bullet">
/// <item>Program.cs registers it only in the Development environment, and only when
/// Sample:UseDevelopmentUserHeader is true. Every other environment uses
/// <see cref="AuthenticatedUserContextProvider"/>.</item>
/// <item>Even then, the header is honored only for requests from this machine
/// (<see cref="LocalRequest.IsLocal"/>). Remote, proxied or forwarded requests are anonymous,
/// and the quota gate denies anonymous users.</item>
/// </list>
/// </summary>
public sealed partial class DevelopmentHeaderUserContextProvider : IRVUserContextProvider
{
    public const string HeaderName = "X-User-Id";

    public IRVUserContext GetUserContext(HttpContext aspnetContext)
    {
        if (!LocalRequest.IsLocal(aspnetContext))
        {
            return new RVUserContext(DailyTokenQuotaGate.AnonymousUserId);
        }

        var value = aspnetContext.Request.Headers[HeaderName].ToString().Trim();
        var userId = UserIdPattern().IsMatch(value) && value != DailyTokenQuotaGate.AnonymousUserId
            ? value
            : DailyTokenQuotaGate.AnonymousUserId;
        return new RVUserContext(userId);
    }

    [GeneratedRegex("^[A-Za-z0-9._@-]{1,64}$")]
    private static partial Regex UserIdPattern();
}
