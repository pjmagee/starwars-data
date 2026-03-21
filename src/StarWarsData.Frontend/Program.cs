using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using MudBlazor.Services;
using StarWarsData.Frontend;
using StarWarsData.Frontend.Components;
using StarWarsData.Frontend.Services;
using StarWarsData.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder
    .Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddKeycloakOpenIdConnect(
        "keycloak",
        realm: "starwars",
        options =>
        {
            options.ClientId = "starwars-frontend";
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.RequireHttpsMetadata = false;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
            options.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        var realmAccess = context.Principal.FindFirst("realm_access");
                        if (realmAccess is not null)
                        {
                            using var doc = JsonDocument.Parse(realmAccess.Value);
                            if (doc.RootElement.TryGetProperty("roles", out var roles))
                            {
                                foreach (var role in roles.EnumerateArray())
                                {
                                    var value = role.GetString();
                                    if (value is not null)
                                        identity.AddClaim(new Claim(ClaimTypes.Role, value));
                                }
                            }
                        }
                    }
                    return Task.CompletedTask;
                },
            };
        }
    );

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder
    .Services.AddMudServices()
    .AddHttpContextAccessor()
    .AddScoped<EndpointService>()
    .AddScoped<NavigationService>()
    .AddSingleton<GlobalFilterService>();

// Register a named HttpClient for the API service
// RemoveAllResilienceHandlers: SSE streaming is long-lived; Polly retries/timeouts don't apply
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
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

var app = builder.Build();

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
