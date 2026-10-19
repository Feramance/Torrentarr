using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Services;

public static class MaintenanceRequirements
{
    public static bool Enabled(MaintenanceOperation operation, MaintenanceConfig config) => config.Enabled && (operation switch
    {
        MaintenanceOperation.OrphanScan => config.Orphans.Enabled,
        MaintenanceOperation.HardlinkAudit => config.Hardlinks.Enabled,
        MaintenanceOperation.UnregisteredCleanup => config.Unregistered.Enabled,
        MaintenanceOperation.AutomaticManagement => config.AutomaticManagement.Enabled,
        MaintenanceOperation.PrivateTagging => !string.IsNullOrWhiteSpace(config.AutomaticManagement.PrivateTag),
        MaintenanceOperation.TrackerErrorTagging => !string.IsNullOrWhiteSpace(config.AutomaticManagement.TrackerErrorTag),
        MaintenanceOperation.RepairPaused => config.Repair.Enabled,
        MaintenanceOperation.RecycleRetention => config.RecycleBin.Enabled,
        _ => true
    });

    public static InventoryRequirements For(MaintenanceOperation operation, MaintenanceConfig config) => operation switch
    {
        MaintenanceOperation.OrphanScan => InventoryRequirements.Metadata | InventoryRequirements.Files | InventoryRequirements.LocalFiles | InventoryRequirements.GlobalOwnership,
        MaintenanceOperation.HardlinkAudit => InventoryRequirements.Metadata | InventoryRequirements.Files | InventoryRequirements.LocalFiles,
        MaintenanceOperation.UnregisteredCleanup => InventoryRequirements.Metadata | InventoryRequirements.Trackers,
        MaintenanceOperation.CategoryReconcile => InventoryRequirements.Metadata | (config.TrackerAssignments.Count > 0 ? InventoryRequirements.Trackers : InventoryRequirements.None),
        MaintenanceOperation.TrackerErrorTagging => InventoryRequirements.Metadata | InventoryRequirements.Trackers,
        MaintenanceOperation.RecycleRetention => InventoryRequirements.None,
        _ => InventoryRequirements.Metadata
    };

    public static bool MovesContent(MaintenanceActionKind kind) => kind is MaintenanceActionKind.QuarantineFile
        or MaintenanceActionKind.RecycleContent;
}
