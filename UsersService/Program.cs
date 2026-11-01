using Ucu.Andis.Common;
using Ucu.Andis.Users.Data;
using Ucu.Andis.Users.Soap;
using Microsoft.EntityFrameworkCore;
using SoapCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<UsersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("UsersDb")));

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<UsersDbContext>("users-db");

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-.
builder.AddObservability();

// Users expone SOAP en lugar de REST: SoapCore recibe los sobres XML, los
// despacha a UserService y publica el WSDL.
builder.Services.AddSoapCore();
builder.Services.AddScoped<IUserService, UserService>();

var app = builder.Build();

// La inyección de fallas va antes que el endpoint SOAP, para que también lo afecte.
app.UseChaos("/users");
app.MapMetrics();
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
// WebApplication es a la vez IApplicationBuilder e IEndpointRouteBuilder, y
// SoapCore tiene una sobrecarga para cada uno: se elige la de middleware.
((IApplicationBuilder)app).UseSoapEndpoint<IUserService>("/users/UserService.svc", new SoapEncoderOptions(), SoapSerializer.DataContractSerializer);

app.Run();
