using SmartHomeDashboard.Repositories;
using SmartHomeDashboard.Services;
using SmartHomeDashboard.Models.Options;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// Options (read-only for now, surfaced on Settings page)
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));

// Persistence + background services
builder.Services.AddSingleton<DeviceRepository>();
builder.Services.AddSingleton<LogsRepository>();
builder.Services.AddHostedService<DeviceMonitorService>();

var app = builder.Build();

// Ensure App_Data stores exist on startup
using (var scope = app.Services.CreateScope())
{
    var devices = scope.ServiceProvider.GetRequiredService<DeviceRepository>();
    _ = devices.GetAll(); // triggers devices.json creation/seed if needed

    var logs = scope.ServiceProvider.GetRequiredService<LogsRepository>();
    _ = logs.GetTail(1);  // triggers logs.json creation if needed
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
