using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MareSynchronosShared.Models;

public class Warning
{
    [Key]
    public ulong DiscordId { get; set; }

    public DateTime Time { get; set; }

    public string? Reason { get; set; }

}