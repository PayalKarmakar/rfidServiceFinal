using Npgsql;
using RfidManagementSystem.Models;

namespace RfidManagementSystem.Services;
//This service will load all active readers from PostgreSQL.
public class RfidReaderConfigurationService
{
    private readonly ConfigurationService _configurationService;

    public RfidReaderConfigurationService(
        ConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public async Task<List<MasterRfidReader>>
        GetActiveReadersAsync()
    {
        var readers = new List<MasterRfidReader>();

        string connectionString =
            _configurationService.GetConnectionString();

        await using var connection =
            new NpgsqlConnection(connectionString);

        await connection.OpenAsync();

        const string sql = @"
            SELECT
                reader_id,
                reader_name,
                reader_serialno,
                ip_address,
                port,
                reader_purpose,
                is_active,
                created_at,
                updated_at,
                last_updated_by
            FROM public.master_rfid_readers
            WHERE is_active = TRUE
            ORDER BY reader_id;
        ";

        await using var command =
            new NpgsqlCommand(sql, connection);

        await using var dbReader =
            await command.ExecuteReaderAsync();

        while (await dbReader.ReadAsync())
        {
            readers.Add(new MasterRfidReader
            {
                ReaderId = dbReader.GetInt64(
                    dbReader.GetOrdinal("reader_id")
                ),

                ReaderName = dbReader.GetString(
                    dbReader.GetOrdinal("reader_name")
                ),

                ReaderSerialno = dbReader.GetString(
                    dbReader.GetOrdinal("reader_serialno")
                ),

                IpAddress = dbReader.GetString(
                    dbReader.GetOrdinal("ip_address")
                ),

                Port = dbReader.GetInt32(
                    dbReader.GetOrdinal("port")
                ),

                ReaderPurpose = dbReader.GetString(
                    dbReader.GetOrdinal("reader_purpose")
                ),

                IsActive = dbReader.GetBoolean(
                    dbReader.GetOrdinal("is_active")
                ),

                CreatedAt = dbReader.GetDateTime(
                    dbReader.GetOrdinal("created_at")
                ),

                UpdatedAt =
                    dbReader.IsDBNull(
                        dbReader.GetOrdinal("updated_at")
                    )
                    ? null
                    : dbReader.GetDateTime(
                        dbReader.GetOrdinal("updated_at")
                    ),

                LastUpdatedBy =
                    dbReader.IsDBNull(
                        dbReader.GetOrdinal("last_updated_by")
                    )
                    ? null
                    : dbReader.GetInt64(
                        dbReader.GetOrdinal("last_updated_by")
                    )
            });
        }

        return readers;
    }
}