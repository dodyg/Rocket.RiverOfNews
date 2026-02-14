using Microsoft.Data.Sqlite;

namespace Rocket.RiverOfNews.Data;

public static class SqliteDatabaseBootstrapper
{
	public static async Task InitializeAsync(
		string DatabasePath,
		string MigrationsPath,
		CancellationToken CancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(MigrationsPath);

		string? DatabaseDirectoryPath = Path.GetDirectoryName(DatabasePath);
		if (string.IsNullOrWhiteSpace(DatabaseDirectoryPath))
		{
			throw new InvalidOperationException("Database path must include a directory.");
		}

		Directory.CreateDirectory(DatabaseDirectoryPath);
		if (!Directory.Exists(MigrationsPath))
		{
			throw new DirectoryNotFoundException($"Migrations path not found: {MigrationsPath}");
		}

		SqliteConnectionStringBuilder ConnectionStringBuilder = new()
		{
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared
		};

		await using SqliteConnection Connection = new(ConnectionStringBuilder.ConnectionString);
		await Connection.OpenAsync(CancellationToken);

		await ExecuteNonQueryAsync(Connection, "PRAGMA foreign_keys=ON;", CancellationToken);
		await ExecuteNonQueryAsync(Connection, "PRAGMA journal_mode=WAL;", CancellationToken);
		await ExecuteNonQueryAsync(Connection, "PRAGMA synchronous=NORMAL;", CancellationToken);

		string[] MigrationFilePaths = Directory.GetFiles(MigrationsPath, "*.sql", SearchOption.TopDirectoryOnly);
		Array.Sort(MigrationFilePaths, StringComparer.Ordinal);

		foreach (string MigrationFilePath in MigrationFilePaths)
		{
			string MigrationSql = await File.ReadAllTextAsync(MigrationFilePath, CancellationToken);
			await ExecuteNonQueryAsync(Connection, MigrationSql, CancellationToken);
		}
	}

	private static async Task ExecuteNonQueryAsync(
		SqliteConnection Connection,
		string Sql,
		CancellationToken CancellationToken)
	{
		await using SqliteCommand Command = Connection.CreateCommand();
		Command.CommandText = Sql;
		await Command.ExecuteNonQueryAsync(CancellationToken);
	}
}
