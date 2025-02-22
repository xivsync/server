using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public class Support
{
    [Key]
    public ulong DiscordId { get; set; }

    public DateTime? ExpiresAt { get; set; }

    [MaxLength(100)]
    public string? LastOrder { get; set; }

    [MaxLength(100)]
    public string? UserId { get; set; }

    public string UserUID { get; set; }
    public User User { get; set; }

}