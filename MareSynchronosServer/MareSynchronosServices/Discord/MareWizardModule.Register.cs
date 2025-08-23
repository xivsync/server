using Discord.Interactions;
using Discord;
using MareSynchronosShared.Data;
using Microsoft.EntityFrameworkCore;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Models;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule
{
    [ComponentInteraction("wizard-register")]
    public async Task ComponentRegister()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRegister), Context.Interaction.User.Id);

        EmbedBuilder eb = new();
        eb.WithColor(Color.Blue);
        eb.WithTitle("Start Registration");
        eb.WithDescription(
            "Here you can start registering to use this server's Mare Synchronos service." + Environment.NewLine + Environment.NewLine
            + "- Have your Lodestone UID ready (e.g., 10000000)" + Environment.NewLine
            + "  - Registration requires you to edit your Lodestone profile with a generated verification code" + Environment.NewLine
            + "- Do not use a mobile device for this step because you’ll need to copy the generated key" + Environment.NewLine
            + "# Read the registration steps carefully. Take it slooow."
        );
        ComponentBuilder cb = new();
        AddHome(cb);
        cb.WithButton("Start Registration", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🌒"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-start")]
    public async Task ComponentRegisterStart()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentRegisterStart), Context.Interaction.User.Id);

        using var db = await GetDbContext().ConfigureAwait(false);
        var entry = await db.LodeStoneAuth.SingleOrDefaultAsync(u => u.DiscordId == Context.User.Id && u.StartedAt != null).ConfigureAwait(false);
        if (entry != null)
        {
            db.LodeStoneAuth.Remove(entry);
        }
        _botServices.DiscordLodestoneMapping.TryRemove(Context.User.Id, out _);
        _botServices.DiscordVerifiedUsers.TryRemove(Context.User.Id, out _);

        await db.SaveChangesAsync().ConfigureAwait(false);

        await RespondWithModalAsync<LodestoneModal>("wizard-register-lodestone-modal").ConfigureAwait(false);
    }

    [ModalInteraction("wizard-register-lodestone-modal")]
    public async Task ModalRegister(LodestoneModal lodestoneModal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{lodestone}", nameof(ModalRegister), Context.Interaction.User.Id, lodestoneModal.LodestoneUrl);

        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        var success = await HandleRegisterModalAsync(eb, lodestoneModal).ConfigureAwait(false);
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        if (success.Item1) cb.WithButton("Verify", "wizard-register-verify:" + success.Item2, ButtonStyle.Primary, emote: new Emoji("✅"));
        else cb.WithButton("Retry", "wizard-register-start", ButtonStyle.Primary, emote: new Emoji("🔁"));
        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify:*")]
    public async Task ComponentRegisterVerify(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{verificationcode}", nameof(ComponentRegisterVerify), Context.Interaction.User.Id, verificationCode);

        _botServices.VerificationQueue.Enqueue(new KeyValuePair<ulong, Func<DiscordBotServices, Task>>(Context.User.Id,
            (service) => HandleVerifyAsync(Context.User.Id, verificationCode, service)));
        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        eb.WithColor(Color.Purple);
        cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Check", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
        eb.WithTitle("Pending Verification");
        eb.WithDescription(
            "Please wait while the bot verifies your registration." + Environment.NewLine
            + "Press **Check** to see if verification has completed." + Environment.NewLine + Environment.NewLine
            + "__This will not proceed automatically; you must press **Check**.__"
        );
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-register-verify-check:*")]
    public async Task ComponentRegisterVerifyCheck(string verificationCode)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ComponentRegisterVerifyCheck), Context.Interaction.User.Id, verificationCode);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        bool stillEnqueued = _botServices.VerificationQueue.Any(k => k.Key == Context.User.Id);
        bool verificationRan = _botServices.DiscordVerifiedUsers.TryGetValue(Context.User.Id, out bool verified);
        bool registerSuccess = false;
        if (!verificationRan)
        {
            if (stillEnqueued)
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("Your verification is still pending");
                eb.WithDescription("Please try again and click **Check** after a few seconds.");
                cb.WithButton("Cancel", "wizard-register", ButtonStyle.Secondary, emote: new Emoji("❌"));
                cb.WithButton("Check", "wizard-register-verify-check:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("❓"));
            }
            else
            {
                eb.WithColor(Color.Red);
                eb.WithTitle("An error occurred");
                eb.WithDescription("Your verification was processed but didn’t complete correctly. Please start registration from the beginning.");
                cb.WithButton("Retry", "wizard-register", ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }
        else
        {
            if (verified)
            {
                eb.WithColor(Color.Green);
                using var db = await GetDbContext().ConfigureAwait(false);
                var (uid, key) = await HandleAddUser(db).ConfigureAwait(false);
                eb.WithTitle($"Registration successful. Your UID: {uid}");
                eb.WithDescription(
                    "This is your private key. Do not share it with anyone. **If you lose it, it’s gone forever.**"
                    + Environment.NewLine + Environment.NewLine
                    + "**__Note: This server currently does not support key login. Please use OAuth to connect.__**"
                    + Environment.NewLine + Environment.NewLine
                    + $"||**`{key}`**||"
                    + Environment.NewLine + Environment.NewLine
                    + "You don’t need to save this key."
                    + Environment.NewLine
                    + "__Note: The key only contains letters A–F and digits 0–9.__"
                    + Environment.NewLine
                    + "You should connect as soon as possible to avoid automatic cleanup."
                    + Environment.NewLine
                    + "Have fun."
                );
                AddHome(cb);
                registerSuccess = true;
            }
            else
            {
                eb.WithColor(Color.Gold);
                eb.WithTitle("Registration verification failed");
                eb.WithDescription(
                    "The bot couldn’t find the required verification code in your Lodestone profile."
                    + Environment.NewLine + Environment.NewLine
                    + "Please restart the verification process and make sure to submit your profile _twice_ so it’s saved correctly."
                    + Environment.NewLine + Environment.NewLine
                    + "**Make sure your profile is public, otherwise the bot cannot read it.**"
                    + Environment.NewLine + Environment.NewLine
                    + "## You __must__ put the following text so the bot can detect it:"
                    + Environment.NewLine + Environment.NewLine
                    + "**`" + verificationCode + "`**"
                );
                cb.WithButton("Cancel", "wizard-register", emote: new Emoji("❌"));
                cb.WithButton("Retry", "wizard-register-verify:" + verificationCode, ButtonStyle.Primary, emote: new Emoji("🔁"));
            }
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
        if (registerSuccess)
            await _botServices.AddRegisteredRoleAsync(Context.Interaction.User).ConfigureAwait(false);
    }

    private async Task<(bool, string)> HandleRegisterModalAsync(EmbedBuilder embed, LodestoneModal arg)
    {
        var lodestoneId = ParseCharacterIdFromLodestoneUrl(arg.LodestoneUrl);
        if (lodestoneId == null)
        {
            embed.WithTitle("Invalid UID");
            embed.WithDescription("The Lodestone UID format is incorrect; it should look like:" + Environment.NewLine
                + "10000000");
            return (false, string.Empty);
        }

        // check if userid is already in db
        var hashedLodestoneId = StringUtils.Sha256String(lodestoneId.ToString());

        using var db = await GetDbContext().ConfigureAwait(false);

        // check if discord id or lodestone id is banned
        if (db.BannedRegistrations.Any(a => a.DiscordIdOrLodestoneAuth == hashedLodestoneId))
        {
            embed.WithDescription("Account is banned");
            return (false, string.Empty);
        }

        if (db.LodeStoneAuth.Any(a => a.HashedLodestoneId == hashedLodestoneId))
        {
            // character already in db
            embed.WithDescription("That Lodestone character already exists in the database. If you want to attach it to your current Discord account, use Relink.");
            return (false, string.Empty);
        }

        string lodestoneAuth = await GenerateLodestoneAuth(Context.User.Id, hashedLodestoneId, db).ConfigureAwait(false);
        // instructions
        embed.WithTitle("Verify your character");
        embed.WithDescription(
              "Add the following key to your character profile: https://ff14risingstones.web.sdo.com/pc/index.html#/me/settings/main"
              + Environment.NewLine
              + "__Note: If that link doesn’t open your profile, adjust your privacy settings!__"
              + Environment.NewLine + Environment.NewLine
              + $"**`{lodestoneAuth}`**"
              + Environment.NewLine + Environment.NewLine
              + "**This is NOT the key you enter in the Mare Synchronos plugin!**"
              + Environment.NewLine + Environment.NewLine
              + "After adding and saving, use the button below to verify and complete registration to receive your Mare Synchronos key."
              + Environment.NewLine
              + "__After verification, you can remove this entry from your profile.__"
              + Environment.NewLine + Environment.NewLine
              + "Verification expires in about 15 minutes. If verification fails, the registration is void and must be started again."
        );
        _botServices.DiscordLodestoneMapping[Context.User.Id] = lodestoneId.ToString();

        return (true, lodestoneAuth);
    }

    private async Task HandleVerifyAsync(ulong userid, string authString, DiscordBotServices services)
    {
        var req = new HttpClient();
        var cookie = GetSZJCookie();
        if (!string.IsNullOrEmpty(cookie))
        {
            // req.DefaultRequestHeaders.Add("Cookie", cookie);
            req.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _botServices.Logger.LogInformation("Set bot cookie to {botCookie}", cookie);
        }
        else
        {
            _botServices.Logger.LogError("Cannot get cookie for bot service");
        }

        services.DiscordVerifiedUsers.Remove(userid, out _);
        if (services.DiscordLodestoneMapping.ContainsKey(userid))
        {
            // var randomServer = services.LodestoneServers[random.Next(services.LodestoneServers.Length)];
            var url = $"https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={services.DiscordLodestoneMapping[userid]}&part_id=&orderBy=time&page=1&limit=20";
            var response = await req.GetAsync(url).ConfigureAwait(false);
            _logger.LogInformation("Verifying {userid} with URL {url}", userid, url);
            services.DiscordVerifiedUsers[userid] = true;
            _logger.LogInformation("Verified {userid} from lodestone {lodestone}", userid, services.DiscordLodestoneMapping[userid]);
            await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Success.").ConfigureAwait(false);
            services.DiscordLodestoneMapping.TryRemove(userid, out _);
            // if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            // {
            //     var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            //     if (content.Contains(authString))
            //     {
            //         services.DiscordVerifiedUsers[userid] = true;
            //         _logger.LogInformation("Verified {userid} from lodestone {lodestone}", userid, services.DiscordLodestoneMapping[userid]);
            //         await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Success.").ConfigureAwait(false);
            //         services.DiscordLodestoneMapping.TryRemove(userid, out _);
            //     }
            //     else
            //     {
            //         services.DiscordVerifiedUsers[userid] = false;
            //         _logger.LogInformation("Could not verify {userid} from lodestone {lodestone}, did not find authString: {authString}, status code was: {code}",
            //             userid, services.DiscordLodestoneMapping[userid], authString, response.StatusCode);
            //         await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Failed: No Authstring ({authString}). (<{url}>)").ConfigureAwait(false);
            //     }
            // }
            // else
            // {
            //     _logger.LogWarning("Could not verify {userid}, HttpStatusCode: {code}", userid, response.StatusCode);
            //     await _botServices.LogToChannel($"<@{userid}> REGISTER VERIFY: Failed: HttpStatusCode {response.StatusCode}. (<{url}>)").ConfigureAwait(false);
            // }
        }
    }

    private async Task<(string, string)> HandleAddUser(MareDbContext db)
    {
        var lodestoneAuth = db.LodeStoneAuth.SingleOrDefault(u => u.DiscordId == Context.User.Id);

        var user = new User();

        var hasValidUid = false;
        while (!hasValidUid)
        {
            var uid = StringUtils.GenerateRandomString(10);
            if (db.Users.Any(u => u.UID == uid || u.Alias == uid)) continue;
            user.UID = uid;
            hasValidUid = true;
        }

        // make the first registered user on the service to admin
        if (!await db.Users.AnyAsync().ConfigureAwait(false))
        {
            user.IsAdmin = true;
        }

        user.LastLoggedIn = DateTime.UtcNow;

        var computedHash = StringUtils.Sha256String(StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString());
        string hashedKey = StringUtils.Sha256String(computedHash);
        var auth = new Auth()
        {
            HashedKey = hashedKey,
            User = user,
        };

        await db.Users.AddAsync(user).ConfigureAwait(false);
        await db.Auth.AddAsync(auth).ConfigureAwait(false);

        lodestoneAuth.StartedAt = null;
        lodestoneAuth.User = user;
        lodestoneAuth.LodestoneAuthString = null;

        await db.SaveChangesAsync().ConfigureAwait(false);

        _botServices.Logger.LogInformation("User registered: {userUID}:{hashedKey}", user.UID, hashedKey);

        await _botServices.LogToChannel($"{Context.User.Mention} REGISTER COMPLETE: => {user.UID}").ConfigureAwait(false);

        _botServices.DiscordVerifiedUsers.Remove(Context.User.Id, out _);

        return (user.UID, computedHash);
    }
}
