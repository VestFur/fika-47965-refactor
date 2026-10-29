using FikaServer.Models.Fika;
using FikaServer.Models.Fika.Config;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;
using System.Collections.Concurrent;

namespace FikaServer.Services.Cache;

[Injectable(InjectionType.Singleton)]
public class PlayerRelationsService(ProfileHelper profileHelper, FikaPaths fikaPaths, JsonUtil jsonUtil, ISptLogger<PlayerRelationsService> logger)
{
    private readonly string _playerRelationsFullPath = fikaPaths.DatabasePath;
    private ConcurrentDictionary<MongoId, FikaPlayerRelations> _playerRelations = [];

    public List<MongoId> Keys
    {
        get
        {
            return [.. _playerRelations.Keys];
        }
    }

    public List<FikaPlayerRelations> Values
    {
        get
        {
            return [.. _playerRelations.Values];
        }
    }

    public async Task OnPreLoad()
    {
        if (!Directory.Exists(_playerRelationsFullPath))
        {
            Directory.CreateDirectory(_playerRelationsFullPath);
        }

        var file = $"{_playerRelationsFullPath}/playerRelations.json";
        if (!File.Exists(file))
        {
            await SaveProfileRelationsAsync();
        }
        else
        {
            _playerRelations = await jsonUtil.DeserializeFromFileAsync<ConcurrentDictionary<MongoId, FikaPlayerRelations>>(file) 
                ?? throw new InvalidOperationException("Could not deserialize player relations!");
        }
    }

    public async Task OnPostLoad()
    {
        var profiles = profileHelper.GetProfiles();
        var shouldSave = false;

        // Deleted accounts must not leave behind relation records. In
        // particular, GetFriendsList can otherwise encounter a stale friend
        // and recreate its record while trying to prune the caller's list.
        foreach (var profileId in _playerRelations.Keys)
        {
            if (!profiles.ContainsKey(profileId) && _playerRelations.TryRemove(profileId, out _))
            {
                shouldSave = true;
            }
        }

        foreach (var profileId in profiles.Keys)
        {
            if (!_playerRelations.TryGetValue(profileId, out var value))
            {
                value = new FikaPlayerRelations();
                if (!_playerRelations.TryAdd(profileId, value))
                {
                    logger.Error($"Failed to add {profileId} to relations database");
                    continue;
                }
                shouldSave = true;

                continue;
            }

            var Friends = value.Friends;

            foreach (var friend in Friends.ToList())
            {
                if (!friend.TryParseMongoId(out var friendId) || !profiles.ContainsKey(friendId))
                {
                    Friends.Remove(friend);
                    shouldSave = true;
                }
            }

            var Ignored = value.Ignore;

            foreach (var ignore in Ignored.ToList())
            {
                if (!ignore.TryParseMongoId(out var ignoredId) || !profiles.ContainsKey(ignoredId))
                {
                    Ignored.Remove(ignore);
                    shouldSave = true;
                }
            }
        }

        if (shouldSave)
        {
            await SaveProfileRelationsAsync();
        }
    }

    public void SaveProfileRelations()
    {
        File.WriteAllText($"{_playerRelationsFullPath}/playerRelations.json", jsonUtil.Serialize(_playerRelations, true));
    }

    public async Task SaveProfileRelationsAsync()
    {
        await File.WriteAllTextAsync($"{_playerRelationsFullPath}/playerRelations.json", jsonUtil.Serialize(_playerRelations, true));
    }

    public FikaPlayerRelations GetStoredValue(string profileId)
    {
        if (!_playerRelations.ContainsKey(profileId))
        {
            StoreValue(profileId, new FikaPlayerRelations());
        }

        return _playerRelations[profileId];
    }

    public bool TryGetStoredValue(MongoId profileId, out FikaPlayerRelations? value)
    {
        return _playerRelations.TryGetValue(profileId, out value);
    }

    public void StoreValue(MongoId profileId, FikaPlayerRelations value)
    {
        if (!_playerRelations.TryAdd(profileId, value))
        {
            logger.Error($"Failed to add {profileId} to relations database");
            return;
        }

        SaveProfileRelations();
    }
}
