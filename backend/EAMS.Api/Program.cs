using EAMS.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Mock build: EF Core InMemory, seeded on startup. No DB install required.
builder.Services.AddDbContext<EamsDbContext>(o => o.UseInMemoryDatabase("EAMS"));

var app = builder.Build();

// Seed mock data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EamsDbContext>();
    SeedData.Initialize(db);
}

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "EAMS API v1");
    o.RoutePrefix = string.Empty; // Swagger UI at the root.
});

// Auth is stubbed for the mock build — endpoints are open.
app.MapControllers();

app.Run();
