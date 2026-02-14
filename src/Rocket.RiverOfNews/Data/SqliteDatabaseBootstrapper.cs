using Microsoft.Data.Sqlite;

namespace Rocket.RiverOfNews.Data;

public static class SqliteDatabaseBootstrapper
{
	public static async Task InitializeAsync(
		string databasePath,
		string migrationsPath,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(migrationsPath);

		string? databaseDirectoryPath = Path.GetDirectoryName(databasePath);
		if (string.IsNullOrWhiteSpace(databaseDirectoryPath))
		{
			throw new InvalidOperationException("Database path must include a directory.");
		}

		Directory.CreateDirectory(databaseDirectoryPath);
		if (!Directory.Exists(migrationsPath))
		{
			throw new DirectoryNotFoundException($"Migrations path not found: {migrationsPath}");
		}

		SqliteConnectionStringBuilder connectionStringBuilder = new()
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared
		};

		await using SqliteConnection connection = new(connectionStringBuilder.ConnectionString);
		await connection.OpenAsync(cancellationToken);

		await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
		await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
		await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);
		await ExecuteNonQueryAsync(
			connection,
			"""
			CREATE TABLE IF NOT EXISTS __migrations
			(
				file_name TEXT PRIMARY KEY,
				applied_at TEXT NOT NULL
			);
			""",
			cancellationToken);

		string[] migrationFilePaths = Directory.GetFiles(migrationsPath, "*.sql", SearchOption.TopDirectoryOnly);
		Array.Sort(migrationFilePaths, StringComparer.Ordinal);

		foreach (string migrationFilePath in migrationFilePaths)
		{
			string migrationFileName = Path.GetFileName(migrationFilePath);
			if (await IsMigrationAppliedAsync(connection, migrationFileName, cancellationToken))
			{
				continue;
			}

			await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
			string migrationSql = await File.ReadAllTextAsync(migrationFilePath, cancellationToken);
			await ExecuteNonQueryAsync(connection, migrationSql, cancellationToken, transaction);
			await RecordMigrationAsync(connection, migrationFileName, cancellationToken, transaction);
			await transaction.CommitAsync(cancellationToken);
		}
	}

	private static async Task<bool> IsMigrationAppliedAsync(
		SqliteConnection connection,
		string migrationFileName,
		CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT EXISTS(SELECT 1 FROM __migrations WHERE file_name = $fileName);";
		command.Parameters.AddWithValue("$fileName", migrationFileName);
		object? scalar = await command.ExecuteScalarAsync(cancellationToken);
		return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture) == 1;
	}

	private static async Task RecordMigrationAsync(
		SqliteConnection connection,
		string migrationFileName,
		CancellationToken cancellationToken,
		System.Data.Common.DbTransaction transaction)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = (SqliteTransaction?)transaction;
		command.CommandText = """
			INSERT INTO __migrations (file_name, applied_at)
			VALUES ($fileName, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
			""";
		command.Parameters.AddWithValue("$fileName", migrationFileName);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task ExecuteNonQueryAsync(
		SqliteConnection connection,
		string sql,
		CancellationToken cancellationToken,
		System.Data.Common.DbTransaction? transaction = null)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = (SqliteTransaction?)transaction;
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync(cancellationToken);
	}
}
