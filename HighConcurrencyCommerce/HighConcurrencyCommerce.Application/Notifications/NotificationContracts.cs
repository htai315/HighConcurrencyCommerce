namespace HighConcurrencyCommerce.Application.Notifications;

public sealed record NotificationDto(
    Guid Id,
    string Type,
    string Title,
    string Body,
    DateTime CreatedAt,
    DateTime? ReadAt);

public sealed record NotificationFeedDto(
    IReadOnlyList<NotificationDto> Items,
    int UnreadCount);

public sealed class NotificationFailure(string code, string message, int status = 404) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface INotificationService
{
    Task EnsureAsync(Guid recipientId, string eventKey, string type, string title, string body, CancellationToken ct = default);
    Task<NotificationFeedDto> GetMineAsync(Guid recipientId, bool unreadOnly, int limit, CancellationToken ct = default);
    Task<NotificationDto> MarkReadAsync(Guid recipientId, Guid notificationId, CancellationToken ct = default);
}
