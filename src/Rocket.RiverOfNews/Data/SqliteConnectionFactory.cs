using Microsoft.Data.Sqlite;

namespace Rocket.RiverOfNews.Data;

public sealed class SqliteConnectionFactory
{
	private readonly string DatabasePath;

	public SqliteConnectionFactory(string databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		DatabasePath = databasePath;
	}

	public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
	{
		SqliteConnectionStringBuilder connectionStringBuilder = new()
		{
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared
		};

		SqliteConnection connection = new(connectionStringBuilder.ConnectionString);
		await connection.OpenAsync(cancellationToken);

		await using SqliteCommand foreignKeysCommand = connection.CreateCommand();
		foreignKeysCommand.CommandText = "PRAGMA foreign_keys=ON;";
		await foreignKeysCommand.ExecuteNonQueryAsync(cancellationToken);

		await using SqliteCommand walCommand = connection.CreateCommand();
		walCommand.CommandText = "PRAGMA journal_mode=WAL;";
		await walCommand.ExecuteNonQueryAsync(cancellationToken);

		await using SqliteCommand syncCommand = connection.CreateCommand();
		syncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
		await syncCommand.ExecuteNonQueryAsync(cancellationToken);

		return connection;
	}
}
