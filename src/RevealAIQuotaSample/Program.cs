using Reveal.Sdk;
using Reveal.Sdk.AI;
using RevealAIQuotaSample.Demo;
using RevealAIQuotaSample.Identity;
using RevealAIQuotaSample.Quota;
using RevealAIQuotaSample.Reveal;

var builder = WebApplication.CreateBuilder(args);

// Quota accounting: options, clock and the SQLite store (singletons, shared by the gate).
builder.Services.Configure<QuotaOptions>(builder.Configuration.GetSection(QuotaOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<QuotaStore>();

// Who is calling. The X-User-Id header provider is a development-only shortcut; anywhere else
// the user comes from ASP.NET Core authentication (configure your scheme below).
var useDevelopmentUserHeader = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue<bool>("Sample:UseDevelopmentUserHeader");

builder.Services.AddControllers().AddReveal(reveal =>
{
    reveal.AddDataSourceProvider<SampleDataSourceProvider>();
    if (useDevelopmentUserHeader)
    {
        reveal.AddUserContextProvider<DevelopmentHeaderUserContextProvider>();
    }
    else
    {
        reveal.AddUserContextProvider<AuthenticatedUserContextProvider>();
    }
});

// Reveal AI. Provider connection, profiles (Model, MaxOutputTokens) and gate options are read
// from the "RevealAI" configuration section; the API key comes from user secrets or an
// environment variable, never from appsettings.json.
builder.Services.AddRevealAI()
    .AddOpenAI()
    .UseMetadataCatalogFile("Reveal/Metadata/catalog.json")
    .AddQuotaGate<DailyTokenQuotaGate>()   // authoritative: checks and settles every user AI call
    .AddUsageSink<UsageLogSink>();         // observability only: best-effort, never for billing

// Production: register your authentication scheme here (for example .AddJwtBearer(...)) and set
// RevealAI:AuthorizationPolicy so every Reveal AI endpoint requires a signed-in user.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

if (useDevelopmentUserHeader)
{
    app.Logger.LogWarning(
        "Development user header enabled for local requests: the user id is taken from the {Header} request header. Never enable this outside local testing.",
        DevelopmentHeaderUserContextProvider.HeaderName);

    // Development-only demo page (local requests only): http://localhost:5111/demo/
    app.UseDemoPage();
}

// No CORS policy is registered: browsers on other origins can't call these endpoints.
// The forwarded-headers middleware is deliberately not enabled either; if you put the app
// behind a reverse proxy, configure it with your proxy's addresses as KnownProxies.

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapQuotaEndpoints();

app.Run();
