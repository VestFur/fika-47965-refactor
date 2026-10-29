using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;

namespace FikaServer.Services.Cache;

/// <summary>
/// Used to cache profiles by nicknames
/// </summary>
/// <param name="saveServer"></param>
[Injectable(InjectionType.Singleton)]
public class FikaProfileService(ISptLogger<FikaProfileService> logger, SaveServer saveServer)
{
    private readonly Dictionary<string, MongoId> _profiles = [];
    private readonly Lock _profilesLock = new();

    public Dictionary<string, MongoId> GetAllProfiles()
    {
        lock (_profilesLock)
        {
            // Names can change without changing the number of profiles. A
            // fresh mapping also prevents callers from observing a cache
            // while another request is rebuilding it.
            RefreshProfiles();
            return new Dictionary<string, MongoId>(_profiles);
        }
    }

    public MongoId? GetProfileIdByNickname(string nickname)
    {
        var profiles = GetAllProfiles();
        return profiles.TryGetValue(nickname, out var foundId) ? foundId : null;
    }

    public SptProfile? GetProfileByNickname(string nickname)
    {
        var profileId = GetProfileIdByNickname(nickname);
        if (!profileId.HasValue ||
            !saveServer.GetProfiles().TryGetValue(profileId.Value, out var profile) ||
            profile.CharacterData?.PmcData?.Info?.Nickname != nickname)
        {
            return null;
        }

        return profile;
    }

    private void RefreshProfiles()
    {
        _profiles.Clear();
        var profiles = saveServer.GetProfiles();
        HashSet<string> duplicateNicknames = [];
        foreach ((var id, var profile) in profiles)
        {
            if (!profile.HasProfileData())
            {
                continue;
            }

            var nick = profile?.CharacterData?.PmcData?.Info?.Nickname;
            if (!string.IsNullOrEmpty(nick))
            {
                if (duplicateNicknames.Contains(nick))
                {
                    continue;
                }

                if (!_profiles.TryAdd(nick, id))
                {
                    // Nickname-only commands cannot safely choose either
                    // profile when two players share the same nickname.
                    _profiles.Remove(nick);
                    duplicateNicknames.Add(nick);
                    logger.Error($"Nickname {nick} is shared by multiple profiles; nickname lookup is disabled for it");
                }
            }
        }
    }
}
