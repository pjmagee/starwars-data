using MongoDB.Bson.Serialization;
using StarWarsData.Models;
using StarWarsData.Services.Data;
using StarWarsData.Services.Helpers;
using StarWarsData.Services.Mongo;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("SWDATA_");

builder.Services.AddResponseCompression();
builder.Services.AddResponseCaching();
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 5001);
builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", b => b
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services
    .AddEndpointsApiExplorer()
    .AddHttpContextAccessor()
    .AddSwaggerGen();

builder.Services
    .AddSingleton(builder.Configuration.GetSection("Settings").Get<Settings>()!)
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddScoped<RecordToEventsTransformer>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<BattleService>()
    .AddScoped<WarService>()
    .AddScoped<PowerService>()
    .AddScoped<CharacterService>();

BsonClassMap.RegisterClassMap(new RecordClassMap());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRouting();

app.UseCors("CorsPolicy");
app.UseResponseCompression();

app.UseResponseCaching();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");
await app.RunAsync();