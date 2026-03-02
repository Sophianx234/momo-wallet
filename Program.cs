using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using momo_wallet.Data;

var builder = WebApplication.CreateBuilder(args);

// 1. This tells the app to enable the Controller feature
builder.Services.AddControllers(); 
builder.Services.AddDbContext<AppDbContext>(option=>option.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// 2. This tells the app to automatically map URLs to the controllers it found
app.MapControllers(); 

app.Run();