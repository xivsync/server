using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;
[Serializable]
public class Moodles
{
    public enum StatusType
    {
        Positive, Negative, Special
    }

    [Key]
    [MaxLength(36)]
    public string GUID { get; set; }
    public int IconID { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public StatusType Type { get; set; }
    public string Applier { get; set; }
    public bool Dispelable  { get; set; }
    public int Stacks { get; set; }
    public Guid StatusOnDispell { get; set; }
    public string CustomFXPath { get; set; }
    public bool StackOnReapply  { get; set; }
    public int StacksIncOnReapply  { get; set; }
    public int Days { get; set; }
    public int Hours { get; set; }
    public int Minutes { get; set; }
    public int Seconds { get; set; }
    public bool NoExpire { get; set; }
    public bool AsPermanent { get; set; }

    public User User { get; set; }
    public string UserUID { get; set; }

}