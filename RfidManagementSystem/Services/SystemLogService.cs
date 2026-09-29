using Npgsql;
namespace RfidManagementSystem.Services;

public class SystemLogService
{
    private readonly ConfigurationService _configurationService;

    public SystemLogService(
        ConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public async Task LogAsync(
        string serviceName,
        string logLevel,
        string eventType,
        string message,
        string? sourceIp = null,
        int? sourcePort = null)
    {
        try
        {
            string connectionString =
                _configurationService.GetConnectionString();

            await using var connection =
                new NpgsqlConnection(connectionString);

            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO public.system_logs
                (
                    service_name,
                    log_level,
                    event_type,
                    message,
                    source_ip,
                    source_port
                )
                VALUES
                (
                    @serviceName,
                    @logLevel,
                    @eventType,
                    @message,
                    @sourceIp,
                    @sourcePort
                );
            ";

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "serviceName",
                serviceName
            );

            command.Parameters.AddWithValue(
                "logLevel",
                logLevel
            );

            command.Parameters.AddWithValue(
                "eventType",
                eventType
            );

            command.Parameters.AddWithValue(
                "message",
                message
            );

            command.Parameters.AddWithValue(
                "sourceIp",
                (object?)sourceIp ?? DBNull.Value
            );

            command.Parameters.AddWithValue(
                "sourcePort",
                (object?)sourcePort ?? DBNull.Value
            );

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            // Don't crash the RFID service if logging fails
            Console.WriteLine(
                $"System log error: {ex.Message}"
            );
        }
    }
}