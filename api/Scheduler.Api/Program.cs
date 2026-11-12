using Microsoft.AspNetCore.Authentication.Negotiate;
using Scheduler.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// CORS for the Angular client, with credentials so the browser sends the Windows sign in
string[] allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? new string[] { "http://localhost:4200" };
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Domain users sign in with Windows (Negotiate, Kerberos or NTLM), Security:Mode Development signs in as Security:DevUser
string securityMode = builder.Configuration["Security:Mode"] ?? "Windows";
if (securityMode == DevelopmentSignIn.SchemeName)
{
    if (!builder.Environment.IsDevelopment())
        throw new Exception("Security:Mode Development is only allowed in the Development environment, a server has to use Windows sign in");
    builder.Services.AddAuthentication(DevelopmentSignIn.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevelopmentSignIn>(DevelopmentSignIn.SchemeName, null);
}
else
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
// every call needs a signed in user, then each action's ProfileAccess says what else
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});

builder.Services.AddControllers(options =>
{
    options.Filters.Add(new AccessRequiredFilter());
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<ISourceSecrets, SourceSecrets>();
builder.Services.AddSingleton<SolveService>();
builder.Services.AddSingleton<UserAccessService>();

var app = builder.Build();
// made now so runs left unfinished by the last stop are cleared before anyone asks
app.Services.GetRequiredService<SolveService>();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Lets the integration tests start the API
public partial class Program { }
