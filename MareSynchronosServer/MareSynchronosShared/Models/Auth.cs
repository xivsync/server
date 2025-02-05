using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class Auth
{
    [Key]
    [MaxLength(64)]
    public string HashedKey { get; set; }

    public string UserUID { get; set; }
    public User User { get; set; }
    public bool MarkForBan { get; set; }
    public bool IsBanned { get; set; }
    
    [Column(TypeName = "jsonb")] 
    public List<string>? CharaIds { get; set; } = [];
    
    public string? PrimaryUserUID { get; set; }
    public User? PrimaryUser { get; set; }
}
