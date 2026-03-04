using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks; // Add this at the very top of your file!
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using momo_wallet.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(); 
builder.Services.AddDbContext<AppDbContext>(option=>option.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHealthChecks()
                .AddDbContextCheck<AppDbContext>();

// ---> 1. ADD SWAGGER SERVICES <---
// This tells ASP.NET to inspect your controllers and figure out what routes exist
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("strict", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5; 
        limiterOptions.Window = TimeSpan.FromMinutes(1); 
        limiterOptions.QueueLimit = 0; 
    });
});

var app = builder.Build();

// ---> 2. ENABLE SWAGGER UI <---
// We wrap this in an 'if' statement so the dashboard is only available on your computer, 
// not when you deploy it live to the internet!
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter(); 


// ... (rest of your code) ...

// ---> UPGRADED HEALTH CHECK <---
app.MapHealthChecks("/api/health", new HealthCheckOptions
{
    // This custom writer intercepts the "Healthy/Unhealthy" text and formats a beautiful JSON response instead
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        
        var response = new
        {
            systemStatus = report.Status.ToString(),
            timeTaken = report.TotalDuration.TotalMilliseconds + " ms",
            components = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                error = e.Value.Exception?.Message // This will show the exact error if it crashes!
            })
        };
        
        await context.Response.WriteAsJsonAsync(response);
    }
});
app.MapControllers().RequireRateLimiting("strict"); 

app.Run();