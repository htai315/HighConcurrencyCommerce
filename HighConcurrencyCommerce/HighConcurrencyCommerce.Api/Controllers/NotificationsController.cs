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
        [FromQuery] DateTime? before = null,
        [FromQuery] Guid? beforeId = null,
        CancellationToken ct = default)
    {
        if (before.HasValue != beforeId.HasValue)
        {
            throw new NotificationFailure("INVALID_CURSOR", "Tham số con trỏ phân trang không hợp lệ. before và beforeId phải cùng được cung cấp hoặc cùng bỏ trống.", 400);
        }

        return Ok(await notifications.GetMineAsync(UserId, unreadOnly, limit, before, beforeId, ct));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<NotificationDto>> Read(Guid id, CancellationToken ct) =>
        Ok(await notifications.MarkReadAsync(UserId, id, ct));
}
