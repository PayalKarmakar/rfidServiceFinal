using Npgsql;

namespace RfidManagementSystem.Helpers;

public static class DatabaseTimeHelper
{
    public static async Task<DateTime> GetDatabaseServerTimeAsync(
        string connectionString)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);

        await connection.OpenAsync();

        const string sql = "SELECT NOW();";

        await using var command =
            new NpgsqlCommand(sql, connection);

        object? result =
            await command.ExecuteScalarAsync();

        return (DateTime)result!;
    }
}