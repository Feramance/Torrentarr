using Tomlyn.Serialization;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Configuration;

public class MaintenanceOperationPolicy
{
    public bool? Enabled { get; set; }
    public string ExecutionMode { get; set; } = "manual";
    public string Schedule { get; set; } = "";
    public List<string> IncludedCategories { get; set; } = [];
    public List<string> ExcludedCategories { get; set; } = [];
    public List<string> ExcludedTags { get; set; } = [];
    public int? MaxActionsPerRun { get; set; }
}

public class ErrorRecoveryConfig
{
    public bool Enabled { get; set; }
    public string Outcome { get; set; } = "keep";
    public List<string> States { get; set; } = ["error", "missingFiles"];
    public int MaxRepairAttempts { get; set; } = 1;
    public int MaxPerRun { get; set; } = 2;
    public int RetryMinutes { get; set; } = 10;
    public int RecheckTimeoutMinutes { get; set; } = 60;
    public int SustainedFailureMinutes { get; set; } = 1440;
    public int HealthyResetMinutes { get; set; } = 60;
}

public class TrackerProtectionConfig
{
    public string Policy { get; set; } = "custom";
    public double MinimumSeedHours { get; set; } = 48;
    public double MinimumRatio { get; set; } = 1;
    public bool AllowClientRatioClearance { get; set; }
    public string PartialPolicy { get; set; } = "preserve";
    public double DownloadThresholdPercent { get; set; } = 10;
    public bool ThresholdInclusive { get; set; } = true;
    public double? FulfillmentHours { get; set; }
    public string DeadlineStart { get; set; } = "added";
    public double AdditionalSeedHours { get; set; } = 6;
    public int AccountingGraceMinutes { get; set; } = 60;
    public double RecoveryReserveHours { get; set; } = 6;
}

public static class MaintenancePolicies
{
    public static MaintenanceOperationPolicy? Get(MaintenanceConfig config, MaintenanceOperation operation)
        => config.Operations.GetValueOrDefault(operation.ToString());
    public static string Mode(MaintenanceConfig config, MaintenanceOperation operation)
        => Get(config, operation)?.ExecutionMode ?? "manual";
    public static string Schedule(MaintenanceConfig config, MaintenanceOperation operation)
        => Get(config, operation) is { Schedule.Length: > 0 } policy ? policy.Schedule : Get(config, operation) == null && operation == MaintenanceOperation.ErrorRecovery ? SuggestedSchedule(operation) : config.Schedule;
    public static string SuggestedSchedule(MaintenanceOperation operation) => operation switch
    {
        MaintenanceOperation.ErrorRecovery => "*/5 * * * *",
        MaintenanceOperation.CategoryReconcile => "*/15 * * * *",
        MaintenanceOperation.OrphanScan => "15 3 * * *",
        MaintenanceOperation.HardlinkAudit => "15 4 * * *",
        MaintenanceOperation.RecycleRetention => "45 4 * * *",
        _ => "*/30 * * * *"
    };
}
