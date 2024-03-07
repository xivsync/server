using Discord;
using Discord.Interactions;

namespace MareSynchronosServices.Discord;

// todo: remove all this crap at some point

public class LodestoneModal : IModal
{
    public string Title => "通过石之家认证";

    [InputLabel("输入您角色的石之家 UID")]
    [ModalTextInput("lodestone_url", TextInputStyle.Short, "10000000")]
    public string LodestoneUrl { get; set; }
}
