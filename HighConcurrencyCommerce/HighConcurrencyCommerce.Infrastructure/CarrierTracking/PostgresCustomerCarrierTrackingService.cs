using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.CarrierTracking;

public sealed class PostgresCustomerCarrierTrackingService(StoreDbContext db) : ICustomerCarrierTrackingService
{
    public async Task<CarrierConsignmentDto> GetForOrderAsync(
        Guid customerId, Guid orderId, CancellationToken ct = default)
    {
        var consignment = await db.CarrierConsignments.AsNoTracking()
            .Include(x => x.Carrier)
            .Include(x => x.TrackingEvents)
            .Where(x => x.SalesShipment != null
                && x.SalesShipment.OrderId == orderId
                && x.SalesShipment.Order.CustomerId == customerId)
            .SingleOrDefaultAsync(ct);
        return consignment is null ? throw NotFound() : ToDto(consignment);
    }

    private static CarrierConsignmentDto ToDto(CarrierConsignment value)
    {
        var shipmentId = value.SalesShipmentId ?? throw new InvalidOperationException("Sales consignment target is missing.");
        var label = value.LabelFileKey is null ? null : new CarrierLabelDto(
            value.LabelContentType!, value.LabelSizeBytes!.Value, value.LabelSha256!);
        return new CarrierConsignmentDto(
            value.Id, "SALES", shipmentId, value.CarrierId, value.Carrier.Code, value.Carrier.Name,
            value.TrackingNumber, BuildTrackingUrl(value), value.ConsignmentStatus, value.ServiceCode,
            value.RecipientNameSnapshot, value.RecipientPhoneSnapshot, value.DestinationSnapshot,
            value.ShippingMethodCodeSnapshot, value.ShippingMethodNameSnapshot, label, value.Revision,
            value.HandedOverAt, value.DeliveredAt, value.CancelledAt, value.CreatedAt, value.UpdatedAt,
            value.TrackingEvents.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                .Select(x => new CarrierTrackingEventDto(
                    x.Id, x.EventKey, x.EventType, x.EventStatus, x.Location, x.Description,
                    x.OccurredAt, x.RecordedAt, x.SourceType)).ToList());
    }

    private static string? BuildTrackingUrl(CarrierConsignment consignment)
    {
        var template = consignment.Carrier.TrackingUrlTemplate;
        if (template is null) return null;
        var value = template.Replace(
            "{trackingNumber}", Uri.EscapeDataString(consignment.TrackingNumber), StringComparison.Ordinal);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static CarrierTrackingFailure NotFound() =>
        new("TRACKING_NOT_FOUND", "Không tìm thấy thông tin vận chuyển của đơn hàng.", 404);
}
