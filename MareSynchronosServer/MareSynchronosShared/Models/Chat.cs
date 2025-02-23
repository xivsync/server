using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MareSynchronosShared.Models;

public class Chat
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    public DateTime TimeStamp { get; set; }
    public string Message { get; set; }

    public User Sender { get; set; }
    [MaxLength(20)]
    public string SenderId { get; set; }

    public Group Group { get; set; }
    [MaxLength(20)]
    public string GroupId { get; set; }
}