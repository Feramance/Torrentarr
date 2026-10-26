using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_maintenance_state")]
public sealed class MaintenanceState
{
    [Key] public string Key { get; set; } = "";
    public string Data { get; set; } = "{}";
}
