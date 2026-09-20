using System.Text.Json.Serialization;
using DigitalTwinSync.Experiment;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Программа моделирования и оценки защищённой синхронизации состояния цифрового двойника",
        Version = "1.0.0",
        Description = "API экспериментального программного стенда"
    });
});
builder.Services.AddSingleton<ExperimentRunner>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint(
        "/swagger/v1/swagger.json",
        "Программа моделирования и оценки защищённой синхронизации состояния цифрового двойника");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "API";
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
