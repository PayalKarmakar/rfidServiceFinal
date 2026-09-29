using Npgsql;
using RfidManagementSystem.Models;
namespace RfidManagementSystem.Services;

public class RfidTransactionService
{
    private readonly ConfigurationService _configurationService;
    private readonly SystemLogService _systemLogService;

    public RfidTransactionService(ConfigurationService configurationService,SystemLogService systemLogService)
    {
        _configurationService = configurationService;
        _systemLogService = systemLogService;
    }

    public async Task<RfidScanResult> ProcessCardAsync(MasterRfidReader reader,string cardUid, string readerIp,int readerPort,string rawHexData)
    {
        try
        {
            string connectionString = _configurationService.GetConnectionString();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // ==========================================
            // EMPLOYEE REGISTRATION
            // No employee lookup required
            // ==========================================

            //if (reader.ReaderPurpose == RfidReaderPurpose.EMPLOYEE_REGISTRATION)
            //{
            //    return await ProcessEmployeeRegistrationAsync(connection,reader,cardUid, readerIp, readerPort,rawHexData);
            //}

            // ==========================================
            // FIND EMPLOYEE USING CARD UID
            // ==========================================

            const string employeeSql = @"
                            SELECT emp_id,employee_name,employee_code,chamber_id,is_lost
                            FROM public.master_employees
                            WHERE card_uid = @cardUid
                            AND is_active = TRUE
                            LIMIT 1;
                        ";

            await using var command = new NpgsqlCommand(employeeSql,connection);
            command.Parameters.AddWithValue("cardUid",cardUid);
            await using var dbReader =await command.ExecuteReaderAsync();

            // ==========================================
            // CARD NOT FOUND
            // ==========================================

            if (!await dbReader.ReadAsync())
            {
                await dbReader.CloseAsync();

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "UNAUTHORIZED",
                    $"Unauthorized RFID card detected. Card UID: {cardUid}.",
                    readerIp,
                    readerPort
                );

                return new RfidScanResult
                {
                    ResultType = RfidScanResultType.UNAUTHORIZED,
                    Message = "Unauthorized RFID card.",
                    CardUid = cardUid,
                    ShouldAlertDashboard = true,
                    ShouldAnnounce = true
                };
            }

            // ==========================================
            // GET EMPLOYEE DETAILS
            // ==========================================

            long employeeId =dbReader.GetInt64(0);
            string employeeName = dbReader.GetString(1);
            string employeeCode = dbReader.GetString(2);
            int? chamberId =dbReader.IsDBNull(3) ? null: dbReader.GetInt32(3);
            bool isLost = dbReader.GetBoolean(4);

            await dbReader.CloseAsync();

            if (isLost)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "UNAUTHORIZED_LOST_CARD",
                    $"Unauthorized RFID card detected. " +
                    $"Card UID: {cardUid}. " +
                    $"Card belongs to employee {employeeName} " +
                    $"({employeeCode}) but is marked as LOST.",
                    readerIp,
                    readerPort
                );

                return new RfidScanResult
                {
                    ResultType = RfidScanResultType.UNAUTHORIZED,
                    Message = "Unauthorized RFID card. Card is marked as lost.",
                    CardUid = cardUid,
                    EmployeeId = employeeId,
                    EmployeeName = employeeName,
                    EmployeeCode = employeeCode,
                    ShouldAlertDashboard = true,
                    ShouldAnnounce = true
                };
            }

            // ==========================================
            // PROCESS ACCORDING TO READER PURPOSE
            // ==========================================

            switch (reader.ReaderPurpose)
            {
                case RfidReaderPurpose.ENTRY:
                    return await ProcessEntryAsync(connection, reader, employeeId, chamberId, employeeName, employeeCode, cardUid, readerIp, readerPort, rawHexData);

                case RfidReaderPurpose.EXIT:
                    return await ProcessExitAsync(connection, reader, employeeId, employeeName, employeeCode, cardUid, readerIp, readerPort, rawHexData);

                case RfidReaderPurpose.ENTRY_EXIT:
                    return await ProcessEntryExitAsync(connection, reader, employeeId, chamberId, employeeName, employeeCode, cardUid, readerIp, readerPort, rawHexData);

                //case RfidReaderPurpose.EMPLOYEE_REGISTRATION:
                //    return await ProcessEmployeeRegistrationAsync(connection, reader, cardUid, readerIp, readerPort, rawHexData);

                default:
                    return new RfidScanResult
                    {
                        ResultType = RfidScanResultType.ERROR,
                        Message = $"Unsupported reader purpose: {reader.ReaderPurpose}",
                        CardUid = cardUid,
                        ShouldAlertDashboard = true,
                        ShouldAnnounce = false
                    };
            }

        }
        catch (Exception ex)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_TRANSACTION_ERROR",
                $"Error processing card {cardUid}: {ex.Message}",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType = RfidScanResultType.ERROR,
                Message = $"Error processing card: {ex.Message}",
                CardUid = cardUid,
                ShouldAlertDashboard = true,
                ShouldAnnounce = false
            };
        }


        
    }

    private async Task<RfidScanResult> ProcessEntryAsync(NpgsqlConnection connection,MasterRfidReader reader,long employeeId,
        int? chamberId,string employeeName,string employeeCode, string cardUid,string readerIp,int readerPort,string rawHexData)
    {
        // ==========================================
        // CHAMBER VALIDATION FOR ENTRY
        // ==========================================

        if (!chamberId.HasValue)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "WARNING",
                "CHAMBER_NOT_ASSIGNED",
                $"Entry denied. Employee {employeeName} " +
                $"({employeeCode}) does not have a chamber assigned.",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType = RfidScanResultType.ACCESS_DENIED,
                Message = $"Entry denied. {employeeName} does not have a chamber assigned.",
                CardUid = cardUid,
                EmployeeId = employeeId,
                EmployeeName = employeeName,
                EmployeeCode = employeeCode,
                ShouldAlertDashboard = true,
                ShouldAnnounce = true
            };
        }

        // ==========================================
        // CHECK OPEN TRANSACTION
        // ==========================================

        const string checkOpenTransactionSql = @"
                                        SELECT id
                                        FROM public.rfid_transactions
                                        WHERE employee_id = @employeeId
                                        AND exit_time IS NULL
                                        AND status <> 'INCOMPLETE'
                                        ORDER BY entry_time DESC
                                        LIMIT 1;
                                    ";

        await using var checkCommand = new NpgsqlCommand(checkOpenTransactionSql,connection);

        checkCommand.Parameters.AddWithValue("employeeId",employeeId);

        object? openTransaction = await checkCommand.ExecuteScalarAsync();

        // ==========================================
        // EMPLOYEE ALREADY INSIDE
        // ==========================================

        if (openTransaction != null)
        {
            await _systemLogService.LogAsync("RFID_SERVICE","WARNING", "ALREADY_INSIDE", $"ENTRY denied. Employee {employeeName} already has an open transaction.",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType = RfidScanResultType.ACCESS_DENIED,
                Message = $"Entry denied. {employeeName} is already inside.",
                CardUid = cardUid,
                EmployeeId = employeeId,
                EmployeeName = employeeName,
                EmployeeCode = employeeCode,
                ShouldAlertDashboard = true,
                ShouldAnnounce = true
            };
        }

        // ==========================================
        // CREATE ENTRY TRANSACTION
        // ==========================================

        const string insertSql = @"
                        INSERT INTO public.rfid_transactions
                        (
                            employee_id,
                            chamber_id,
                            employee_name,
                            card_uid,
                            entry_time,
                            entry_reader_id,
                            entry_reader_ip,
                            entry_reader_port,
                            entry_raw_hex_data,
                            status
                        )
                        VALUES
                        (
                            @employeeId,
                            @chamberId,
                            @employeeName,
                            @cardUid,
                            NOW(),
                            @readerId,
                            @readerIp,
                            @readerPort,
                            @rawHexData,
                            'OPEN'
                        );
                    ";

        await using var insertCommand = new NpgsqlCommand(insertSql,connection);

        insertCommand.Parameters.AddWithValue("employeeId", employeeId);
        insertCommand.Parameters.AddWithValue("employeeName", employeeName);
        insertCommand.Parameters.AddWithValue("chamberId",chamberId.Value);
        insertCommand.Parameters.AddWithValue( "cardUid",cardUid);
        insertCommand.Parameters.AddWithValue("readerId",reader.ReaderId);
        insertCommand.Parameters.AddWithValue( "readerIp", readerIp);
        insertCommand.Parameters.AddWithValue("readerPort",readerPort);
        insertCommand.Parameters.AddWithValue("rawHexData",rawHexData);
        await insertCommand.ExecuteNonQueryAsync();

        // ==========================================
        // SYSTEM LOG
        // ==========================================

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "INFO",
            "ENTRY_RECORDED",
            $"Entry recorded for {employeeName} ({employeeCode}) " +
            $"using RFID Reader '{reader.ReaderName}'.",
            readerIp,
            readerPort
        );

        // ==========================================
        // RETURN RESULT
        // ==========================================

        return new RfidScanResult
        {
            ResultType = RfidScanResultType.ENTRY_RECORDED,
            Message = $"Entry recorded successfully for {employeeName}.",
            CardUid = cardUid,
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            EmployeeCode = employeeCode,
            ShouldAlertDashboard = false,
            ShouldAnnounce = false
        };
    }

    private async Task<RfidScanResult> ProcessExitAsync(NpgsqlConnection connection, MasterRfidReader reader, long employeeId,
        string employeeName, string employeeCode, string cardUid, string readerIp, int readerPort, string rawHexData)
    
    {
        const string updateSql = @"
            UPDATE public.rfid_transactions
            SET
                exit_time = NOW(),
                duration = NOW() - entry_time,
                exit_reader_id = @readerId,
                exit_reader_ip = @readerIp,
                exit_reader_port = @readerPort,
                exit_raw_hex_data = @rawHexData,
                status = 'COMPLETED',
                updated_at = NOW()
            WHERE id =
            (
                SELECT id
                FROM public.rfid_transactions
                WHERE employee_id = @employeeId
                AND status = 'OPEN'
                ORDER BY entry_time DESC
                LIMIT 1
            );
        ";

        await using var updateCommand =new NpgsqlCommand(updateSql,connection);

        updateCommand.Parameters.AddWithValue(
            "employeeId",
            employeeId
        );

        updateCommand.Parameters.AddWithValue(
            "readerId",
            reader.ReaderId
        );

        updateCommand.Parameters.AddWithValue(
            "readerIp",
            readerIp
        );

        updateCommand.Parameters.AddWithValue(
            "readerPort",
            readerPort
        );

        updateCommand.Parameters.AddWithValue(
            "rawHexData",
            rawHexData
        );

        int rowsAffected =
            await updateCommand.ExecuteNonQueryAsync();

        // ==========================================
        // NO OPEN ENTRY FOUND
        // ==========================================

        if (rowsAffected == 0)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "WARNING",
                "EXIT_WITHOUT_ENTRY",
                $"Exit denied. Employee {employeeName} " +
                $"({employeeCode}) does not have an open ENTRY transaction.",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType =
                    RfidScanResultType.ACCESS_DENIED,

                Message =
                    $"Exit denied. {employeeName} does not have an open entry.",

                CardUid = cardUid,

                EmployeeId = employeeId,

                EmployeeName = employeeName,

                EmployeeCode = employeeCode,

                ShouldAlertDashboard = true,

                ShouldAnnounce = true
            };
        }

        // ==========================================
        // EXIT SUCCESSFULLY RECORDED
        // ==========================================

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "INFO",
            "EXIT_RECORDED",
            $"Exit recorded for {employeeName} ({employeeCode}) " +
            $"using RFID Reader '{reader.ReaderName}'.",
            readerIp,
            readerPort
        );

        return new RfidScanResult
        {
            ResultType = RfidScanResultType.EXIT_RECORDED,

            Message = $"Exit recorded successfully for {employeeName}.",

            CardUid = cardUid,

            EmployeeId = employeeId,

            EmployeeName = employeeName,

            EmployeeCode = employeeCode,

            ShouldAlertDashboard = false,

            ShouldAnnounce = false
        };
    }

    private async Task<RfidScanResult> ProcessEntryExitAsync(NpgsqlConnection connection, MasterRfidReader reader, long employeeId,
        int? chamberId, string employeeName, string employeeCode, string cardUid, string readerIp, int readerPort, string rawHexData)
    
    {
        // ==========================================
        // GET LATEST OPEN TRANSACTION
        // PostgreSQL calculates elapsed time
        // using database server time
        // ==========================================

        const string openTransactionSql = @"
                    SELECT
                        id,
                        EXTRACT(
                            EPOCH FROM (NOW() - entry_time)
                        ) AS elapsed_seconds
                    FROM public.rfid_transactions
                    WHERE employee_id = @employeeId
                      AND status = 'OPEN'
                      AND exit_time IS NULL
                    ORDER BY entry_time DESC
                    LIMIT 1;
                ";

     

        await using var command =new NpgsqlCommand(openTransactionSql,connection);

        command.Parameters.AddWithValue("employeeId",employeeId);
        await using var dbReader = await command.ExecuteReaderAsync();

        // ==========================================
        // NO OPEN TRANSACTION  // EMPLOYEE IS OUTSIDE
        // → THIS IS AN ENTRY
        // ==========================================

        if (!await dbReader.ReadAsync())
        {
            await dbReader.CloseAsync();

            return await ProcessEntryAsync(connection,reader,employeeId,chamberId, employeeName,employeeCode,cardUid,readerIp, readerPort,rawHexData);
        }

        // ==========================================
        // OPEN TRANSACTION FOUND
        // GET DATABASE CALCULATED GAP
        // ==========================================

        long transactionId = dbReader.GetInt64(0);

        double elapsedSeconds = dbReader.GetDouble(1);
        await dbReader.CloseAsync();

        // ==========================================
        // GET CONFIGURED ENTRY   
        // ==========================================

        int entryExitGapSeconds = _configurationService.GetEntryExitGapSeconds();      

        // ==========================================
        // COOLDOWN SCAN
        // ==========================================

        if (elapsedSeconds < entryExitGapSeconds)
        {
            int remainingSeconds = Math.Max( 1,entryExitGapSeconds -(int)Math.Floor(elapsedSeconds));
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "WARNING",
                "SCAN_COOLDOWN",
                $"RFID scan ignored for {employeeName} " + $"({employeeCode}). Please wait {remainingSeconds} " + $"seconds before scanning again.",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType = RfidScanResultType.SCAN_COOLDOWN,
                Message =  $"Please wait {remainingSeconds} seconds " + $"before scanning again.",
                CardUid = cardUid,
                EmployeeId = employeeId,
                EmployeeName = employeeName,
                EmployeeCode = employeeCode,
                ShouldAlertDashboard = true,
                ShouldAnnounce = true
            };
        }

        // ==========================================
        // GAP COMPLETED
        // → THIS IS AN EXIT
        // ==========================================

        return await ProcessExitAsync(connection, reader,employeeId,employeeName,employeeCode,cardUid,readerIp, readerPort, rawHexData);
    }

    private async Task<RfidScanResult> ProcessEmployeeRegistrationAsync(NpgsqlConnection connection,MasterRfidReader reader, string cardUid,string readerIp,
        int readerPort,string rawHexData)
    {
        try
        {
            // Check whether this card is already assigned
            const string checkCardSql = @"
            SELECT
                emp_id,
                employee_name,
                employee_code,
                chamber_id
            FROM public.master_employees
            WHERE card_uid = @cardUid
            AND is_active = TRUE
            LIMIT 1;
        ";

            await using var command = new NpgsqlCommand(checkCardSql, connection);
            command.Parameters.AddWithValue("cardUid",cardUid);

            await using var dbReader = await command.ExecuteReaderAsync();

            // ==========================================
            // CARD IS ALREADY ASSIGNED
            // ==========================================

            if (await dbReader.ReadAsync())
            {
                long employeeId = dbReader.GetInt64(0);

                string employeeName = dbReader.GetString(1);

                string employeeCode = dbReader.GetString(2);

                await dbReader.CloseAsync();

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_CARD_ALREADY_ASSIGNED",
                    $"RFID card {cardUid} is already assigned to " +
                    $"{employeeName} ({employeeCode}).",
                    readerIp,
                    readerPort
                );

                return new RfidScanResult
                {
                    ResultType = RfidScanResultType.ACCESS_DENIED,

                    Message = $"RFID card is already assigned to " + $"{employeeName}.",
                    CardUid = cardUid,
                    EmployeeId = employeeId,
                    EmployeeName = employeeName,
                    EmployeeCode = employeeCode,
                    ShouldAlertDashboard = true,
                    ShouldAnnounce = true
                };
            }

            await dbReader.CloseAsync();

            // ==========================================
            // NEW CARD DETECTED FOR REGISTRATION
            // ==========================================

            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "INFO",
                "EMPLOYEE_REGISTRATION_CARD_DETECTED",
                $"New RFID card detected for employee registration. " +
                $"Card UID: {cardUid}. " +
                $"Reader: '{reader.ReaderName}'.",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType = RfidScanResultType.EMPLOYEE_REGISTRATION,

                Message ="RFID card detected successfully for employee registration.",

                CardUid = cardUid,

                ShouldAlertDashboard = true,

                ShouldAnnounce = false
            };
        }
        catch (Exception ex)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "EMPLOYEE_REGISTRATION_ERROR",
                $"Error processing registration card {cardUid}: " +
                $"{ex.Message}",
                readerIp,
                readerPort
            );

            return new RfidScanResult
            {
                ResultType =RfidScanResultType.ERROR,
                Message =$"Error processing RFID registration card: {ex.Message}",
                CardUid = cardUid,
                ShouldAlertDashboard = true,
                ShouldAnnounce = false
            };
        }
    }

    
}