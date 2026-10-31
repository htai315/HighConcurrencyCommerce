using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HighConcurrencyCommerce.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<NotificationFeedDto>> Mine(
        [FromQuery] bool unreadOnly = false,
        [FromQuery, Range(1, 100)] int limit = 50,
        CancellationToken ct = default) =>
        Ok(await notifications.GetMineAsync(UserId, unreadOnly, limit, ct));

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<NotificationDto>> Read(Guid id, CancellationToken ct) =>
        Ok(await notifications.MarkReadAsync(UserId, id, ct));
}
