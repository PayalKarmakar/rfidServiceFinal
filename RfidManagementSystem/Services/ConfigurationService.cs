using Microsoft.Extensions.Configuration;

namespace RfidManagementSystem.Services;

public class ConfigurationService
{
    private readonly IConfiguration _configuration;

    public ConfigurationService()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(
                "appsettings.json",
                optional: false,
                reloadOnChange: true
            )
            .Build();
    }

    public string GetConnectionString()
    {
        return _configuration.GetConnectionString(
            "PostgreSqlConnection"
        )
        ?? throw new Exception(
            "PostgreSQL connection string is missing."
        );
    }

    //public int GetEntryPort()
    //{
    //    return int.Parse(
    //        _configuration["RfidSettings:EntryPort"]
    //        ?? throw new Exception("EntryPort is missing.")
    //    );
    //}

    //public int GetExitPort()
    //{
    //    return int.Parse(
    //        _configuration["RfidSettings:ExitPort"]
    //        ?? throw new Exception("ExitPort is missing.")
    //    );
    //}


    public int GetEntryExitGapSeconds()
    {
        return int.Parse(
            _configuration["RfidSettings:EntryExitGapSeconds"]
            ?? "30"
        );
    }

    public int GetReaderConnectionRetryIntervalSeconds()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:ReaderConnectionRetryIntervalSeconds"
            ] ?? "2"
        );
    }

    public int GetReaderConnectionRetryDurationMinutes()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:ReaderConnectionRetryDurationMinutes"
            ] ?? "5"
        );
    }

    public int GetReaderConfigReloadIntervalSeconds()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:ReaderConfigReloadIntervalSeconds"
            ] ?? "10"
        );
    }

    public int GetViolationCheckIntervalSeconds()
    {
        return _configuration
            .GetValue<int>("RfidSettings:ViolationCheckIntervalSeconds");
    }

    public int GetInventoryIntervalSeconds()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:InventoryIntervalSeconds"
            ] ?? "1"
        );
    }

    public int GetEntryExitCooldownSeconds()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:EntryExitCooldownSeconds"
            ] ?? "10");
    }

    public int GetReaderConnectionRetryCount()
    {
        return int.Parse(
            _configuration[
                "RfidSettings:ReaderConnectionRetryCount"
            ] ?? "5");
    }
}