using Microsoft.AspNetCore.Server.Kestrel.Core;
using StarWarsData.Models;
using StarWarsData.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("SWDATA_");

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

// app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();