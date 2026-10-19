using AP1.Components;
using AP1.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ApiResultCache>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient<RaceCalendarService>();

builder.Services.AddHttpClient<OpenF1Service>(client =>
{
    client.BaseAddress = new Uri("https://api.openf1.org/v1/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Blazor Server uses SignalR for its interactive circuit. Explicitly enable
// WebSockets so the circuit can upgrade when the app runs behind Docker port
// publishing or another HTTP proxy; without a live circuit, @onchange handlers
// render as static HTML and the selected season never updates.
app.UseWebSockets();

app.UseStatusCodePagesWithReExecute(
    "/not-found",
    createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/rennkalender/export", async (RaceCalendarService raceCalendarService, CancellationToken cancellationToken) =>
{
    var calendar = await raceCalendarService.CreateCurrentSeasonCalendarAsync(cancellationToken);
    var fileName = $"f1-rennkalender-{DateTime.UtcNow.Year}.ics";

    return Results.File(calendar, "text/calendar; charset=utf-8", fileName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
