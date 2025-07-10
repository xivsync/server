using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.Group;

namespace MareSynchronosShared.Models;

public class PFinder
{
    [Key]
    public Guid Guid { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public DateTime LastUpdate { get; set; }

    [MaxLength(128)]
    public string Title { get; set; }

    [MaxLength(1024)]
    public string Description { get; set; }

    [MaxLength(512)]
    public string Tags { get; set; }

    public bool IsNSFW { get; set; }

    public bool Open { get; set; }

    public User User { get; set; }
    [MaxLength(20)]
    public string UserId { get; set; }

    public Group Group { get; set; }
    [MaxLength(20)]
    public string GroupId { get; set; }

    public PFinder() { }

    public PFinder(PFinderDto pFinderDto)
    {
        Guid = pFinderDto.Guid;
        StartTime = pFinderDto.StartTime;
        EndTime = pFinderDto.EndTime;
        LastUpdate = pFinderDto.LastUpdate;
        Title = pFinderDto.Title;
        Description = pFinderDto.Description;
        Tags = pFinderDto.Tags;
        IsNSFW = pFinderDto.IsNSFW;
        Open = pFinderDto.Open;
        UserId = pFinderDto.User.UID;
        GroupId = pFinderDto.Group.GID;
    }

    public void Update(PFinderDto pFinderDto)
    {
        StartTime = pFinderDto.StartTime;
        EndTime = pFinderDto.EndTime;
        LastUpdate = pFinderDto.LastUpdate;
        Title = pFinderDto.Title;
        Description = pFinderDto.Description;
        Tags = pFinderDto.Tags;
        IsNSFW = pFinderDto.IsNSFW;
        Open = pFinderDto.Open;
        UserId = pFinderDto.User.UID;
        GroupId = pFinderDto.Group.GID;
    }

    public PFinderDto ToDto()
    {
        return new PFinderDto(Guid, StartTime, EndTime, LastUpdate, Title, Description, Tags, IsNSFW, Open, new GroupData(GroupId), new UserData(UserId));
    }
}