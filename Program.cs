var builder = WebApplication.CreateBuilder(args);

// 1. This tells the app to enable the Controller feature
builder.Services.AddControllers(); 

var app = builder.Build();

// 2. This tells the app to automatically map URLs to the controllers it found
app.MapControllers(); 

app.Run();