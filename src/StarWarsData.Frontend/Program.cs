using System.Security.Claims;
using System.Text.Json;
using Keycloak.AuthServices.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MudBlazor.Services;
using StarWarsData.Frontend;
using StarWarsData.Frontend.Components;
using StarWarsData.Frontend.Services;
using StarWarsData.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

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

                        if (
                            doc.RootElement.TryGetProperty("roles", out var rolesElement)
                            && rolesElement.ValueKind == JsonValueKind.Array
                        )
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
    .AddHttpContextAccessor()
    .AddScoped<EndpointService>()
    .AddScoped<NavigationService>()
    .AddSingleton<GlobalFilterService>()
    .AddScoped<ChatHistoryService>()
    .AddScoped<CircuitTokenProvider>();

// Register a named HttpClient for the API service
// RemoveAllResilienceHandlers: SSE streaming is long-lived; Polly retries/timeouts don't apply
builder.Services.AddScoped<AuthTokenDelegatingHandler>();
#pragma warning disable EXTEXP0001
builder
    .Services.AddHttpClient(
        "StarWarsData",
        client =>
        {
            client.BaseAddress = new Uri("http+https://apiservice:80");
            client.Timeout = TimeSpan.FromMinutes(5);
        }
    )
    .AddHttpMessageHandler<AuthTokenDelegatingHandler>()
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

var app = builder.Build();

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
