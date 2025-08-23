using Discord;
using Discord.Interactions;

namespace MareSynchronosServices.Discord;

// todo: remove all this crap at some point

public class LodestoneModal : IModal
{
    public string Title => "Lodestone Verification";

    [InputLabel("Enter Your Lode Stone Characters UID")]
    [ModalTextInput("lodestone_url", TextInputStyle.Short, "10000000")]
    public string LodestoneUrl { get; set; }
}
