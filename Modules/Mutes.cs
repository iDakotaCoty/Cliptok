﻿using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cliptok.Modules
{

    public static class Mutes
    {

        public static TimeSpan RoundToNearest(this TimeSpan a, TimeSpan roundTo)
        {
            long ticks = (long)(Math.Round(a.Ticks / (double)roundTo.Ticks) * roundTo.Ticks);
            return new TimeSpan(ticks);
        }

        // Only to be used on naughty users.
        public static async Task<bool> MuteUserAsync(DiscordUser naughtyUser, string reason, ulong moderatorId, DiscordGuild guild, DiscordChannel channel = null, TimeSpan muteDuration = default, bool alwaysRespond = false)
        {
            bool permaMute = false;
            DiscordChannel logChannel = await Program.discord.GetChannelAsync(Program.cfgjson.LogChannel);
            DiscordRole mutedRole = guild.GetRole(Program.cfgjson.MutedRole);
            DateTime? expireTime = DateTime.Now + muteDuration;
            DiscordMember moderator = await guild.GetMemberAsync(moderatorId);

            DiscordMember naughtyMember = default;
            try
            {
                naughtyMember = await guild.GetMemberAsync(naughtyUser.Id);
            }
            catch (DSharpPlus.Exceptions.NotFoundException)
            {
                // nothing
            }


            if (muteDuration == default)
            {
                permaMute = true;
                expireTime = null;
            }

            MemberPunishment newMute = new()
            {
                MemberId = naughtyUser.Id,
                ModId = moderatorId,
                ServerId = guild.Id,
                ExpireTime = expireTime
            };

            await Program.db.HashSetAsync("mutes", naughtyUser.Id, JsonConvert.SerializeObject(newMute));

            if (naughtyMember != default)
            {
                try
                {
                    await naughtyMember.GrantRoleAsync(mutedRole, $"[Mute by {moderator.Username}#{moderator.Discriminator}]: {reason}");
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                if (permaMute)
                {
                    await logChannel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} was successfully muted by `{moderator.Username}#{moderator.Discriminator}` (`{moderatorId}`).\nReason: **{reason}**");
                    if (naughtyMember != default)
                        await naughtyMember.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} You have been muted in **{guild.Name}**!\nReason: **{reason}**");
                }

                else
                {
                    await logChannel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} was successfully muted for {Warnings.TimeToPrettyFormat(muteDuration, false)} by `{moderator.Username}#{moderator.Discriminator}` (`{moderatorId}`).\nReason: **{reason}**");
                    if (naughtyMember != default)
                        await naughtyMember.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} You have been muted in **{guild.Name}** for {Warnings.TimeToPrettyFormat(muteDuration, false)}!\nReason: **{reason}**");
                }
            }
            catch
            {
                // A DM failing to send isn't important, but let's put it in chat just so it's somewhere.
                if (!(channel is null))
                {
                    if (muteDuration == default)
                        await channel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} has been muted: **{reason}**");
                    else
                        await channel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} has been muted for **{Warnings.TimeToPrettyFormat(muteDuration, false)}**: **{reason}**");
                    return true;
                }
            }

            if (!(channel is null) && alwaysRespond)
            {
                reason = reason.Replace("`", "\\`").Replace("*", "\\*");
                if (muteDuration == default)
                    await channel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} has been muted: **{reason}**");
                else
                    await channel.SendMessageAsync($"{Program.cfgjson.Emoji.Muted} {naughtyUser.Mention} has been muted for **{Warnings.TimeToPrettyFormat(muteDuration, false)}**: **{reason}**");
            }
            return true;
        }

        public static async Task<bool> UnmuteUserAsync(DiscordUser targetUser)
        {
            bool success = false;
            DiscordGuild guild = await Program.discord.GetGuildAsync(Program.cfgjson.ServerID);
            DiscordChannel logChannel = await Program.discord.GetChannelAsync(Program.cfgjson.LogChannel);

            // todo: store per-guild
            DiscordRole mutedRole = guild.GetRole(Program.cfgjson.MutedRole);
            DiscordMember member = default;
            try
            {
                member = await guild.GetMemberAsync(targetUser.Id);
            }
            catch (DSharpPlus.Exceptions.NotFoundException)
            {
                // they probably left :(
            }

            if (member == default)
            {
                await logChannel.SendMessageAsync($"{Program.cfgjson.Emoji.Information} Attempt to remove Muted role from <@{targetUser.Id}> failed because the user could not be found.\nThis is expected if the user was banned or left.");
            }
            else
            {
                // Perhaps we could be catching something specific, but this should do for now.
                try
                {
                    await member.RevokeRoleAsync(mutedRole);
                    foreach (var role in member.Roles)
                    {
                        if (role.Name == "Muted")
                        {
                            try
                            {
                                await member.RevokeRoleAsync(role);
                            }
                            catch
                            {
                                // ignore, continue to next role
                            }
                        }
                    }
                    success = true;
                }
                catch
                {
                    await logChannel.SendMessageAsync($"{Program.cfgjson.Emoji.Error} Attempt to removed Muted role from <@{targetUser.Id}> failed because of a Discord API error!" +
                    $"\nIf the role was removed manually, this error can be disregarded safely.");
                }
                if (success)
                    await logChannel.SendMessageAsync($"{Program.cfgjson.Emoji.Information} Successfully unmuted <@{targetUser.Id}>!");

            }
            // Even if the bot failed to remove the role, it reported that failure to a log channel and thus the mute
            //  can be safely removed internally.
            await Program.db.HashDeleteAsync("mutes", targetUser.Id);

            return true;
        }

        public static async Task<bool> CheckMutesAsync(bool includeRemutes = false)
        {
            DiscordChannel logChannel = await Program.discord.GetChannelAsync(Program.cfgjson.LogChannel);
            Dictionary<string, MemberPunishment> muteList = Program.db.HashGetAll("mutes").ToDictionary(
                x => x.Name.ToString(),
                x => JsonConvert.DeserializeObject<MemberPunishment>(x.Value)
            );
            if (muteList == null | muteList.Keys.Count == 0)
                return false;
            else
            {
                // The success value will be changed later if any of the unmutes are successful.
                bool success = false;
                foreach (KeyValuePair<string, MemberPunishment> entry in muteList)
                {
                    MemberPunishment mute = entry.Value;
                    if (DateTime.Now > mute.ExpireTime)
                    { 
                        await UnmuteUserAsync(await Program.discord.GetUserAsync(mute.MemberId));
                        success = true;
                    }
                    else if (includeRemutes)
                    {
                        try
                        {
                            var guild = await Program.discord.GetGuildAsync(mute.ServerId);
                            var member = await guild.GetMemberAsync(mute.MemberId);
                            if (member != null)
                            {
                                var muteRole = guild.GetRole(Program.cfgjson.MutedRole);
                                await member.GrantRoleAsync(muteRole);
                            }
                        }
                        catch
                        {
                            // nothing
                        }

                    }
                }
#if DEBUG
                Console.WriteLine($"Checked mutes at {DateTime.Now} with result: {success}");
#endif
                return success;
            }
        }
    }

    public class MuteCmds : BaseCommandModule
    {
        [Command("unmute")]
        [Description("Unmutes a previously muted user, typically ahead of the standard expiration time. See also: mute")]
        [HomeServer, RequireHomeserverPerm(ServerPermLevel.TrialMod)]
        public async Task UnmuteCmd(CommandContext ctx, [Description("The user you're trying to unmute.")] DiscordUser targetUser)
        {
            DiscordGuild guild = ctx.Guild;

            // todo: store per-guild
            DiscordRole mutedRole = guild.GetRole(Program.cfgjson.MutedRole);

            DiscordMember member = default;
            try
            {
                member = await guild.GetMemberAsync(targetUser.Id);
            }
            catch (DSharpPlus.Exceptions.NotFoundException)
            {
                // nothing
            }

            if ((await Program.db.HashExistsAsync("mutes", targetUser.Id)) || (member != default && member.Roles.Contains(mutedRole)))
            {
                await Mutes.UnmuteUserAsync(targetUser);
                await ctx.RespondAsync($"{Program.cfgjson.Emoji.Information} Successfully unmuted **{targetUser.Username}#{targetUser.Discriminator}**.");
            }
            else
                try
                {
                    await Mutes.UnmuteUserAsync(targetUser);
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Warning} According to Discord that user is not muted, but I tried to unmute them anyway. Hope it works.");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    await ctx.RespondAsync($"{Program.cfgjson.Emoji.Error} That user doesn't appear to be muted, *and* an error ocurred while attempting to unmute them anyway. Please contact the bot owner, the error has been logged.");
                }
        }

        [Command("mute")]
        [Description("Mutes a user, preventing them from sending messages until they're unmuted. See also: unmute")]
        [HomeServer, RequireHomeserverPerm(ServerPermLevel.TrialMod)]
        public async Task MuteCmd(
            CommandContext ctx, [Description("The user you're trying to mute")] DiscordUser targetUser,
            [RemainingText, Description("Combined argument for the time and reason for the mute. For example '1h rule 7' or 'rule 10'")] string timeAndReason = "No reason specified."
        )
        {
            DiscordMember targetMember = default;
            try
            {
                targetMember = await ctx.Guild.GetMemberAsync(targetUser.Id);
            }
            catch (DSharpPlus.Exceptions.NotFoundException)
            {
                // nothing
            }

            if (targetMember != default && Warnings.GetPermLevel(ctx.Member) == ServerPermLevel.TrialMod && (Warnings.GetPermLevel(targetMember) >= ServerPermLevel.TrialMod || targetMember.IsBot))
            {
                await ctx.Channel.SendMessageAsync($"{Program.cfgjson.Emoji.Error} {ctx.User.Mention}, as a Trial Moderator you cannot perform moderation actions on other staff members or bots.");
                return;
            }

            await ctx.Message.DeleteAsync();
            bool timeParsed = false;

            TimeSpan muteDuration = default;
            string possibleTime = timeAndReason.Split(' ').First();
            string reason = timeAndReason;

            try
            {
                muteDuration = HumanDateParser.HumanDateParser.Parse(possibleTime).Subtract(ctx.Message.Timestamp.DateTime);
                timeParsed = true;
            }
            catch
            {
                // keep default
            }


            if (timeParsed)
            {
                int i = reason.IndexOf(" ") + 1;
                reason = reason[i..];
            }

            if (timeParsed && possibleTime == reason)
                reason = "No reason specified.";

            _ = Mutes.MuteUserAsync(targetUser, reason, ctx.User.Id, ctx.Guild, ctx.Channel, muteDuration, true);
        }
    }
}
