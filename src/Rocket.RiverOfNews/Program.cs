using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Configuration;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;
using Rocket.Syndication.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

RiverOfNewsSettings settings = new();
builder.Configuration.GetSection("RiverOfNews").Bind(settings);
builder.Services.AddSingleton(settings);

string repositoryRootPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
string databasePath = Path.Combine(repositoryRootPath, "db", "river.db");
string migrationsPath = Path.Combine(repositoryRootPath, "db", "migrations");

await SqliteDatabaseBootstrapper.InitializeAsync(databasePath, migrationsPath, CancellationToken.None);

builder.Services.AddSingleton(new SqliteConnectionFactory(databasePath));
builder.Services.AddSyndicationClient(options =>
{
	options.Timeout = TimeSpan.FromSeconds(settings.Feed.RequestTimeoutSeconds);
	options.EnableCaching = false;
});
builder.Services.AddScoped<FeedIngestionService>();
builder.Services.AddHostedService<FeedPollingBackgroundService>();
builder.Services.AddHostedService<RetentionCleanupBackgroundService>();

WebApplication app = builder.Build();

app.UseHttpsRedirection();

app.MapGet("/", () => Results.Redirect("/river"));
app.MapGet("/river", DatastarApi.GetRiverPage);
app.MapGet("/river/items/{itemId}", DatastarApi.GetRiverItemPage);
app.MapGet("/river/feeds", DatastarApi.GetFeedsAsync);
app.MapGet("/river/toggle-feed/{feedId}", DatastarApi.ToggleFeedAsync);
app.MapGet("/river/items", DatastarApi.GetItemsAsync);
app.MapGet("/river/clear-filters", DatastarApi.ClearFiltersAsync);
app.MapPost("/river/feeds", DatastarApi.AddFeedAsync);
app.MapDelete("/river/feeds/{feedId}", DatastarApi.DeleteFeedAsync);
app.MapPost("/river/refresh", DatastarApi.RefreshAsync);
app.MapDelete("/river/items", DatastarApi.ClearItemsAsync);
app.MapGet("/river/items/{itemId}/detail", DatastarApi.GetItemDetailAsync);
app.MapGet("/health", () => Results.Ok(new
{
	Status = "ok"
}));
app.MapGet("/api/feeds", MvpApi.GetFeedsAsync);
app.MapPost("/api/feeds", MvpApi.AddFeedAsync);
app.MapDelete("/api/feeds/{feedId}", MvpApi.DeleteFeedAsync);
app.MapPost("/api/refresh", MvpApi.RefreshAsync);
app.MapGet("/api/items", MvpApi.GetItemsAsync);
app.MapGet("/api/items/{itemId}", MvpApi.GetItemByIdAsync);
app.MapDelete("/api/items", MvpApi.ClearItemsAsync);
app.MapGet("/api/perf/latest-200", MvpApi.GetLatest200PerformanceAsync);

app.Run();
