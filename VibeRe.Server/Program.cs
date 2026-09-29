using VibeRe.Server;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
// DI
builder.Services.AddVibeReServices();
var app = builder.Build();

// Migrating SQUEELITE
await app.ApplyMigrationsAsync();


// Configure the HTTP request pipeline.
app.MapGet("/",
    () =>
        "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();