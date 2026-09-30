using RfidManagementSystem.Hardware.Rfid;
using RfidManagementSystem.Models;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RfidManagementSystem.Services;

public class RfidService
{
    private readonly ConfigurationService _configurationService;
    private readonly RfidReaderConfigurationService _readerConfigurationService;
    private readonly SystemLogService _systemLogService;
    private readonly RfidTransactionService _rfidTransactionService;
    private readonly EmployeeRegistrationService _employeeRegistrationService;

    // ============================
    // DYNAMIC RFID READERS
    // ============================

    // Key = reader_id from master_rfid_readers
    // Value = TCP server for that reader
    private readonly Dictionary<long, RfidTcpClient> _rfidClients = new();
    private readonly Dictionary<long, CancellationTokenSource> _inventoryTokens = new();

    // Stores IDs of currently connected readers
    private readonly HashSet<long> _connectedReaderIds = new();

    // Stores IDs of currently disconnected connected readers
    private readonly HashSet<long> _disconnectedReaderIds = new();

    // Active readers loaded from database
    private List<MasterRfidReader> _activeReaders = new();

    // Prevents ALL_READERS_CONNECTED
    // log from being inserted multiple times
    private bool _allReadersStatusLogged = false;

   

    // ============================
    // EVENTS
    // ============================
    public event Action<MasterRfidReader, string>? ReaderConnected;
    //public event Action<MasterRfidReader, string>? ServerStarted;

    public event Action<MasterRfidReader, string,int,byte[]>? CardDataReceived;
    public event Action<MasterRfidReader, string, int, RfidScanResult>? ScanProcessed;

    public event Action? ReaderConnectionStatusChanged;

    // ==========================================
    // EMPLOYEE RFID REGISTRATION STATE
    // ==========================================

    private bool _employeeRegistrationActive = false;

    private RfidScanResult? _employeeRegistrationResult;

    private readonly object _employeeRegistrationLock = new();

    private bool _serviceStarted = false;
    private bool _serviceStopped = false;

    private readonly RfidViolationService _rfidViolationService;

    public RfidService(EmployeeRegistrationService employeeRegistrationService)
    {
        _configurationService = new ConfigurationService();

        _systemLogService = new SystemLogService(_configurationService);
     
        _rfidTransactionService = new RfidTransactionService(_configurationService,_systemLogService);

        _readerConfigurationService =new RfidReaderConfigurationService(_configurationService);

        _employeeRegistrationService = employeeRegistrationService;

        _rfidViolationService = new RfidViolationService(_configurationService,  _systemLogService);
    }

    public async Task StartAsync()
    {
        try
        {
            if (_serviceStarted)
            {
                return;
            }

            _serviceStarted = true;

            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "INFO",
                "RFID_SERVICE_STARTED",
                "RFID service started successfully.",
                null,
                null
            );

            // Load all active RFID readers dynamically from database
            _activeReaders = await _readerConfigurationService.GetActiveReadersAsync();

            if (_activeReaders.Count == 0)
            {
                await _systemLogService.LogAsync("RFID_SERVICE","WARNING","NO_ACTIVE_READERS", "No active RFID readers are configured.", null,null);
                return;
            }

            // Start TCP server for every active reader
            foreach (var reader in _activeReaders)
            {
                await StartReaderAsync(reader);
            }

            // ALL readers are connected first
            foreach (var reader in _activeReaders)
            {
                if (_connectedReaderIds.Contains(reader.ReaderId))
                {
                    StartInventoryLoop(reader);
                }
            }
            // Monitor physical RFID reader connections
            // and retry/check based on appsettings configuration
            _ = MonitorReaderConnectionsAsync();

            // Keep reader purpose/IP/active flags in sync with DB
            // so ENTRY <-> EMPLOYEE_REGISTRATION changes apply without restart.
            _ = MonitorReaderConfigurationAsync();

            // Monitor RFID transactions for time-threshold violations
            _ = MonitorRfidViolationsAsync();
        }
        catch (Exception ex)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_SERVICE_START_ERROR",
                $"Error starting RFID service: {ex.Message}",
                null,
                null
            );
        }
    }

    private void StartInventoryLoop(MasterRfidReader reader)
    {
        if (!_rfidClients.TryGetValue(reader.ReaderId, out var client))
            return;

        if (_inventoryTokens.ContainsKey(reader.ReaderId))
            return;

        var cts = new CancellationTokenSource();

        _inventoryTokens[reader.ReaderId] = cts;

        _ = InventoryLoopAsync(reader, client, cts.Token);
    }

    private async Task InventoryLoopAsync(MasterRfidReader reader,RfidTcpClient client,CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // =====================================================
                // CHECK CONNECTION
                // =====================================================

                if (!client.IsConnected)
                {
                    int maxRetries = _configurationService.GetReaderConnectionRetryCount();

                    int retryIntervalSeconds = _configurationService.GetReaderConnectionRetryIntervalSeconds();

                    bool connected = false;

                    for (int retry = 1; retry <= maxRetries;retry++)
                    {
                        try
                        {
                            await client.ConnectAsync(reader.IpAddress,reader.Port,cancellationToken);

                            if (client.IsConnected)
                            {
                                connected = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Retry below
                        }

                        if (retry < maxRetries)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(retryIntervalSeconds),cancellationToken);
                        }
                    }

                    if (!connected)
                    {
                        await _systemLogService.LogAsync(
                            "RFID_SERVICE",
                            "ERROR",
                            "RFID_READER_NOT_CONNECTED",
                            $"Reader '{reader.ReaderName}' " +
                            $"could not reconnect after " +
                            $"{maxRetries} retries.",
                            reader.IpAddress,
                            reader.Port);

                        await Task.Delay(
                            TimeSpan.FromSeconds(
                                retryIntervalSeconds),
                            cancellationToken);

                        continue;
                    }

                    continue;
                }


                // =====================================================
                // MIFARE INVENTORY COMMAND
                // =====================================================

                byte[] command =
                {
                0x03,
                0x2F,
                0x01
            };


                // =====================================================
                // SEND COMMAND
                // =====================================================

                byte[]? response =
                    await client.SendCommandAsync(
                        command,
                        cancellationToken);


                // =====================================================
                // NO RESPONSE
                // =====================================================

                if (response == null)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            _configurationService
                                .GetInventoryIntervalSeconds()),
                        cancellationToken);

                    continue;
                }


                // =====================================================
                // PROCESS RESPONSE
                // =====================================================

                bool cardFound =
                    await ProcessInventoryResponseAsync(
                        reader,
                        response);


                // =====================================================
                // CARD FOUND
                // =====================================================

                if (cardFound)
                {
                    int cooldownSeconds =
                        _configurationService
                            .GetEntryExitCooldownSeconds();

                    await Task.Delay(
                        TimeSpan.FromSeconds(
                            cooldownSeconds),
                        cancellationToken);

                    continue;
                }


                // =====================================================
                // NO CARD
                // =====================================================

                int inventoryIntervalSeconds =
                    _configurationService
                        .GetInventoryIntervalSeconds();

                await Task.Delay(
                    TimeSpan.FromSeconds(
                        inventoryIntervalSeconds),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
            catch (Exception ex)
            {
                // IMPORTANT:
                // Never allow inventory loop to die silently.

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "ERROR",
                    "RFID_INVENTORY_LOOP_ERROR",
                    $"Inventory loop error for reader " +
                    $"'{reader.ReaderName}': {ex.Message}",
                    reader.IpAddress,
                    reader.Port);

                // Give the reader/client a moment before retrying.
                await Task.Delay(
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
            }
        }
    }

    //private async Task InventoryLoopAsync(MasterRfidReader reader,RfidTcpClient client, CancellationToken cancellationToken)
    //{
    //    while (!cancellationToken.IsCancellationRequested)
    //    {
    //        if (!client.IsConnected)
    //        {
    //            int maxRetries = _configurationService.GetReaderConnectionRetryCount();
    //            int retryIntervalSeconds =  _configurationService.GetReaderConnectionRetryIntervalSeconds();
    //            string lastError = "Reader is not connected.";

    //            bool connected = false;
    //            for (int retry = 1; retry <= maxRetries;retry++)
    //            {
    //                try
    //                {
    //                    // Try connecting again
    //                    await client.ConnectAsync(
    //                        reader.IpAddress,
    //                        reader.Port,
    //                        cancellationToken);

    //                    if (client.IsConnected)
    //                    {
    //                        connected = true;
    //                        break;
    //                    }
    //                }
    //                catch (Exception ex)
    //                {
    //                    lastError = ex.Message;
    //                }

    //                if (retry < maxRetries)
    //                {
    //                    await Task.Delay(
    //                        TimeSpan.FromSeconds(
    //                            retryIntervalSeconds),
    //                        cancellationToken);
    //                }
    //            }

    //            if (!connected)
    //            {
    //                await _systemLogService.LogAsync(
    //                    "RFID_SERVICE",
    //                    "ERROR",
    //                    "RFID_READER_NOT_CONNECTED",
    //                    $"Reader: {reader.ReaderName} " +
    //                    $"(IP: {reader.IpAddress}, " +
    //                    $"Port: {reader.Port}) " +
    //                    $"could not connect after {maxRetries} " +
    //                    $"retries. Error: {lastError}");

    //                break;
    //            }
    //            continue;
    //        }

    //        byte[] command =
    //            {
    //            0x03,
    //            0x2F,
    //            0x01
    //        };

    //        byte[]? response = await client.SendCommandAsync(command,cancellationToken);

    //        if (response != null)
    //        {
    //            bool cardFound = await ProcessInventoryResponseAsync(reader,response);

    //            if (cardFound)
    //            {
    //                int cooldownSeconds = _configurationService.GetEntryExitCooldownSeconds();

    //                await Task.Delay(TimeSpan.FromSeconds(cooldownSeconds),cancellationToken);
    //                continue;
    //            }
    //        }

    //        int inventoryIntervalSeconds =_configurationService.GetInventoryIntervalSeconds();

    //        await Task.Delay(TimeSpan.FromSeconds(inventoryIntervalSeconds), cancellationToken);
    //    }
    //}

    private async Task<bool> ProcessInventoryResponseAsync(MasterRfidReader reader, byte[] data)
    {
        try
        {
            // =========================================================
            // DISPLAY EXACT RAW HEX RECEIVED
            // =========================================================

            string rawHex =
                BitConverter
                    .ToString(data)
                    .Replace("-", " ");

            // RAW HEX

            //await _systemLogService.LogAsync(
            //    "RFID_SERVICE",
            //    "INFO",
            //    "RFID_INVENTORY_RAW",
            //    $"RAW HEX: {rawHex}",
            //    reader.IpAddress,
            //    reader.Port);


            // =========================================================
            // VALIDATE RESPONSE SIZE
            // =========================================================

            if (data.Length < 7)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_INVALID_RESPONSE",
                    $"Response too short. RAW HEX: {rawHex}",
                    reader.IpAddress,
                    reader.Port);

                return false;
            }


            // =========================================================
            // EXACT SAME CHECK AS OLD APPLICATION
            // =========================================================
            //
            // Old application:
            //
            // if (rxBuffer[4] != 0x00 ||
            //     rxBuffer[5] != 0x00)
            // {
            //     return false;
            // }
            //
            // =========================================================

            if (data[4] != 0x00 ||
                data[5] != 0x00)
            {
                //await _systemLogService.LogAsync(
                //    "RFID_SERVICE",
                //    "INFO",
                //    "RFID_NO_CARD",
                //    $"No card detected. RAW HEX: {rawHex}",
                //    reader.IpAddress,
                //    reader.Port);

                return false;
            }


            // =========================================================
            // UID LENGTH
            // =========================================================
            //
            // Old application:
            //
            // int uidLength = rxBuffer[6];
            //
            // =========================================================

            int uidLength =
                data[6];


            if (uidLength <= 0)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "INFO",
                    "RFID_NO_CARD",
                    $"UID length is 0. RAW HEX: {rawHex}",
                    reader.IpAddress,
                    reader.Port);

                return false;
            }


            if (uidLength > 8)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_INVALID_UID_LENGTH",
                    $"Invalid UID length: {uidLength}. RAW HEX: {rawHex}",
                    reader.IpAddress,
                    reader.Port);

                return false;
            }


            // =========================================================
            // CHECK THAT COMPLETE UID IS AVAILABLE
            // =========================================================

            if (data.Length < 7 + uidLength)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_INCOMPLETE_UID",
                    $"UID Length: {uidLength}, " +
                    $"Received Bytes: {data.Length}, " +
                    $"RAW HEX: {rawHex}",
                    reader.IpAddress,
                    reader.Port);

                return false;
            }


            // =========================================================
            // READ UID
            // =========================================================
            //
            // EXACT SAME LOGIC AS OLD APPLICATION
            //
            // UID starts from:
            //
            // rxBuffer[7]
            //
            // =========================================================

            byte[] uid =
                new byte[uidLength];

            string cardUid = string.Empty;


            for (int j = 0;
                 j < uidLength;
                 j++)
            {
                uid[j] =
                    data[7 + j];

                cardUid +=
                    uid[j].ToString("X2");
            }

            // =========================================================
            // EMPLOYEE REGISTRATION READER
            // =========================================================           
            if (reader.ReaderPurpose == "EMPLOYEE_REGISTRATION")
            {
                _employeeRegistrationService.SubmitCardUid(cardUid);

                return true;
            }


            // =========================================================
            // CARD FOUND
            // =========================================================

            //await _systemLogService.LogAsync(
            //    "RFID_SERVICE",
            //    "INFO",
            //    "RFID_CARD_FOUND",
            //    $"RAW HEX: {rawHex} | " +
            //    $"UID LENGTH: {uidLength} | " +
            //    $"UID: {cardUid}",
            //    reader.IpAddress,
            //    reader.Port);


            // =========================================================
            // IMPORTANT
            // =========================================================
            //
            // At this point cardUid contains the actual UID.
            //
            // Example:
            //
            // 5F3357EE
            //
            // =========================================================


            // =========================================================
            // CALL EXISTING CARD / PUNCH LOGIC
            // =========================================================
            //
            // If your existing HandleCardData() currently expects
            // byte[] data, we should NOT call it here because it may
            // extract the UID again incorrectly.
            //
            // Instead use cardUid directly in your existing
            // authorization / punch logic.
            //
            // Example:
            //
            RfidScanResult result = await _rfidTransactionService.ProcessCardAsync(reader, cardUid, reader.IpAddress,reader.Port, rawHex);
            // =========================================================


            return true;
        }
        catch (Exception ex)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_INVENTORY_PARSE_ERROR",
                $"Error processing RFID response: {ex.Message}",
                reader.IpAddress,
                reader.Port);

            return false;
        }
    }

    private async Task StartReaderAsync(
     MasterRfidReader reader)
    {
        try
        {
            // =====================================================
            // PREVENT DUPLICATE CLIENT
            // =====================================================

            if (_rfidClients.ContainsKey(reader.ReaderId))
            {
                return;
            }


            // =====================================================
            // CREATE TCP CLIENT
            // Application = TCP CLIENT
            // RFID Reader = TCP SERVER
            // =====================================================

            var client = new RfidTcpClient();


            // =====================================================
            // STORE CLIENT
            // =====================================================

            _rfidClients.Add(
                reader.ReaderId,
                client);


            // =====================================================
            // STATUS EVENT
            // =====================================================

            client.StatusChanged += status =>
            {
                HandleReaderStatus(
                    reader,
                    status);
            };


            // =====================================================
            // CARD DATA EVENT
            // =====================================================

            client.DataReceived +=
                (readerIp, readerPort, data) =>
                {
                    HandleCardData(
                        reader,
                        readerIp,
                        readerPort,
                        data);
                };


            // =====================================================
            // LOG CONNECTION ATTEMPT
            // =====================================================

            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "INFO",
                "TCP_CLIENT_CONNECTING",
                $"Connecting to RFID Reader " +
                $"'{reader.ReaderName}' " +
                $"({reader.ReaderPurpose}). " +
                $"IP: {reader.IpAddress}, " +
                $"Port: {reader.Port}.",
                reader.IpAddress,
                reader.Port);


            // =====================================================
            // CONNECT
            //
            // IP AND PORT COME FROM DATABASE
            // =====================================================

            bool connected =
                await client.ConnectAsync(
                    reader.IpAddress,
                    reader.Port);


            // =====================================================
            // CONNECTION FAILED
            // =====================================================

            if (!connected)
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_READER_CONNECTION_FAILED",
                    $"Could not connect to RFID Reader " +
                    $"'{reader.ReaderName}'. " +
                    $"IP: {reader.IpAddress}, " +
                    $"Port: {reader.Port}, " +
                    $"Purpose: {reader.ReaderPurpose}.",
                    reader.IpAddress,
                    reader.Port);
            }
        }
        catch (Exception ex)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "TCP_CLIENT_START_ERROR",
                $"Error connecting to RFID Reader " +
                $"'{reader.ReaderName}': {ex.Message}",
                reader.IpAddress,
                reader.Port);

            Console.WriteLine(
                $"[{reader.ReaderName}] TCP client error: {ex}");
        }
    }

    private async Task StopReaderAsync(
    long readerId,
    string reason)
    {
        if (!_rfidClients.TryGetValue(
                readerId,
                out var client))
        {
            return;
        }

        MasterRfidReader? reader =
            _activeReaders.FirstOrDefault(
                r => r.ReaderId == readerId);

        try
        {
            client.Disconnect();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Error stopping RFID reader " +
                $"{readerId}: {ex.Message}");
        }

        _rfidClients.Remove(readerId);

        lock (_connectedReaderIds)
        {
            _connectedReaderIds.Remove(readerId);
        }

        lock (_disconnectedReaderIds)
        {
            _disconnectedReaderIds.Remove(readerId);
        }

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "INFO",
            "TCP_CLIENT_STOPPED",
            $"Stopped TCP client for RFID reader_id={readerId}" +
            (reader == null
                ? string.Empty
                : $" '{reader.ReaderName}'") +
            $". Reason: {reason}.",
            reader?.IpAddress,
            reader?.Port);
    }

    /// <summary>
    /// Periodically reloads master_rfid_readers so purpose/IP/active changes
    /// from Dashboard apply without restarting RfidService.
    /// </summary>
    private async Task MonitorReaderConfigurationAsync()
    {
        int intervalSeconds = Math.Max(3,_configurationService.GetReaderConfigReloadIntervalSeconds());

        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
                await ReloadReaderConfigurationAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reader config reload error: {ex.Message}");

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "ERROR",
                    "READER_CONFIG_RELOAD_ERROR",
                    $"Error reloading RFID reader configuration: {ex.Message}",
                    null,
                    null
                );
            }
        }
    }

    private async Task ReloadReaderConfigurationAsync()
    {
        List<MasterRfidReader> latestReaders =
            await _readerConfigurationService.GetActiveReadersAsync();

        var latestById = latestReaders.ToDictionary(r => r.ReaderId);

        // Snapshot current active readers
        List<MasterRfidReader> currentReaders = _activeReaders.ToList();
        var currentById = currentReaders.ToDictionary(r => r.ReaderId);

        // Stop readers that were deactivated / removed from DB
        foreach (var existing in currentReaders)
        {
            if (!latestById.ContainsKey(existing.ReaderId))
            {
                await StopReaderAsync(
                    existing.ReaderId,
                    "Reader deactivated or removed from database");
            }
        }

        // Add or update readers from DB
        foreach (var dbReader in latestReaders)
        {
            if (!currentById.TryGetValue(dbReader.ReaderId, out var running))
            {
                // Brand new active reader
                _activeReaders.Add(dbReader);
                await StartReaderAsync(dbReader);

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "INFO",
                    "READER_CONFIG_ADDED",
                    $"Loaded new RFID reader '{dbReader.ReaderName}' " +
                    $"({dbReader.ReaderPurpose}) on port {dbReader.Port}.",
                    dbReader.IpAddress,
                    dbReader.Port
                );
                continue;
            }

            bool purposeChanged = !string.Equals(
                running.ReaderPurpose,
                dbReader.ReaderPurpose,
                StringComparison.OrdinalIgnoreCase);

            bool portChanged = running.Port != dbReader.Port;

            bool metaChanged =
                !string.Equals(running.ReaderName, dbReader.ReaderName, StringComparison.Ordinal)
                || !string.Equals(running.IpAddress, dbReader.IpAddress, StringComparison.Ordinal)
                || !string.Equals(running.ReaderSerialno, dbReader.ReaderSerialno, StringComparison.Ordinal);

            if (portChanged)
            {
                // Port change requires TCP restart with a fresh reader instance
                await StopReaderAsync(running.ReaderId, "Reader port changed in database");

                _activeReaders.RemoveAll(r => r.ReaderId == running.ReaderId);
                _activeReaders.Add(dbReader);
                await StartReaderAsync(dbReader);

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "INFO",
                    "READER_CONFIG_PORT_CHANGED",
                    $"RFID reader '{dbReader.ReaderName}' port changed " +
                    $"{running.Port} -> {dbReader.Port}; TCP server restarted. " +
                    $"Purpose: {dbReader.ReaderPurpose}.",
                    dbReader.IpAddress,
                    dbReader.Port
                );
                continue;
            }

            if (purposeChanged || metaChanged)
            {
                string oldPurpose = running.ReaderPurpose;

                // Mutate same instance so HandleCardData closures see new purpose immediately
                running.ReaderName = dbReader.ReaderName;
                running.ReaderSerialno = dbReader.ReaderSerialno;
                running.IpAddress = dbReader.IpAddress;
                running.ReaderPurpose = dbReader.ReaderPurpose;
                running.IsActive = dbReader.IsActive;
                running.UpdatedAt = dbReader.UpdatedAt;
                running.LastUpdatedBy = dbReader.LastUpdatedBy;

                if (purposeChanged)
                {
                    await _systemLogService.LogAsync(
                        "RFID_SERVICE",
                        "INFO",
                        "READER_PURPOSE_UPDATED",
                        $"RFID reader '{running.ReaderName}' purpose updated " +
                        $"{oldPurpose} -> {running.ReaderPurpose} " +
                        "(applied live, no service restart).",
                        running.IpAddress,
                        running.Port
                    );
                }
            }
        }

        // Drop deactivated readers from in-memory active list
        _activeReaders.RemoveAll(r => !latestById.ContainsKey(r.ReaderId));
    }

    private async Task MonitorReaderConnectionsAsync()
    {
        int retryIntervalSeconds =_configurationService.GetReaderConnectionRetryIntervalSeconds();

        int retryDurationMinutes = _configurationService.GetReaderConnectionRetryDurationMinutes();

        TimeSpan maximumRetryDuration = TimeSpan.FromMinutes(retryDurationMinutes);

        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < maximumRetryDuration)
        {
            List<MasterRfidReader> disconnectedReaders;

            lock (_connectedReaderIds)
            {
                disconnectedReaders = _activeReaders.Where(reader => !_connectedReaderIds.Contains(reader.ReaderId)).ToList();
            }

            if (disconnectedReaders.Count == 0)
            {
                await LogAllReadersConnectedAsync();
                return;
            }

            foreach (var reader in disconnectedReaders)
            {
                await HandleReaderNotConnectedAsync(reader);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(retryIntervalSeconds)
            );
        }

        await LogFinalDisconnectedReadersAsync();
    }

    private async Task HandleReaderNotConnectedAsync(MasterRfidReader reader)
    {
        // Prevent the same disconnected reader
        // from being logged repeatedly on every retry
        bool newlyDisconnected;

        lock (_disconnectedReaderIds)
        {
            newlyDisconnected =
                _disconnectedReaderIds.Add(reader.ReaderId);
        }

        if (!newlyDisconnected)
        {
            return;
        }

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "WARNING",
            "RFID_READER_NOT_CONNECTED",
            $"RFID Reader '{reader.ReaderName}' is not connected. " +
            $"IP: {reader.IpAddress}, Port: {reader.Port}, " +
            $"Purpose: {reader.ReaderPurpose}.",
            reader.IpAddress,
            reader.Port
        );
    }

    private async Task LogAllReadersConnectedAsync()
    {
        // Prevent duplicate ALL_READERS_CONNECTED logs
        if (_allReadersStatusLogged)
        {
            return;
        }

        _allReadersStatusLogged = true;

        string readerDetails = string.Join(
            ", ",
            _activeReaders.Select(reader =>
                $"{reader.ReaderName} " +
                $"(IP: {reader.IpAddress}, Port: {reader.Port}, " +
                $"Purpose: {reader.ReaderPurpose})"
            )
        );

        await _systemLogService.LogAsync(
            "RFID_SERVICE",
            "INFO",
            "ALL_RFID_READERS_CONNECTED",
            $"All {_activeReaders.Count} active RFID readers are connected. " +
            $"Readers: {readerDetails}",
            null,
            null
        );
    }

    private async Task LogFinalDisconnectedReadersAsync()
    {
        List<MasterRfidReader> disconnectedReaders;

        lock (_connectedReaderIds)
        {
            disconnectedReaders = _activeReaders
                .Where(reader =>
                    !_connectedReaderIds.Contains(reader.ReaderId)
                )
                .ToList();
        }

        // Safety check
        if (disconnectedReaders.Count == 0)
        {
            await LogAllReadersConnectedAsync();
            return;
        }

        foreach (var reader in disconnectedReaders)
        {
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_READER_CONNECTION_FAILED",
                $"RFID Reader '{reader.ReaderName}' could not be connected " +
                $"within the configured retry duration. " +
                $"IP: {reader.IpAddress}, " +
                $"Port: {reader.Port}, " +
                $"Purpose: {reader.ReaderPurpose}.",
                reader.IpAddress,
                reader.Port
            );
        }
    }

    private async void HandleReaderStatus(MasterRfidReader reader,string status)
    {
        try
        {
            Console.WriteLine(
                $"[{reader.ReaderName}] RFID Status: {status}"
            );

            if (status.StartsWith("RFID_READER_CONNECTED"))
            {
                lock (_connectedReaderIds)
                {
                    _connectedReaderIds.Add(reader.ReaderId);
                }

                lock (_disconnectedReaderIds)
                {
                    _disconnectedReaderIds.Remove(reader.ReaderId);
                }

                ReaderConnected?.Invoke(
                    reader,
                    status
                );

                ReaderConnectionStatusChanged?.Invoke();

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "INFO",
                    "RFID_READER_CONNECTED",
                    $"RFID Reader '{reader.ReaderName}' connected successfully. " +
                    $"IP: {reader.IpAddress}, " +
                    $"Port: {reader.Port}, " +
                    $"Purpose: {reader.ReaderPurpose}.",
                    reader.IpAddress,
                    reader.Port
                );
            }
            // ==========================================
            // READER DISCONNECTED
            // ==========================================

            else if (status.StartsWith("RFID_READER_DISCONNECTED"))
            {
                lock (_connectedReaderIds)
                {
                    _connectedReaderIds.Remove(reader.ReaderId);
                }

                lock (_disconnectedReaderIds)
                {
                    _disconnectedReaderIds.Add(reader.ReaderId);
                }

                ReaderConnectionStatusChanged?.Invoke();

                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_READER_DISCONNECTED",
                    $"RFID Reader '{reader.ReaderName}' disconnected. " +
                    $"IP: {reader.IpAddress}, " +
                    $"Port: {reader.Port}, " +
                    $"Purpose: {reader.ReaderPurpose}.",
                    reader.IpAddress,
                    reader.Port
                );
            }

            else if (status.StartsWith("RFID_TX|"))
            {
                //await _systemLogService.LogAsync(
                //    "RFID_SERVICE",
                //    "INFO",
                //    "RFID_TX",
                //    status,
                //    reader.IpAddress,
                //    reader.Port
                //);
            }
            else if (status.StartsWith("RFID_RX|"))
            {
                //await _systemLogService.LogAsync(
                //    "RFID_SERVICE",
                //    "INFO",
                //    "RFID_RX",
                //    status,
                //    reader.IpAddress,
                //    reader.Port
                //);
            }
            else if (status.StartsWith("RFID_READER_NOT_CONNECTED"))
            {
                await _systemLogService.LogAsync(
                    "RFID_SERVICE",
                    "WARNING",
                    "RFID_READER_NOT_CONNECTED",
                    status,
                    reader.IpAddress,
                    reader.Port
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"RFID status handling error: {ex.Message}"
            );
        }
    }

    private async void HandleCardData(MasterRfidReader reader, string readerIp,int readerPort, byte[] data)
    {
        try
        {
            // ==========================================
            // CONVERT COMPLETE RFID PACKET TO HEX
            // ==========================================

            string rawHexData =BitConverter.ToString(data).Replace("-", "");

            await _systemLogService.LogAsync(
           "RFID_SERVICE",
           "INFO",
           "RFID_RAW_CARD_RESPONSE",
           $"RAW HEX: {rawHexData}",
           readerIp,
           readerPort);

            // ==========================================
            // EXTRACT ACTUAL CARD UID
            // Tested logic from your previous code
            // ==========================================

            string cardUid;

            if (rawHexData.Length >= 16)
            {
                cardUid =rawHexData.Substring(12, 8);
            }
            else
            {
                cardUid = rawHexData;
            }

            //Console.WriteLine(
            //    $"RFID Card Detected. " +
            //    $"Reader: {reader.ReaderName}, " +
            //    $"Purpose: {reader.ReaderPurpose}, " +
            //    $"Card UID: {cardUid}"
            //);

            // ==========================================
            // LOG CARD DETECTION
            // ==========================================

            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "INFO",
                "RFID_CARD_DETECTED",
                $"RFID card detected from reader " +
                $"'{reader.ReaderName}'. " +
                $"Card UID: {cardUid}. " +
                $"Purpose: {reader.ReaderPurpose}.",
                readerIp,
                readerPort
            );

            // ==========================================
            // EMPLOYEE REGISTRATION READER
            //
            // Do NOT process through RfidTransactionService.
            //
            // RFID Service only reads the card UID.
            // Dashboard Service will:
            // - validate the card
            // - check if already assigned
            // - show error if required
            // - save the employee and card UID
            // ==========================================


            if (reader.ReaderPurpose == RfidReaderPurpose.EMPLOYEE_REGISTRATION)
            {
                _employeeRegistrationService.SubmitCardUid(cardUid);

                var registrationResult = new RfidScanResult
                {
                    ResultType = RfidScanResultType.EMPLOYEE_REGISTRATION,
                    Message = "RFID card scanned successfully.",
                    CardUid = cardUid,
                    ShouldAlertDashboard = true,
                    ShouldAnnounce = false
                };                

                ScanProcessed?.Invoke(
                    reader,
                    readerIp,
                    readerPort,
                    registrationResult
                );

                await HandleScanResultAsync(
                    reader,
                    readerIp,
                    readerPort,
                    registrationResult
                );

                return;
            }

     
           

            // ==========================================
            // NORMAL RFID READERS
            //
            // ENTRY
            // EXIT
            // ENTRY_EXIT
            //
            // Send these to transaction service
            // ==========================================

            RfidScanResult result = await _rfidTransactionService.ProcessCardAsync(reader,cardUid,readerIp, readerPort,rawHexData);

            // ==========================================
            // SEND RAW DATA EVENT IF NEEDED
            // ==========================================

            CardDataReceived?.Invoke(reader, readerIp,readerPort,data);

            // ==========================================
            // SEND FINAL RESULT
            //
            // Dashboard can later use this for:
            // - alerts
            // - announcements
            // - unauthorized card
            // - cooldown scan
            // - access denied
            // ==========================================

            ScanProcessed?.Invoke(reader,readerIp,readerPort,result);

            // ==========================================
            // HANDLE DASHBOARD / ANNOUNCEMENT
            // ==========================================

            await HandleScanResultAsync(reader,readerIp,readerPort,result);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"RFID card handling error: {ex.Message}"
            );

            // ==========================================
            // CREATE ERROR RESULT
            // ==========================================         
            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_CARD_PROCESSING_ERROR",
                $"Error processing RFID card: {ex.Message}",
                readerIp,
                readerPort
            );

            var errorResult = new RfidScanResult
            {
                ResultType =
                 RfidScanResultType.ERROR,

                Message =
                 $"Error processing RFID card: {ex.Message}",

                CardUid = string.Empty,

                ShouldAlertDashboard = true,

                ShouldAnnounce = false
            };


            // Notify listeners about the error
            ScanProcessed?.Invoke(
                reader,
                readerIp,
                readerPort,
                errorResult
            );
        }
    }

    private async Task HandleScanResultAsync(MasterRfidReader reader, string readerIp,int readerPort, RfidScanResult result)
    {
        try
        {
            // ==========================================
            // LOG RESULT IN CONSOLE FOR NOW
            // ==========================================

            Console.WriteLine($"RFID Scan Result: {result.ResultType} | " +
                $"Card UID: {result.CardUid} | " +
                $"Message: {result.Message}"
            );

            // ==========================================
            // DASHBOARD ALERT
            // ==========================================

            if (result.ShouldAlertDashboard)
            {
                Console.WriteLine(
                    $"DASHBOARD ALERT REQUIRED: {result.Message}"
                );

                // Dashboard Service integration
                // will be added here later
            }

            // ==========================================
            // AUDIO ANNOUNCEMENT
            // ==========================================

            if (result.ShouldAnnounce)
            {
                Console.WriteLine(
                    $"ANNOUNCEMENT REQUIRED: {result.Message}"
                );

                // Audio announcement integration
                // will be added here later
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Error handling scan result: {ex.Message}"
            );

            await _systemLogService.LogAsync(
                "RFID_SERVICE",
                "ERROR",
                "RFID_SCAN_RESULT_HANDLING_ERROR",
                $"Error handling scan result for card " +
                $"{result.CardUid}: {ex.Message}",
                readerIp,
                readerPort
            );
        }
    }

    private async Task MonitorRfidViolationsAsync()
    {
        int intervalSeconds = _configurationService.GetViolationCheckIntervalSeconds();

        while (true)
        {
            try
            {
                await _rfidViolationService.CheckAndMarkTimeViolationsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine( $"RFID violation monitor error: {ex}"
                );
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds)
            );
        }
    }    

    // ==========================================
    // START EMPLOYEE REGISTRATION SCAN
    // ==========================================

    public void StartEmployeeRegistrationScan()
    {
        lock (_employeeRegistrationLock)
        {
            _employeeRegistrationActive = true;

            // Clear previous result
            _employeeRegistrationResult = null;
        }
    }


    // ==========================================
    // GET EMPLOYEE REGISTRATION RESULT
    // ==========================================

    public RfidScanResult? GetEmployeeRegistrationResult()
    {
        lock (_employeeRegistrationLock)
        {
            return _employeeRegistrationResult;
        }
    }


    // ==========================================
    // CANCEL EMPLOYEE REGISTRATION SCAN
    // ==========================================

    public void CancelEmployeeRegistrationScan()
    {
        lock (_employeeRegistrationLock)
        {
            _employeeRegistrationActive = false;

            _employeeRegistrationResult = null;
        }
    }

    public List<MasterRfidReader> GetConnectedReaders()
    {
        lock (_connectedReaderIds)
        {
            return _activeReaders
                .Where(reader =>
                    _connectedReaderIds.Contains(reader.ReaderId))
                .ToList();
        }
    }

    public List<MasterRfidReader> GetDisconnectedReaders()
    {
        lock (_connectedReaderIds)
        {
            return _activeReaders
                .Where(reader =>
                    !_connectedReaderIds.Contains(reader.ReaderId))
                .ToList();
        }
    }

    public void Stop()
    {
        if (_serviceStopped)
        {
            return;
        }

        _serviceStopped = true;

        _ = _systemLogService.LogAsync(
            "RFID_SERVICE",
            "INFO",
            "RFID_SERVICE_STOPPED",
            "RFID service stopped.",
            null,
            null);

        foreach (var client in _rfidClients.Values)
        {
            try
            {
                client.Disconnect();
            }
            catch
            {
            }
        }

        _rfidClients.Clear();

        lock (_connectedReaderIds)
        {
            _connectedReaderIds.Clear();
        }

        lock (_disconnectedReaderIds)
        {
            _disconnectedReaderIds.Clear();
        }

        _allReadersStatusLogged = false;
    }
}