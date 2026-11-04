using RevealAIQuotaSample.Identity;

namespace RevealAIQuotaSample.Demo;

/// <summary>
/// Serves the development-only demo page from wwwroot/demo. It is registered only when the
/// development identity header is enabled (Development environment), and only answers local
/// requests; everything else gets 404. The page uses the public Reveal AI and quota endpoints.
/// </summary>
public static class DemoPage
{
    public const string BasePath = "/demo";

    public static void UseDemoPage(this WebApplication app)
    {
        app.MapGet("/", (HttpContext http) =>
            LocalRequest.IsLocal(http) ? Results.Redirect(BasePath + "/") : Results.NotFound());

        app.UseWhen(
            http => http.Request.Path.StartsWithSegments(BasePath),
            branch =>
            {
                branch.Use(async (http, next) =>
                {
                    if (!LocalRequest.IsLocal(http))
                    {
                        http.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    var headers = http.Response.Headers;
                    headers.ContentSecurityPolicy =
                        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                    headers.XContentTypeOptions = "nosniff";
                    headers.XFrameOptions = "DENY";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers.CacheControl = "no-store";
                    await next(http);
                });
                branch.UseDefaultFiles();
                branch.UseStaticFiles();
            });
    }
}
