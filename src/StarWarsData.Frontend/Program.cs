using System.Security.Claims;
using System.Text.Json;
using Keycloak.AuthServices.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor;
using MudBlazor.Services;
using StarWarsData.Frontend;
using StarWarsData.Frontend.Components;
using StarWarsData.Frontend.Services;
using StarWarsData.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Blazor Interactive Server runs all UI over a SignalR circuit. The default
// MaximumReceiveMessageSize is 32 KB — any JSInterop call that pushes a larger
// payload (e.g. our 117 KB galaxy geography handed to D3) closes the circuit
// with "Connection closed with an error" and the JS function never runs even
// though C# reports the await as successful. We bump it generously and also
// give long-running JS calls a sensible timeout window.
builder
    .Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
    })
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

// builder.Services.AddTransient<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

builder
    .Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddKeycloakOpenIdConnect(
        "keycloak",
        realm: "starwars-data",
        options =>
        {
            options.ClientId = "starwars-frontend";
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.SaveTokens = true;
            options.RequireHttpsMetadata = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = "roles";
            options.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is not ClaimsIdentity identity)
                        return Task.CompletedTask;

                    // Avoid duplicates
                    if (identity.Claims.Any(c => c.Type == "roles"))
                        return Task.CompletedTask;

                    var realmAccess = identity.FindFirst("realm_access")?.Value;
                    if (!string.IsNullOrWhiteSpace(realmAccess))
                    {
                        using var doc = JsonDocument.Parse(realmAccess);

                        if (doc.RootElement.TryGetProperty("roles", out var rolesElement) && rolesElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var role in rolesElement.EnumerateArray())
                            {
                                var value = role.GetString();
                                if (!string.IsNullOrWhiteSpace(value))
                                {
                                    identity.AddClaim(new Claim("roles", value));
                                }
                            }
                        }
                    }

                    return Task.CompletedTask;
                },
            };
        }
    );

builder
    .Services.AddKeycloakAuthorization(options =>
    {
        options.RoleClaimType = ClaimTypes.Role;
        options.RolesResource = "starwars-frontend";
        options.EnableRolesMapping = RolesClaimTransformationSource.All;
    })
    .AddAuthorizationBuilder();

builder.Services.AddCascadingAuthenticationState();

builder
    .Services.AddMudServices()
    .AddMudMarkdownServices()
    .AddHttpContextAccessor()
    .AddScoped<EndpointService>()
    .AddScoped<NavigationService>()
    .AddScoped<GlobalFilterService>()
    .AddScoped<ChatHistoryService>()
    .AddScoped<LayoutService>()
    .AddScoped<PageContextService>()
    .AddScoped<PageControlService>()
    .AddScoped<GlobalCopilotToolsService>()
    .AddScoped<WookieepediaArticleModalService>()
    .AddGlobalCopilotTool<WookieepediaArticleToolFactory>();

// Register a named HttpClient for the API service
// SSE streaming is long-lived; the default 30s total timeout from StandardResilienceHandler kills it
builder.Services.AddScoped<UserIdDelegatingHandler>();
builder.Services.AddTransient<RateLimitMessageHandler>();
builder
    .Services.AddHttpClient(
        "StarWarsData",
        client =>
        {
            client.BaseAddress = new Uri("http+https://apiservice:80");
            client.Timeout = TimeSpan.FromMinutes(5);
        }
    )
    .RemoveAllResilienceHandlers()
    .AddHttpMessageHandler<UserIdDelegatingHandler>()
    // AGUIChatClient calls EnsureSuccessStatusCode() and discards the body. The
    // rate-limit handler runs first on the response path, converts a 429 into a
    // typed RateLimitedException carrying the JSON body, and lets Ask/Copilot
    // render the precise limit + retry-after instead of a generic message.
    .AddHttpMessageHandler<RateLimitMessageHandler>();

// HttpClient used by the Wookieepedia article proxy (Design-043). Same-origin
// iframe pulls article HTML from /wookieepedia/article, which calls Fandom's
// MediaWiki action=parse API with a polite UA. Separate from the "StarWarsData"
// client because it talks to a different host with different headers.
builder.Services.AddHttpClient(
    "Wookieepedia",
    client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
    }
);

var app = builder.Build();

// Route MudBlazor's internal lifecycle exceptions to our ILogger so they don't
// silently disappear. Without this hook, anything MudBlazor catches inside its
// own components (theme provider JS interop, dialog/snackbar lifecycle, etc.)
// goes to a default no-op handler and never reaches the Aspire log stream.
var mudLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MudBlazor");
MudBlazor.MudGlobal.UnhandledExceptionHandler = ex => mudLogger.LogError(ex, "MudBlazor unhandled exception");

app.MapGet(
        "/debug/claims",
        (ClaimsPrincipal user) =>
        {
            return Results.Json(
                user.Claims.Select(c => new
                {
                    c.Type,
                    c.Value,
                    c.ValueType,
                })
            );
        }
    )
    .RequireAuthorization();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseStaticFiles();

app.UseAuthentication();

// Dev-only auto-auth bypass: when running locally (Environment=Development) and
// no real Keycloak principal arrived from cookies, inject a synthetic authenticated
// user so AuthorizeView gates open without forcing the dev to round-trip through
// Keycloak just to click an admin-y button. Production / staging are untouched —
// the real OIDC flow remains the only way in.
//
// The principal carries the same claim shape the real login produces
// (`preferred_username`, `roles`) so downstream code (the X-User-Id DelegatingHandler,
// Keycloak roles transformation) doesn't need a special case.
if (app.Environment.IsDevelopment())
{
    app.Use(
        async (ctx, next) =>
        {
            if (ctx.User?.Identity?.IsAuthenticated != true)
            {
                var identity = new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "dev-local-user"), new Claim("preferred_username", "dev"), new Claim("roles", "admin") },
                    authenticationType: "DevAuthBypass",
                    nameType: "preferred_username",
                    roleType: "roles"
                );
                ctx.User = new ClaimsPrincipal(identity);
            }
            await next();
        }
    );
}

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapLoginAndLogout();

// Same-origin proxy serving cleaned Wookieepedia article HTML to the SP-4 modal
// iframe. See Design-043 + WookieepediaArticleProxy.cs for the rationale.
app.MapWookieepediaArticleProxy();

app.Run();
