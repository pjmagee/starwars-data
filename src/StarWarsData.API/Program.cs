using StarWarsData.Models;
using StarWarsData.Services;

var builder = WebApplication.CreateBuilder();

builder.Configuration.AddEnvironmentVariables("SWDATA_");

builder.Services.AddResponseCompression();
builder.Services.AddResponseCaching();
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 5001);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<RecordsService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Settings").Get<Settings>()!);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger(options =>
    {
        
    });
    
    app.UseSwaggerUI(options =>
    {
        
    });
}

app.UseResponseCompression();
app.UseResponseCaching();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();