using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;
using Rocket.Syndication.DependencyInjection;

WebApplicationBuilder Builder = WebApplication.CreateBuilder(args);

string RepositoryRootPath = Path.GetFullPath(Path.Combine(Builder.Environment.ContentRootPath, "..", ".."));
string DatabasePath = Path.Combine(RepositoryRootPath, "db", "river.db");
string MigrationsPath = Path.Combine(RepositoryRootPath, "db", "migrations");

await SqliteDatabaseBootstrapper.InitializeAsync(DatabasePath, MigrationsPath, CancellationToken.None);

Builder.Services.AddSingleton(new SqliteConnectionFactory(DatabasePath));
Builder.Services.AddSyndicationClient(Options =>
{
	Options.Timeout = TimeSpan.FromSeconds(10);
	Options.EnableCaching = false;
});
Builder.Services.AddScoped<FeedIngestionService>();
Builder.Services.AddHostedService<FeedPollingBackgroundService>();
Builder.Services.AddHostedService<RetentionCleanupBackgroundService>();

WebApplication App = Builder.Build();

App.MapGet("/", () => Results.Redirect("/river"));
App.MapGet("/river", MvpApi.GetRiverPage);
App.MapGet("/health", () => Results.Ok(new
{
	Status = "ok"
}));
App.MapGet("/api/feeds", MvpApi.GetFeedsAsync);
App.MapPost("/api/feeds", MvpApi.AddFeedAsync);
App.MapDelete("/api/feeds/{FeedId}", MvpApi.DeleteFeedAsync);
App.MapPost("/api/refresh", MvpApi.RefreshAsync);
App.MapGet("/api/items", MvpApi.GetItemsAsync);
App.MapGet("/api/perf/latest-200", MvpApi.GetLatest200PerformanceAsync);

App.Run();
