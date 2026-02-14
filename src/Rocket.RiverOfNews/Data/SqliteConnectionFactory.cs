using Microsoft.Data.Sqlite;

namespace Rocket.RiverOfNews.Data;

public sealed class SqliteConnectionFactory
{
	private readonly string DatabasePath;

	public SqliteConnectionFactory(string DatabasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
		this.DatabasePath = DatabasePath;
	}

	public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken CancellationToken)
	{
		SqliteConnectionStringBuilder ConnectionStringBuilder = new()
		{
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared
		};

		SqliteConnection Connection = new(ConnectionStringBuilder.ConnectionString);
		await Connection.OpenAsync(CancellationToken);

		await using SqliteCommand ForeignKeysCommand = Connection.CreateCommand();
		ForeignKeysCommand.CommandText = "PRAGMA foreign_keys=ON;";
		await ForeignKeysCommand.ExecuteNonQueryAsync(CancellationToken);

		await using SqliteCommand WalCommand = Connection.CreateCommand();
		WalCommand.CommandText = "PRAGMA journal_mode=WAL;";
		await WalCommand.ExecuteNonQueryAsync(CancellationToken);

		await using SqliteCommand SyncCommand = Connection.CreateCommand();
		SyncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
		await SyncCommand.ExecuteNonQueryAsync(CancellationToken);

		return Connection;
	}
}
