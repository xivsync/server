using Discord;
using Discord.Interactions;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Models;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Services;
using StackExchange.Redis;
using MareSynchronos.API.Data.Enum;
using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosServices.Discord;

public class MareModule : InteractionModuleBase
{
    private readonly ILogger<MareModule> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ServerTokenGenerator _serverTokenGenerator;

    public MareModule(ILogger<MareModule> logger, IServiceProvider services,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer, ServerTokenGenerator serverTokenGenerator)
    {
        _logger = logger;
        _services = services;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
        _serverTokenGenerator = serverTokenGenerator;
    }

    [SlashCommand("userinfo", "显示您的用户信息")]
    public async Task UserInfo([Summary("secondary_uid", "（可选）您的辅助UID")] string? secondaryUid = null,
        [Summary("discord_user", "仅限管理员：要检查的 Discord 用户")] IUser? discordUser = null,
        [Summary("uid", "仅限管理员：要检查的 UID")] string? uid = null,
        [Summary("lodestone", "仅限管理员：要检查的LodeStone账户")] string? lodestone = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}",
            Context.Interaction.User.Id, nameof(UserInfo));

        try
        {
            EmbedBuilder eb = new();

            eb = await HandleUserInfo(eb, Context.User.Id, secondaryUid, discordUser?.Id ?? null, uid, lodestone);

            await RespondAsync(embeds: new[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("发生了错误");
            eb.WithDescription("请上报该BUG: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [SlashCommand("useradd", "仅限管理员：无条件添加用户到数据库")]
    public async Task UserAdd([Summary("desired_uid", "用户 UID")] string desiredUid)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Interaction.User.Id, nameof(UserAdd),
            string.Join(",", new[] { $"{nameof(desiredUid)}:{desiredUid}" }));

        try
        {
            var embed = await HandleUserAdd(desiredUid, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("发生了错误");
            eb.WithDescription("请上报该BUG: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    [SlashCommand("mod", "仅限Admin：调整管理")]
    public async Task Mod([Summary("discord_user", "仅限管理员：目标的 Discord 用户")] IUser discordUser = null,
        [Summary("arg", "参数: add 或 remove")]string arg = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{params}",
            Context.Interaction.User.Id, nameof(Mod),
            string.Join(",", new[] { $"{nameof(discordUser)}:{discordUser}", $"{nameof(arg)}:{arg}" }));

        try
        {
            var embed = await HandleMod(discordUser, arg, Context.User.Id);

            await RespondAsync(embeds: new[] { embed }, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            EmbedBuilder eb = new();
            eb.WithTitle("发生了错误");
            eb.WithDescription("请上报该BUG: " + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine);

            await RespondAsync(embeds: new Embed[] { eb.Build() }, ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task<Embed> HandleMod(IUser targetUser, string arg, ulong discordUserId)
    {
        var embed = new EmbedBuilder();

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        var target = (await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == targetUser.Id))?.User;

        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == discordUserId))?.User?.IsAdmin ?? true)
        {
            embed.WithTitle("修改权限失败");
            embed.WithDescription("权限不足");
        }
        else
        {
            if (target != null)
            {
                target.IsModerator = arg switch
                {
                    "add" => true,
                    "remove" => false,
                    _ => target.IsModerator,
                };
                db.Users.Update(target);
                embed.WithTitle("已更新用户的权限");
                embed.WithDescription($"<@{targetUser.Id}> 的权限已变更为: {(target.IsModerator ? "管理员":"非管理员")}");
            }
            else
            {
                embed.WithTitle("修改权限失败");
                embed.WithDescription("用户未找到");
            }

            await db.SaveChangesAsync();
        }

        return embed.Build();
    }

    [SlashCommand("message", "仅管理员：向客户端发送消息")]
    public async Task SendMessageToClients([Summary("message", "要发送的消息")] string message,
        [Summary("severity", "消息严重性")] MessageSeverity messageType = MessageSeverity.Information,
        [Summary("uid", "要发送给的用户UID")] string? uid = null)
    {
        _logger.LogInformation("SlashCommand:{userId}:{Method}:{message}:{type}:{uid}", Context.Interaction.User.Id, nameof(SendMessageToClients), message, messageType, uid);

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();

        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == Context.Interaction.User.Id))?.User?.IsAdmin ?? true)
        {
            await RespondAsync("权限不足", ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(uid) && !await db.Users.AnyAsync(u => u.UID == uid))
        {
            await RespondAsync("UID不存在", ephemeral: true).ConfigureAwait(false);
            return;
        }

        try
        {
            using HttpClient c = new HttpClient();
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _serverTokenGenerator.Token);
            await c.PostAsJsonAsync(new Uri(_mareServicesConfiguration.GetValue<Uri>
                (nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage"), new ClientMessage(messageType, message, uid ?? string.Empty))
                .ConfigureAwait(false);

            var discordChannelForMessages = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForMessages), null);
            if (uid == null && discordChannelForMessages != null)
            {
                var discordChannel = await Context.Guild.GetChannelAsync(discordChannelForMessages.Value) as IMessageChannel;
                if (discordChannel != null)
                {
                    var embedColor = messageType switch
                    {
                        MessageSeverity.Information => Color.Blue,
                        MessageSeverity.Warning => new Color(255, 255, 0),
                        MessageSeverity.Error => Color.Red,
                        _ => Color.Blue
                    };

                    EmbedBuilder eb = new();
                    eb.WithTitle(messageType + " 服务器信息");
                    eb.WithColor(embedColor);
                    eb.WithDescription(message);

                    await discordChannel.SendMessageAsync(embed: eb.Build());
                }
            }

            await RespondAsync("消息已发送", ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RespondAsync($"对{new Uri(_mareServicesConfiguration.GetValue<Uri>
                (nameof(ServicesConfiguration.MainServerAddress)), "/msgc/sendMessage")} 消息发送失败: " + ex.Message, ephemeral: true).ConfigureAwait(false);
        }
    }

    public async Task<Embed> HandleUserAdd(string desiredUid, ulong discordUserId)
    {
        var embed = new EmbedBuilder();

        using var scope = _services.CreateScope();
        using var db = scope.ServiceProvider.GetService<MareDbContext>();
        if (!(await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == discordUserId))?.User?.IsAdmin ?? true)
        {
            embed.WithTitle("添加用户失败");
            embed.WithDescription("权限不足");
        }
        else if (db.Users.Any(u => u.UID == desiredUid || u.Alias == desiredUid))
        {
            embed.WithTitle("添加用户失败");
            embed.WithDescription("用户已存在");
        }
        else
        {
            User newUser = new()
            {
                IsAdmin = false,
                IsModerator = false,
                LastLoggedIn = DateTime.UtcNow,
                UID = desiredUid,
            };

            var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
            var auth = new Auth()
            {
                HashedKey = StringUtils.Sha256String(computedHash),
                User = newUser,
            };

            await db.Users.AddAsync(newUser);
            await db.Auth.AddAsync(auth);

            await db.SaveChangesAsync();

            embed.WithTitle("已成功添加 " + desiredUid);
            embed.WithDescription("密钥: " + computedHash);
        }

        return embed.Build();
    }

    private async Task<EmbedBuilder> HandleUserInfo(EmbedBuilder eb, ulong id, string? secondaryUserUid = null, ulong? optionalUser = null, string? uid = null, string? lodestoneId = null)
    {
        bool showForSecondaryUser = secondaryUserUid != null;
        using var scope = _services.CreateScope();
        await using var db = scope.ServiceProvider.GetRequiredService<MareDbContext>();

        var primaryUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == id).ConfigureAwait(false);

        ulong userToCheckForDiscordId = id;

        if (primaryUser == null)
        {
            eb.WithTitle("账号不存在");
            eb.WithDescription("没有与本Discord账号关联的Mare账号");
            return eb;
        }

        bool isAdminCall = primaryUser.User.IsModerator || primaryUser.User.IsAdmin;

        if ((optionalUser != null || uid != null || lodestoneId != null) && !isAdminCall)
        {
            eb.WithTitle("权限不足");
            eb.WithDescription("只有管理员可以查看其它用户的信息");
            return eb;
        }
        else if ((optionalUser != null || uid != null || lodestoneId != null) && isAdminCall)
        {
            LodeStoneAuth userInDb = null;
            if (optionalUser != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == optionalUser).ConfigureAwait(false);
            }
            else if (uid != null)
            {
                var primary = (await db.Auth.SingleOrDefaultAsync(u => u.UserUID == uid && u.PrimaryUserUID != null))?.PrimaryUserUID ?? uid;//确认是否为子账号，如果是，查找主账号DC信息
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == primary || u.User.Alias == primary).ConfigureAwait(false);
            }
            else if (lodestoneId != null)
            {
                userInDb = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.HashedLodestoneId == StringUtils.Sha256String(lodestoneId)).ConfigureAwait(false);
            }

            if (userInDb == null)
            {
                eb.WithTitle("账号不存在");
                eb.WithDescription("未找到要查询的账号");
                return eb;
            }

            userToCheckForDiscordId = userInDb.DiscordId;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.DiscordId == userToCheckForDiscordId).ConfigureAwait(false);
        var dbUser = lodestoneUser.User;
        if (showForSecondaryUser)
        {
            dbUser = (await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.PrimaryUserUID == dbUser.UID && u.UserUID == secondaryUserUid))?.User;
            if (dbUser == null)
            {
                eb.WithTitle("辅助UID不存在");
                eb.WithDescription($"你的主UID {primaryUser.User.UID} 下不存在辅助UID {secondaryUserUid}.");
                return eb;
            }
        }

        var secondaryCheck = isAdminCall && uid != null && uid != dbUser?.UID;//查找的UID为子账号
        if (secondaryCheck)
        {
            dbUser = (await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid))?.User;//显示子账号信息
        }
        
        var auth = await db.Auth.Include(u => u.PrimaryUser).SingleOrDefaultAsync(u => u.UserUID == dbUser.UID).ConfigureAwait(false);
        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var identity = await _connectionMultiplexer.GetDatabase().StringGetAsync("UID:" + dbUser.UID).ConfigureAwait(false);

        eb.WithTitle("用户信息");
        eb.WithDescription("这是 Discord 用户 <@" + userToCheckForDiscordId + "> 的信息" + Environment.NewLine);
        eb.AddField("UID", dbUser.UID);
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("个性 UID", dbUser.Alias);
        }
        if (showForSecondaryUser || secondaryCheck)
        {
            eb.AddField(dbUser.UID + " 的主UID:", auth.PrimaryUserUID);
        }
        else
        {
            var secondaryUIDs = await db.Auth.Where(p => p.PrimaryUserUID == dbUser.UID).Select(p => p.UserUID).ToListAsync();
            if (secondaryUIDs.Any())
            {
                eb.AddField("辅助UID:", string.Join(Environment.NewLine, secondaryUIDs));
            }
        }
        eb.AddField("上一次在线(本地时间)", $"<t:{new DateTimeOffset(dbUser.LastLoggedIn.ToUniversalTime()).ToUnixTimeSeconds()}:f>");
        eb.AddField("目前是否在线", !string.IsNullOrEmpty(identity));
        eb.AddField("同步密钥哈希值", auth.HashedKey);
        eb.AddField("加入的同步贝数量", groupsJoined.Count);
        eb.AddField("拥有的同步贝数量", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(group.Alias))
            {
                eb.AddField("拥有的同步贝 " + group.GID + " ,个性 GID", group.Alias);
            }
            eb.AddField("拥有的同步贝 " + group.GID + " ,用户数量", syncShellUserCount);
        }

        if (isAdminCall && !string.IsNullOrEmpty(identity))
        {
            eb.AddField("角色识别码", identity);
        }
        
        if (primaryUser.User.IsAdmin && !string.IsNullOrEmpty(lodestoneUser.HashedLodestoneId))
        {
            eb.AddField("LodeStone识别码", lodestoneUser.HashedLodestoneId);
        }

        return eb;
    }

    [SlashCommand("banuser", "封禁用户")]
    [RequireUserPermission(GuildPermission.Administrator)]
    public async Task BanUser([Summary("uid", "用户uid")] string uid,
    [Summary("reason", "封禁原因")] string? reason = null)
    {

        using var scope = _services.CreateScope();
        using var dbContext = scope.ServiceProvider.GetService<MareDbContext>();

        if (!(await dbContext.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(a => a.DiscordId == Context.Interaction.User.Id))?.User?.IsAdmin ?? true)
        {
            await RespondAsync("权限不足", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var user = await dbContext.Auth.SingleAsync(u => u.UserUID == uid).ConfigureAwait(false);
        if (user.MarkForBan)
        {
            await RespondAsync("错误，用户已被封禁", ephemeral:true).ConfigureAwait(false);
            return;
        }
        user.MarkForBan = true;
        var lodeStoneAuth = await dbContext.LodeStoneAuth.SingleAsync(u => u.User.UID == user.UserUID || u.User.UID == user.PrimaryUserUID).ConfigureAwait(false);
        dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
        {
            DiscordIdOrLodestoneAuth = lodeStoneAuth.HashedLodestoneId
        });
        dbContext.BannedRegistrations.Add(new MareSynchronosShared.Models.BannedRegistrations()
        {
            DiscordIdOrLodestoneAuth = lodeStoneAuth.DiscordId.ToString()
        });
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        var text = $"已将用户 `{uid}` 添加到封禁列表";
        if (!string.IsNullOrEmpty(reason)) text += $", 封禁原因: `{reason}`";
        await RespondAsync(text, ephemeral:false).ConfigureAwait(false);
        _logger.LogInformation($"Admin {Context.Interaction.User.Username} banned {uid}");

    }
}