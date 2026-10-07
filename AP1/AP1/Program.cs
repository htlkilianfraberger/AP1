using AP1.Components;
using AP1.Services;

var builder = WebApplication.CreateBuilder(args);

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
    app.UseHttpsRedirection();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
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
