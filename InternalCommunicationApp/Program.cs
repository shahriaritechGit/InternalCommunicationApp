using InternalCommunicationApp.Data;
var builder = WebApplication.CreateBuilder(args);

// Razor Pages
builder.Services.AddRazorPages();

builder.Services.AddSingleton<RedisConnection>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
