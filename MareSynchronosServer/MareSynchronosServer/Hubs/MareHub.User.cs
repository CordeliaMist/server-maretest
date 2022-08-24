﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MareSynchronos.API;
using MareSynchronosShared.Authentication;
using MareSynchronosShared.Metrics;
using MareSynchronosShared.Models;
using MareSynchronosShared.Protos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs
{
    public partial class MareHub
    {
        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserDeleteAccount)]
        public async Task DeleteAccount()
        {
            _logger.LogInformation("User {AuthenticatedUserId} deleted their account", AuthenticatedUserId);


            string userid = AuthenticatedUserId;
            var userEntry = await _dbContext.Users.SingleAsync(u => u.UID == userid).ConfigureAwait(false);
            var ownPairData = await _dbContext.ClientPairs.Where(u => u.User.UID == userid).ToListAsync().ConfigureAwait(false);
            var auth = await _dbContext.Auth.SingleAsync(u => u.UserUID == userid).ConfigureAwait(false);
            var lodestone = await _dbContext.LodeStoneAuth.SingleOrDefaultAsync(a => a.User.UID == userid).ConfigureAwait(false);

            if (lodestone != null)
            {
                _dbContext.Remove(lodestone);
            }

            while (_dbContext.Files.Any(f => f.Uploader == userEntry))
            {
                await Task.Delay(1000).ConfigureAwait(false);
            }

            await _authServiceClient.RemoveAuthAsync(new RemoveAuthRequest() { Uid = userid }).ConfigureAwait(false);


            _dbContext.RemoveRange(ownPairData);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherPairData = await _dbContext.ClientPairs.Include(u => u.User)
                .Where(u => u.OtherUser.UID == userid).ToListAsync().ConfigureAwait(false);
            foreach (var pair in otherPairData)
            {
                await Clients.User(pair.User.UID)
                    .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = userid,
                        IsRemoved = true
                    }, userEntry.CharacterIdentification).ConfigureAwait(false);
            }

            await _metricsClient.DecGaugeAsync(new GaugeRequest()
            { GaugeName = MetricsAPI.GaugePairs, Value = ownPairData.Count + otherPairData.Count }).ConfigureAwait(false);
            await _metricsClient.DecGaugeAsync(new GaugeRequest()
            { GaugeName = MetricsAPI.GaugePairsPaused, Value = ownPairData.Count(c => c.IsPaused) }).ConfigureAwait(false);
            await _metricsClient.DecGaugeAsync(new GaugeRequest()
            { GaugeName = MetricsAPI.GaugeUsersRegistered, Value = 1 }).ConfigureAwait(false);

            _dbContext.RemoveRange(otherPairData);
            _dbContext.Remove(userEntry);
            _dbContext.Remove(auth);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetOnlineCharacters)]
        public async Task<List<string>> GetOnlineCharacters()
        {
            _logger.LogInformation("User {AuthenticatedUserId} requested online characters", AuthenticatedUserId);

            var ownUser = await GetAuthenticatedUserUntrackedAsync().ConfigureAwait(false);

            var otherUsers = await _dbContext.ClientPairs.AsNoTracking()
                .Include(u => u.User)
                .Include(u => u.OtherUser)
                .Where(w => w.User.UID == ownUser.UID && !w.IsPaused)
                .Where(w => !string.IsNullOrEmpty(w.OtherUser.CharacterIdentification))
                .Select(e => e.OtherUser).ToListAsync().ConfigureAwait(false);
            var otherEntries = await _dbContext.ClientPairs.AsNoTracking()
                .Include(u => u.User)
                .Where(u => otherUsers.Any(e => e == u.User) && u.OtherUser == ownUser && !u.IsPaused).ToListAsync().ConfigureAwait(false);

            await Clients.Users(otherEntries.Select(e => e.User.UID)).SendAsync(Api.OnUserAddOnlinePairedPlayer, ownUser.CharacterIdentification).ConfigureAwait(false);
            return otherEntries.Select(e => e.User.CharacterIdentification).Distinct().ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserGetPairedClients)]
        public async Task<List<ClientPairDto>> GetPairedClients()
        {
            string userid = AuthenticatedUserId;
            var query =
                from userToOther in _dbContext.ClientPairs
                join otherToUser in _dbContext.ClientPairs
                    on new
                    {
                        user = userToOther.UserUID,
                        other = userToOther.OtherUserUID

                    } equals new
                    {
                        user = otherToUser.OtherUserUID,
                        other = otherToUser.UserUID
                    } into leftJoin
                from otherEntry in leftJoin.DefaultIfEmpty()
                join alias in _dbContext.Aliases
                    on new
                    {
                        uid = userToOther.UserUID
                    } equals new
                    {
                        uid = alias.UserUID
                    } into aliasLeftJoin
                from aliasEntry in aliasLeftJoin.DefaultIfEmpty()
                where
                    userToOther.UserUID == userid
                select new
                {
                    Alias = aliasEntry == null ? string.Empty : aliasEntry.AliasUID,
                    userToOther.IsPaused,
                    OtherIsPaused = otherEntry != null && otherEntry.IsPaused,
                    userToOther.OtherUserUID,
                    IsSynced = otherEntry != null
                };

            return (await query.ToListAsync().ConfigureAwait(false)).Select(f => new ClientPairDto()
            {
                VanityUID = f.Alias,
                IsPaused = f.IsPaused,
                OtherUID = f.OtherUserUID,
                IsSynced = f.IsSynced,
                IsPausedFromOthers = f.OtherIsPaused
            }).ToList();
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.InvokeUserPushCharacterDataToVisibleClients)]
        public async Task PushCharacterDataToVisibleClients(CharacterCacheDto characterCache, List<string> visibleCharacterIds)
        {
            _logger.LogInformation("User {AuthenticatedUserId} pushing character data to {visibleCharacterIds} visible clients", AuthenticatedUserId, visibleCharacterIds.Count);

            var user = await GetAuthenticatedUserUntrackedAsync().ConfigureAwait(false);

            var query =
                from userToOther in _dbContext.ClientPairs
                join otherToUser in _dbContext.ClientPairs
                    on new
                    {
                        user = userToOther.UserUID,
                        other = userToOther.OtherUserUID

                    } equals new
                    {
                        user = otherToUser.OtherUserUID,
                        other = otherToUser.UserUID
                    }
                where
                    userToOther.UserUID == user.UID
                    && !userToOther.IsPaused
                    && !otherToUser.IsPaused
                    && visibleCharacterIds.Contains(userToOther.OtherUser.CharacterIdentification)
                select otherToUser.UserUID;

            var otherEntries = await query.ToListAsync().ConfigureAwait(false);

            await Clients.Users(otherEntries).SendAsync(Api.OnUserReceiveCharacterData, characterCache, user.CharacterIdentification).ConfigureAwait(false);

            await _metricsClient.IncreaseCounterAsync(new IncreaseCounterRequest()
            { CounterName = MetricsAPI.CounterUserPushData, Value = 1 }).ConfigureAwait(false);
            await _metricsClient.IncreaseCounterAsync(new IncreaseCounterRequest()
            { CounterName = MetricsAPI.CounterUserPushDataTo, Value = otherEntries.Count }).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientAddition)]
        public async Task SendPairedClientAddition(string uid)
        {
            string otherUserUid = uid;
            if (uid == AuthenticatedUserId) return;
            uid = uid.Trim();
            var user = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);

            var potentialAlias = _dbContext.Aliases.SingleOrDefault(u => u.AliasUID == uid);
            if (potentialAlias != null)
            {
                otherUserUid = potentialAlias.UserUID;
            }

            var otherUser = await _dbContext.Users
                .SingleOrDefaultAsync(u => u.UID == otherUserUid).ConfigureAwait(false);
            var existingEntry =
                await _dbContext.ClientPairs.AsNoTracking()
                    .FirstOrDefaultAsync(p =>
                        p.User.UID == AuthenticatedUserId && p.OtherUser.UID == otherUserUid).ConfigureAwait(false);
            if (otherUser == null || existingEntry != null) return;
            _logger.LogInformation("User {AuthenticatedUserId} adding {uid} to whitelist", AuthenticatedUserId, otherUserUid);
            ClientPair wl = new ClientPair()
            {
                IsPaused = false,
                OtherUser = otherUser,
                User = user
            };
            await _dbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherEntry = OppositeEntry(otherUserUid);
            await Clients.User(user.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsPaused = false,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, string.Empty).ConfigureAwait(false);
            if (otherEntry != null)
            {
                await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs,
                    new ClientPairDto()
                    {
                        OtherUID = user.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = true
                    }, user.CharacterIdentification).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(user.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, otherUser.CharacterIdentification).ConfigureAwait(false);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserAddOnlinePairedPlayer, user.CharacterIdentification).ConfigureAwait(false);
                }
            }

            await _metricsClient.IncGaugeAsync(new GaugeRequest() { GaugeName = MetricsAPI.GaugePairs, Value = 1 }).ConfigureAwait(false);
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientPauseChange)]
        public async Task SendPairedClientPauseChange(string otherUserUid, bool isPaused)
        {
            if (otherUserUid == AuthenticatedUserId) return;
            ClientPair pair = await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == AuthenticatedUserId && w.OtherUserUID == otherUserUid).ConfigureAwait(false);
            if (pair == null) return;

            _logger.LogInformation("User {AuthenticatedUserId} changed pause status with {otherUserUid} to {isPaused}", AuthenticatedUserId, otherUserUid, isPaused);
            pair.IsPaused = isPaused;
            _dbContext.Update(pair);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var selfCharaIdent = (await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false)).CharacterIdentification;
            var otherCharaIdent = (await _dbContext.Users.SingleAsync(u => u.UID == otherUserUid).ConfigureAwait(false)).CharacterIdentification;
            var otherEntry = OppositeEntry(otherUserUid);

            await Clients.User(AuthenticatedUserId)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUserUid,
                    IsPaused = isPaused,
                    IsPausedFromOthers = otherEntry?.IsPaused ?? false,
                    IsSynced = otherEntry != null
                }, otherCharaIdent).ConfigureAwait(false);
            if (otherEntry != null)
            {
                await Clients.User(otherUserUid).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = AuthenticatedUserId,
                    IsPaused = otherEntry.IsPaused,
                    IsPausedFromOthers = isPaused,
                    IsSynced = true
                }, selfCharaIdent).ConfigureAwait(false);
            }

            if (isPaused)
            {
                await _metricsClient.IncGaugeAsync(new GaugeRequest() { GaugeName = MetricsAPI.GaugePairsPaused, Value = 1 }).ConfigureAwait(false);
            }
            else
            {
                await _metricsClient.DecGaugeAsync(new GaugeRequest() { GaugeName = MetricsAPI.GaugePairsPaused, Value = 1 }).ConfigureAwait(false);
            }
        }

        [Authorize(AuthenticationSchemes = SecretKeyGrpcAuthenticationHandler.AuthScheme)]
        [HubMethodName(Api.SendUserPairedClientRemoval)]
        public async Task SendPairedClientRemoval(string uid)
        {
            if (uid == AuthenticatedUserId) return;

            var sender = await _dbContext.Users.SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);
            var otherUser = await _dbContext.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);
            if (otherUser == null) return;
            _logger.LogInformation("User {AuthenticatedUserId} removed {uid} from whitelist", AuthenticatedUserId, uid);
            ClientPair wl =
                await _dbContext.ClientPairs.SingleOrDefaultAsync(w => w.User == sender && w.OtherUser == otherUser).ConfigureAwait(false);
            if (wl == null) return;
            _dbContext.ClientPairs.Remove(wl);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            var otherEntry = OppositeEntry(uid);
            await Clients.User(sender.UID)
                .SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                {
                    OtherUID = otherUser.UID,
                    IsRemoved = true
                }, otherUser.CharacterIdentification).ConfigureAwait(false);
            if (otherEntry != null)
            {
                if (!string.IsNullOrEmpty(otherUser.CharacterIdentification))
                {
                    await Clients.User(sender.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, otherUser.CharacterIdentification).ConfigureAwait(false);
                    await Clients.User(otherUser.UID)
                        .SendAsync(Api.OnUserRemoveOnlinePairedPlayer, sender.CharacterIdentification).ConfigureAwait(false);
                    await Clients.User(otherUser.UID).SendAsync(Api.OnUserUpdateClientPairs, new ClientPairDto()
                    {
                        OtherUID = sender.UID,
                        IsPaused = otherEntry.IsPaused,
                        IsPausedFromOthers = false,
                        IsSynced = false
                    }, sender.CharacterIdentification).ConfigureAwait(false);
                }
            }

            await _metricsClient.DecGaugeAsync(new GaugeRequest() { GaugeName = MetricsAPI.GaugePairs, Value = 1 }).ConfigureAwait(false);
        }

        private ClientPair OppositeEntry(string otherUID) =>
                                    _dbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == AuthenticatedUserId);
    }
}
