using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Notifications;

public sealed class PostgresNotificationService(StoreDbContext db, TimeProvider timeProvider) : INotificationService
{
    public async Task EnsureAsync(
        Guid recipientId, string eventKey, string type, string title, string body, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO store.in_app_notifications
                (id, recipient_id, event_key, notification_type, title, body, created_at, updated_at)
            VALUES
                ({{Guid.NewGuid()}}, {{recipientId}}, {{eventKey}}, {{type}}, {{title}}, {{body}}, {{now}}, {{now}})
            ON CONFLICT (recipient_id, event_key) DO NOTHING
            """, ct);
    }

    public async Task<NotificationFeedDto> GetMineAsync(
        Guid recipientId, bool unreadOnly, int limit, DateTime? before = null, Guid? beforeId = null, CancellationToken ct = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 100);
        var mine = db.InAppNotifications.AsNoTracking().Where(x => x.RecipientId == recipientId);
        var unreadCount = await mine.CountAsync(x => x.ReadAt == null, ct);
        if (unreadOnly) mine = mine.Where(x => x.ReadAt == null);
        if (before.HasValue != beforeId.HasValue)
        {
            throw new NotificationFailure("INVALID_CURSOR", "before và beforeId phải cùng được cung cấp hoặc cùng bỏ trống.", 400);
        }

        if (before.HasValue && beforeId.HasValue)
        {
            var beforeDate = before.Value;
            var bId = beforeId.Value;
            mine = mine.Where(x => x.CreatedAt < beforeDate || (x.CreatedAt == beforeDate && x.Id < bId));
        }

        var itemsPlusOne = await mine.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(boundedLimit + 1).Select(x => new NotificationDto(
                x.Id, x.NotificationType, x.Title, x.Body, x.CreatedAt, x.ReadAt, x.ActionUrl)).ToListAsync(ct);

        var hasMore = itemsPlusOne.Count > boundedLimit;
        var items = itemsPlusOne.Take(boundedLimit).ToList();
        return new NotificationFeedDto(items, unreadCount, hasMore);
    }

    public async Task<NotificationDto> MarkReadAsync(
        Guid recipientId, Guid notificationId, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await db.InAppNotifications
            .Where(x => x.Id == notificationId && x.RecipientId == recipientId && x.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ReadAt, now)
                .SetProperty(x => x.UpdatedAt, now), ct);
        return await db.InAppNotifications.AsNoTracking()
            .Where(x => x.Id == notificationId && x.RecipientId == recipientId)
            .Select(x => new NotificationDto(x.Id, x.NotificationType, x.Title, x.Body, x.CreatedAt, x.ReadAt, x.ActionUrl))
            .SingleOrDefaultAsync(ct)
            ?? throw new NotificationFailure("NOT_FOUND", "Không tìm thấy thông báo.");
    }
}
