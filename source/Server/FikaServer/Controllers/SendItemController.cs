using FikaServer.Models.Fika.Config;
using FikaServer.Models.Fika.SendItem;
using FikaServer.Models.Fika.WebSocket.Notifications;
using FikaServer.Services;
using FikaServer.WebSockets;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using System.Collections.Concurrent;

namespace FikaServer.Controllers;

[Injectable]
public class SendItemController(ISptLogger<SendItemController> logger, EventOutputHolder eventOutputHolder,
    SendItemTransferService transferService, SaveServer saveServer, HttpResponseUtil httpResponseUtil,
    FikaServerConfig fikaServerConfig, NotificationWebSocket notificationWebSocket, ICloner cloner)
{
    private static readonly ConcurrentDictionary<MongoId, SemaphoreSlim> SenderGates = new();

    public async ValueTask<ItemEventRouterResponse> SendItem(SendItemRequestData body, MongoId sessionId)
    {
        // Serialize requests from the same sender before reading inventory. A
        // second request must see the item removed by the first one, otherwise
        // both requests can create mail for the same inventory root.
        var gate = SenderGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        ItemEventRouterResponse output;
        ReceivedSentItemNotification notification;
        try
        {
            if (!saveServer.ProfileExists(sessionId))
            {
                throw new InvalidOperationException($"Sender profile {sessionId} was not found");
            }

            output = eventOutputHolder.GetOutput(sessionId);
            if (!fikaServerConfig.Server.AllowItemSending)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Item sending is disabled");
            }
            if (body is null || body.ItemIds is not { Length: > 0 } || string.IsNullOrWhiteSpace(body.Target))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Missing data in body");
            }

            var senderProfile = saveServer.GetProfile(sessionId);
            var senderPmc = senderProfile?.CharacterData?.PmcData;
            if (senderPmc?.Inventory?.Items is null)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Sender profile or inventory not found");
            }

            if (body.Target == sessionId)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Cannot send items to yourself");
            }

            if (!saveServer.ProfileExists(body.Target))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Target profile not found");
            }

            var targetProfile = saveServer.GetProfile(body.Target);
            if (targetProfile?.CharacterData?.PmcData?.Info is null || targetProfile.IsHeadlessProfile())
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Target cannot receive items");
            }

            var requestedIdStrings = body.ItemIds.Distinct().ToArray();
            if (requestedIdStrings.Any(id => string.IsNullOrWhiteSpace(id) || !MongoId.IsValidMongoId(id)))
                return httpResponseUtil.AppendErrorToOutput(output, "Invalid item id");
            var requestedItemIds = requestedIdStrings.Select(id => new MongoId(id)).ToArray();

            // A client may retry when its previous HTTP response was lost. The
            // sender's inventory is already empty by then; use the journal to
            // complete the same transfer and repeat its deletion response.
            if (transferService.HasTransferRecord(sessionId, requestedItemIds))
            {
                await transferService.CompleteOrCreateAsync(sessionId, body.Target, senderPmc.Info?.Nickname ?? "Unknown",
                    requestedItemIds, [], 0, output);
                return output;
            }

            var senderItems = senderPmc.Inventory.Items;
            if (requestedItemIds.Any(id => !senderItems.Any(item => item.Id == id)))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "One or more items were not found in inventory");
            }

            // RemoveItem deletes descendants with the selected root. Sending both
            // a container and a selected child would otherwise create two mail
            // roots while reporting only one deletion to the client.
            var requestedIds = requestedItemIds.ToHashSet();
            foreach (var itemId in requestedItemIds)
            {
                foreach (var member in senderItems.GetItemWithChildren(itemId))
                {
                    if (member.Id != itemId && requestedIds.Contains(member.Id))
                        return httpResponseUtil.AppendErrorToOutput(output, "A selected item is inside another selected item");
                }
            }

            logger.Info($"{requestedItemIds.Length} items are going to sessionID: {body.Target}");

            // MailSendService adopts orphaned roots and may alter ParentId/SlotId.
            // Keep the live inventory untouched until RemoveItem creates the response.
            var itemsToSend = cloner.Clone(requestedItemIds
                .SelectMany(id => senderItems.GetItemWithChildren(id))
                .DistinctBy(item => item.Id)
                .ToList()) ?? [];

            if (itemsToSend.Count == 0)
            {
                return httpResponseUtil.AppendErrorToOutput(output, "Item not found in inventory");
            }

            // The client checks this before opening the recipient picker, but
            // item-event requests can be sent directly. Check the complete
            // inventory snapshot so a selected container cannot hide a banned
            // attachment or one of several selected items.
            var blacklistedItems = fikaServerConfig.Client.BlacklistedItems;
            if (blacklistedItems is { Length: > 0 } && itemsToSend.Any(item =>
                    blacklistedItems.Contains(item.Template.ToString(), StringComparer.OrdinalIgnoreCase)))
            {
                return httpResponseUtil.AppendErrorToOutput(output, "One or more items are blacklisted from sending");
            }

            if (fikaServerConfig.Server.SentItemsLoseFIR)
            {
                foreach (var item in itemsToSend)
                {
                    item.Upd ??= new();
                    item.Upd.SpawnedInSession = false;
                }
            }

            await transferService.CompleteOrCreateAsync(sessionId, body.Target,
                senderPmc.Info?.Nickname ?? "Unknown", requestedItemIds, itemsToSend,
                fikaServerConfig.Server.ItemSendingStorageTime * 86_400L, output);

            var stackCount = itemsToSend.Count > 1
                ? 1d
                : itemsToSend[0].Upd?.StackObjectsCount.GetValueOrDefault() ?? 1d;

            notification = new ReceivedSentItemNotification
            {
                Nickname = senderPmc.Info?.Nickname ?? "Unknown",
                TargetId = body.Target,
                ItemName = $"{itemsToSend[0].Template} ShortName",
                StackCount = stackCount,
                Multiple = itemsToSend.Count > 1
            };
        }
        finally
        {
            gate.Release();
        }

        try
        {
            await notificationWebSocket.SendAsync(body.Target, notification);
        }
        catch (Exception ex)
        {
            // The mail and inventory change already succeeded; a stale websocket
            // must not make the item event appear to have failed to the sender.
            logger.Warning($"Unable to notify {body.Target} of the sent item: {ex.Message}");
        }

        return output;
    }

    public Dictionary<string, string> HandleAvailableReceivers(string sessionID)
    {
        Dictionary<string, string> result = [];

        if (!saveServer.ProfileExists(sessionID))
        {
            return result;
        }

        foreach (var profileKvP in saveServer.GetProfiles())
        {
            var profile = profileKvP.Value;

            // Skip freshly created profiles as they would cause an error
            if (profile.CharacterData?.PmcData?.Info is null)
            {
                continue;
            }

            // Skip headless clients
            if (profile.IsHeadlessProfile())
            {
                continue;
            }

            var nickname = profile.CharacterData.PmcData.Info.Nickname;
            var targetId = profile.ProfileInfo?.ProfileId;

            if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(targetId) ||
                targetId == sessionID)
            {
                continue;
            }

            // Skip if the same user already exists in the results
            if (result.ContainsKey(nickname))
            {
                continue;
            }

            result.Add(nickname, targetId);
        }

        return result;
    }
}
