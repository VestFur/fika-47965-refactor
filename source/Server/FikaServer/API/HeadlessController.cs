using FikaServer.Helpers;
using FikaServer.Models;
using FikaServer.Models.Enums;
using FikaServer.Services.Headless;
using FikaShared;
using FikaShared.Responses;
using Microsoft.AspNetCore.Mvc;
using System.Net.WebSockets;
using static FikaShared.Enums;

namespace FikaServer.API;

[ApiController]
[Route("fika/api/headless")]
[RequireApiKey]
public class HeadlessController(HeadlessService headlessService, HeadlessHelper headlessHelper) : ControllerBase
{
    [HttpGet]
    public IActionResult HandleRequest()
    {
        var headlessClients = headlessService.HeadlessClients;
        var clients = new List<OnlineHeadless>(headlessClients.Count);
        foreach ((var profileId, var headlessClient) in headlessClients)
        {
            EHeadlessState state;
            List<string> profileIds;
            int playerCount;
            lock (headlessClient)
            {
                state = headlessClient.State is EHeadlessStatus.READY &&
                    headlessClient.ClientReady &&
                    headlessClient.RequesterSessionID == null &&
                    headlessClient.WebSocket.State is WebSocketState.Open &&
                    !headlessClient.IsReconnecting ? EHeadlessState.Ready : EHeadlessState.NotReady;
                profileIds = [.. headlessClient.Players.Select(x => x.ToString())];
                playerCount = profileIds.Count;
            }

            clients.Add(new OnlineHeadless
            {
                ProfileId = profileId,
                Nickname = headlessHelper.GetHeadlessNickname(profileId),
                State = state,
                Players = playerCount,
                ProfileIds = profileIds
            });
        }

        var headlessResponse = new GetHeadlessResponse()
        {
            HeadlessClients = clients
        };

        return Ok(headlessResponse);
    }
}
