using obsbot_control.Components;
using obsbot_control.Services;
using Microsoft.AspNetCore.DataProtection;
using Obsbot.Control;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.Configure<FfmpegCameraStreamOptions>(builder.Configuration.GetSection("Obsbot:Stream"));
builder.Services.AddSingleton<IObsbotCameraDriver, DirectShowObsbotCameraDriver>();
builder.Services.AddSingleton<FfmpegCameraStreamService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapGet("/obsbot/video.mjpeg", async (HttpContext context, FfmpegCameraStreamService stream, CancellationToken cancellationToken) =>
{
    await stream.StreamMjpegAsync(
        context.Response,
        context.Request.Query["backend"],
        context.Request.Query["orientation"],
        cancellationToken);
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
