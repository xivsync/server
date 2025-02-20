using System.Diagnostics;
using Discord;
using Discord.Interactions;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    private static readonly string AfdAddress = "https://ifdian.net/order/create?plan_id={0}&product_type=0&custom_order_id={1}";
    private static readonly string Plan5 = "1319dc64ef6611efac7552540025c377";
    private static readonly string Plan10 = "4c992b16ef6611ef822752540025c377";

    private string GetAddress(string plan)
    {
        return string.Format(AfdAddress, plan, Context.User.Id);
    }

    [ComponentInteraction("wizard-support")]
    public async Task ComponentSupport()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Gold);
        eb.WithTitle("赞助者界面");

        ComponentBuilder cb = new();
        cb.WithButton(label:"赞助5元", url: GetAddress(Plan5), style: ButtonStyle.Link, emote: new Emoji("💎"));
        cb.WithButton(label:"赞助10元", url: GetAddress(Plan10), style: ButtonStyle.Link, emote: new Emoji("💎"));
        cb.WithButton(label: "刷新", customId: "wizard-support", ButtonStyle.Secondary, new Emoji("🔄"));
        cb.WithButton("返回主菜单", "wizard-home:false", ButtonStyle.Secondary, new Emoji("🏠"), row: 1);

        var user = await mareDb.Supports.AsNoTracking().FirstOrDefaultAsync(x => x.DiscordId == Context.User.Id).ConfigureAwait(false);
        if (user is null)
        {
            eb.WithDescription("未找到你的赞助信息, 如有疑问请联系管理员.");
        }
        else if (user.UserId is not null && user.LastOrder is not null && user.ExpiresAt is not null)
        {
            if (user.ExpiresAt > DateTime.UtcNow)
            {
                await _botServices.UpdateSupporterForUser(Context.User,
                        _mareServicesConfiguration.GetValueOrDefault(nameof(ServicesConfiguration.Supporter),
                            new ulong?()))
                    .ConfigureAwait(false);
            }

            eb.AddField("赞助到期时间:",
                $"<t:{new DateTimeOffset(user.ExpiresAt.Value.ToUniversalTime()).ToUnixTimeSeconds()}:f>");
            eb.AddField("最后一次赞助订单号:", $"{user.LastOrder}");
            eb.AddField("最后一次赞助的用户ID", $"{user.UserId}");
        }
        else
        {
            eb.WithDescription("数据库错误, 请联系管理员");
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

}