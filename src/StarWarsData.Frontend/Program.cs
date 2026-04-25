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
    .AddScoped<LayoutService>();

// Register a named HttpClient for the API service
// SSE streaming is long-lived; the default 30s total timeout from StandardResilienceHandler kills it
builder.Services.AddScoped<UserIdDelegatingHandler>();
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
    .AddHttpMessageHandler<UserIdDelegatingHandler>();

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
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapLoginAndLogout();

app.Run();
