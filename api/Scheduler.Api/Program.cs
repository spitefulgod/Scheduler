using Scheduler.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// CORS for the Angular client
string[] allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? new string[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<ISourceSecrets, SourceSecrets>();
builder.Services.AddSingleton<SolveService>();

var app = builder.Build();
// made now so runs left unfinished by the last stop are cleared before anyone asks
app.Services.GetRequiredService<SolveService>();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseCors();
app.MapControllers();

app.Run();

// Lets the integration tests start the API
public partial class Program { }
