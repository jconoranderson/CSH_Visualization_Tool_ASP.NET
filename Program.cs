var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<SleepVisualizationTool.Services.SleepChartRenderer>();
builder.Services.AddSingleton<SleepVisualizationTool.Services.SleepCsvLoader>();
builder.Services.AddSingleton<SleepVisualizationTool.Services.SleepPdfGenerator>();

var app = builder.Build();

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
