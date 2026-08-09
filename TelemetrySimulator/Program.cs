using TelemetrySimulator.Icd;
using TelemetrySimulator.Ingestion;
using TelemetrySimulator.Resolving;
using TelemetrySimulator.Services;
using TelemetrySimulator.Storage;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSingleton<UploadStore>();

// Services
builder.Services.AddScoped<UploadService>();
builder.Services.AddScoped<SimulationService>();

// singleton instances
builder.Services.AddSingleton<Encoder>();
builder.Services.AddSingleton<Resolver>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddSingleton<SimulationRegistry>();
builder.Services.AddSingleton<RecordReaderFactory>();


// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

string icdPath = Path.Combine(AppContext.BaseDirectory, "Icd", "MissionMapTable.json");
builder.Services.AddTransient(_ => IcdDocument.Load(File.ReadAllText(icdPath)));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();

