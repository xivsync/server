using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

// Value Converter
public class ListToStringConverter : ValueConverter<List<string>, string>
{
    public ListToStringConverter() : base(
        v => JsonSerializer.Serialize(v, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull}), // Or JsonConvert.SerializeObject(v)
        v => (v == null || v == "null") ? new List<string>() : JsonSerializer.Deserialize<List<string>>(v, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })) // Or JsonConvert.DeserializeObject<List<string>>(v)
    { }
}

// Value Comparer
public class ListValueComparer : ValueComparer<List<string>>
{
    public ListValueComparer() : base(
        (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
        c => c == null ? 0 : c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
        c => c == null ? null : c.ToList())
    { }
}
