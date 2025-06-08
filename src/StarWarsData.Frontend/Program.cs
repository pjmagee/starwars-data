using MudBlazor.Services;
using StarWarsData.Frontend.Components;
using StarWarsData.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services
    .AddMudServices()
    .AddHttpContextAccessor();
// TODO: Login
//.AddTransient<AuthorizationHandler>();

// Register a named HttpClient for the API service
builder.Services.AddHttpClient("StarWarsData", client => { client.BaseAddress = new Uri("http+https://apiservice:80"); });

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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();