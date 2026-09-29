using Npgsql;

namespace RfidManagementSystem.Services;

public class RfidViolationService
{
    private readonly ConfigurationService _configurationService;
    private readonly SystemLogService _systemLogService;

    public RfidViolationService(
        ConfigurationService configurationService,
        SystemLogService systemLogService)
    {
        _configurationService = configurationService;
        _systemLogService = systemLogService;
    }

    public async Task CheckAndMarkTimeViolationsAsync()
    {
        try
        {
            string connectionString =
                _configurationService.GetConnectionString();

            await using var connection =
                new NpgsqlConnection(connectionString);

            await connection.OpenAsync();

            // ==========================================
            // FIND OPEN TRANSACTIONS WHICH EXCEEDED
            // THEIR CHAMBER TIME THRESHOLD
            // ==========================================

            const string sql = @"
                SELECT
                    rt.id,
                    rt.employee_id,
                    rt.employee_name,
                    rt.chamber_id,
                    rt.entry_time,
                    mc.time_threshold
                FROM public.rfid_transactions rt
                INNER JOIN public.master_chambers mc
                    ON mc.chamber_id = rt.chamber_id
                WHERE rt.status = 'OPEN'
                  AND rt.exit_time IS NULL
                  AND rt.entry_time IS NOT NULL
                  AND rt.violation = FALSE
                  AND mc.is_active = TRUE
                  AND mc.time_threshold IS NOT NULL
                  AND mc.time_threshold > 0
                  AND rt.entry_time
                      + (mc.time_threshold * INTERVAL '1 minute')
                      <= NOW();
            ";

            await using var command =
                new NpgsqlCommand(sql, connection);

            await using var reader =
                await command.ExecuteReaderAsync();

            var violations = new List<ViolationCandidate>();

            while (await reader.ReadAsync())
            {
                violations.Add(new ViolationCandidate
                {
                    TransactionId = reader.GetInt64(
                        reader.GetOrdinal("id")
                    ),

                    EmployeeId = reader.GetInt64(
                        reader.GetOrdinal("employee_id")
                    ),

                    EmployeeName = reader.GetString(
                        reader.GetOrdinal("employee_name")
                    ),

                    ChamberId = reader.GetInt64(
                        reader.GetOrdinal("chamber_id")
                    ),

                    EntryTime = reader.GetDateTime(
                        reader.GetOrdinal("entry_time")
                    ),

                    TimeThreshold = reader.GetInt32(
                        reader.GetOrdinal("time_threshold")
                    )
                });
            }

            await reader.CloseAsync();

            // ==========================================
            // MARK EACH VIOLATION
            // ==========================================

            foreach (var violation in violations)
            {
                await MarkViolationAsync(
                    connection,
                    violation
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("RFID VIOLATION CHECK ERROR");
            Console.WriteLine($"Type: {ex.GetType().FullName}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine($"Inner Exception: {ex.InnerException?.Message}");
            Console.WriteLine($"Full Exception: {ex}");
            Console.WriteLine("========================================");

            try
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "ERROR",
                    "RFID_VIOLATION_CHECK_ERROR",
                    $"Error checking RFID time violations: {ex}",
                    null,
                    null
                );
            }
            catch (Exception logEx)
            {
                Console.WriteLine("SYSTEM LOG ALSO FAILED:");
                Console.WriteLine(logEx);
            }
        }
    }

    private async Task MarkViolationAsync(
        NpgsqlConnection connection,
        ViolationCandidate violation)
    {
        const string updateSql = @"
            UPDATE public.rfid_transactions
            SET
                violation = TRUE,
                violation_time_threshold = @timeThreshold,
                violation_marked_at = NOW(),
                updated_at = NOW()
            WHERE id = @transactionId
              AND status = 'OPEN'
              AND violation = FALSE;
        ";

        await using var updateCommand =
            new NpgsqlCommand(updateSql, connection);

        updateCommand.Parameters.AddWithValue(
            "transactionId",
            violation.TransactionId
        );

        updateCommand.Parameters.AddWithValue(
            "timeThreshold",
            violation.TimeThreshold
        );

        int rowsAffected =
            await updateCommand.ExecuteNonQueryAsync();

        // ==========================================
        // ONLY LOG IF THIS TRANSACTION WAS
        // ACTUALLY MARKED AS A VIOLATION
        // ==========================================

        if (rowsAffected == 0)
        {
            return;
        }

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "WARNING",
            "RFID_TIME_THRESHOLD_VIOLATION",
            $"Time threshold violation detected for " +
            $"{violation.EmployeeName}. " +
            $"Transaction ID: {violation.TransactionId}. " +
            $"Chamber ID: {violation.ChamberId}. " +
            $"Allowed time: {violation.TimeThreshold} minutes. " +
            $"Entry time: {violation.EntryTime:yyyy-MM-dd HH:mm:ss}.",
            null,
            null
        );
    }

    // ==========================================
    // INTERNAL MODEL
    // ==========================================

    private sealed class ViolationCandidate
    {
        public long TransactionId { get; set; }

        public long EmployeeId { get; set; }

        public string EmployeeName { get; set; } = string.Empty;

        public long ChamberId { get; set; }

        public DateTime EntryTime { get; set; }

        public int TimeThreshold { get; set; }
    }
}