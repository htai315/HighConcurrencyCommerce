using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.CarrierTracking;
using HighConcurrencyCommerce.Application.Fulfillment;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.CarrierTracking;

public sealed class PostgresCarrierConsignmentService(
    StoreDbContext db,
    ICarrierLabelStorage labelStorage,
    IFulfillmentService fulfillmentService,
    IExchangeFulfillmentService exchangeFulfillmentService,
    INotificationService notifications,
    TimeProvider timeProvider) : ICarrierConsignmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ValidStatuses =
    [
        "DRAFT", "LABEL_READY", "HANDED_OVER", "IN_TRANSIT", "OUT_FOR_DELIVERY",
        "DELIVERY_FAILED", "DELIVERED", "RETURNING", "RETURNED", "CANCELLED"
    ];

    public async Task<IReadOnlyList<CarrierConsignmentDto>> GetQueueAsync(
        string? status, CancellationToken ct = default)
    {
        var normalizedStatus = NormalizeOptional(status)?.ToUpperInvariant();
        if (normalizedStatus is not null && !ValidStatuses.Contains(normalizedStatus))
            throw InvalidArgument("Trạng thái vận đơn không hợp lệ.");
        var query = db.CarrierConsignments.AsNoTracking()
            .Include(x => x.Carrier)
            .Include(x => x.TrackingEvents)
            .AsQueryable();
        if (normalizedStatus is not null)
            query = query.Where(x => x.ConsignmentStatus == normalizedStatus);
        var values = await query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        return values.Select(ToDto).ToList();
    }

    public async Task<CarrierConsignmentDto> GetAsync(Guid consignmentId, CancellationToken ct = default) =>
        ToDto(await LoadAsync(consignmentId, ct));

    public async Task<CarrierConsignmentDto> CreateForSalesShipmentAsync(
        Guid staffId, Guid shipmentId, CreateCarrierConsignmentRequest request,
        string correlationId, CancellationToken ct = default)
    {
        var tracking = NormalizeTracking(request.TrackingNumber);
        var serviceCode = NormalizeOptional(request.ServiceCode);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var carrier = await LockActiveCarrierAsync(request.CarrierId, ct);
        var shipment = await db.Shipments
            .FromSqlInterpolated($"SELECT * FROM store.shipments WHERE id = {shipmentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw TrackingNotFound();
        EnsureShipmentEligible(shipment.ShipmentStatus);
        var order = await db.SalesOrders.AsNoTracking().SingleAsync(x => x.Id == shipment.OrderId, ct);
        var address = ParseAddress(order.ShippingAddressSnapshot);
        var consignment = CreateEntity(staffId, carrier.Id, tracking, serviceCode, address,
            order.ShippingMethodCodeSnapshot, order.ShippingMethodNameSnapshot, shipmentId, null);
        await PersistCreatedAsync(consignment, staffId, correlationId, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(consignment.Id, ct);
    }

    public async Task<CarrierConsignmentDto> CreateForExchangeShipmentAsync(
        Guid staffId, Guid shipmentId, CreateCarrierConsignmentRequest request,
        string correlationId, CancellationToken ct = default)
    {
        var tracking = NormalizeTracking(request.TrackingNumber);
        var serviceCode = NormalizeOptional(request.ServiceCode);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var carrier = await LockActiveCarrierAsync(request.CarrierId, ct);
        var shipment = await db.ExchangeShipments
            .FromSqlInterpolated($"SELECT * FROM store.exchange_shipments WHERE id = {shipmentId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw TrackingNotFound();
        EnsureShipmentEligible(shipment.ShipmentStatus);
        var returnRequest = await db.OrderReturnRequests.AsNoTracking()
            .SingleAsync(x => x.Id == shipment.ReturnRequestId, ct);
        var order = await db.SalesOrders.AsNoTracking().SingleAsync(x => x.Id == returnRequest.OrderId, ct);
        var address = ParseAddress(order.ShippingAddressSnapshot);
        var consignment = CreateEntity(staffId, carrier.Id, tracking, serviceCode, address,
            order.ShippingMethodCodeSnapshot, order.ShippingMethodNameSnapshot, null, shipmentId);
        await PersistCreatedAsync(consignment, staffId, correlationId, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(consignment.Id, ct);
    }

    public async Task<CarrierConsignmentDto> AttachLabelAsync(
        Guid staffId, Guid consignmentId, StoredCarrierLabel label, long expectedRevision,
        string correlationId, CancellationToken ct = default)
    {
        string? previousKey;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var consignment = await LockConsignmentAsync(consignmentId, ct);
            EnsureRevision(consignment, expectedRevision);
            if (consignment.ConsignmentStatus is not ("DRAFT" or "LABEL_READY"))
                throw InvalidTransition("Chỉ có thể gắn nhãn trước khi bàn giao vận đơn.");
            previousKey = consignment.LabelFileKey;
            var before = Snapshot(consignment);
            consignment.LabelFileKey = label.StorageKey;
            consignment.LabelContentType = label.ContentType;
            consignment.LabelSizeBytes = label.Length;
            consignment.LabelSha256 = label.Sha256;
            consignment.ConsignmentStatus = "LABEL_READY";
            Touch(consignment, staffId);
            db.CarrierTrackingEvents.Add(Event(consignment, $"label-created:{consignment.Revision}", "LABEL_CREATED",
                "LABEL_READY", null, "Nhãn vận chuyển đã được cập nhật.", consignment.UpdatedAt,
                "STAFF", staffId, HashPayload("LABEL_CREATED", "LABEL_READY", null, label.Sha256, consignment.UpdatedAt)));
            db.AuditLogs.Add(Audit(staffId, "CARRIER_LABEL_ATTACHED", consignment.Id,
                before, Snapshot(consignment), correlationId, consignment.UpdatedAt));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (previousKey is not null && previousKey != label.StorageKey)
            await labelStorage.DeleteAsync(previousKey, ct);
        return await GetAsync(consignmentId, ct);
    }

    public async Task<OpenCarrierLabel> OpenLabelAsync(Guid consignmentId, CancellationToken ct = default)
    {
        var key = await db.CarrierConsignments.AsNoTracking()
            .Where(x => x.Id == consignmentId)
            .Select(x => x.LabelFileKey)
            .SingleOrDefaultAsync(ct);
        if (key is null) throw TrackingNotFound();
        return await labelStorage.OpenReadAsync(key, ct)
            ?? throw TrackingNotFound();
    }

    public async Task<CarrierConsignmentDto> HandoverAsync(
        Guid staffId, Guid consignmentId, long expectedRevision,
        string correlationId, CancellationToken ct = default)
    {
        var target = await db.CarrierConsignments.AsNoTracking()
            .Where(x => x.Id == consignmentId)
            .Select(x => new { x.SalesShipmentId, x.ExchangeShipmentId })
            .SingleOrDefaultAsync(ct) ?? throw TrackingNotFound();
        if (target.SalesShipmentId is { } salesShipmentId)
            await fulfillmentService.HandoverWithConsignmentAsync(
                staffId, salesShipmentId, consignmentId, expectedRevision, correlationId, ct);
        else if (target.ExchangeShipmentId is { } exchangeShipmentId)
            await exchangeFulfillmentService.HandoverWithConsignmentAsync(
                staffId, exchangeShipmentId, consignmentId, expectedRevision, correlationId, ct);
        else
            throw TrackingNotFound();
        return await GetAsync(consignmentId, ct);
    }

    public async Task<CarrierConsignmentDto> RecordEventAsync(
        Guid staffId, Guid consignmentId, RecordCarrierTrackingEventRequest request,
        string correlationId, CancellationToken ct = default)
    {
        var eventKey = request.EventKey.Trim();
        var eventType = request.EventType.Trim().ToUpperInvariant();
        var location = NormalizeOptional(request.Location);
        var description = NormalizeOptional(request.Description);
        var occurredAt = NormalizeUtc(request.OccurredAt);
        var targetStatus = TargetStatus(eventType);
        var payloadHash = HashPayload(eventType, targetStatus, location, description, occurredAt);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var consignment = await LockConsignmentAsync(consignmentId, ct);

        var replay = await db.CarrierTrackingEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConsignmentId == consignmentId && x.EventKey == eventKey, ct);
        if (replay is not null)
        {
            if (replay.PayloadHash != payloadHash)
                throw new CarrierTrackingFailure("TRACKING_IDEMPOTENCY_CONFLICT",
                    "Event key đã được dùng với nội dung khác.", 409);
            await tx.CommitAsync(ct);
            return await GetAsync(consignmentId, ct);
        }

        EnsureRevision(consignment, request.ExpectedRevision);
        var shipment = await LoadShipmentStateAsync(consignment, ct);
        ValidateEventTime(occurredAt, shipment.CreatedAt);
        EnsureManualTransition(consignment.ConsignmentStatus, eventType, shipment.Status);
        var before = Snapshot(consignment);
        consignment.ConsignmentStatus = targetStatus;
        Touch(consignment, staffId);
        db.CarrierTrackingEvents.Add(Event(consignment, eventKey, eventType, targetStatus,
            location, description, occurredAt, "STAFF", staffId, payloadHash));
        db.AuditLogs.Add(Audit(staffId, "CARRIER_TRACKING_EVENT_RECORDED", consignment.Id,
            before, Snapshot(consignment), correlationId, consignment.UpdatedAt));
        var notification = NotificationContent(eventType, shipment.OrderNumber);
        await notifications.EnsureAsync(
            shipment.CustomerId,
            $"tracking:{consignment.Id:N}:{HashKey(eventKey)[..16]}",
            "ORDER_TRACKING_UPDATED",
            notification.Title,
            notification.Body,
            ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(consignmentId, ct);
    }

    public async Task<CarrierConsignmentDto> CancelAsync(
        Guid staffId, Guid consignmentId, long expectedRevision,
        string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var consignment = await LockConsignmentAsync(consignmentId, ct);
        EnsureRevision(consignment, expectedRevision);
        if (consignment.ConsignmentStatus == "CANCELLED")
        {
            await tx.CommitAsync(ct);
            return await GetAsync(consignmentId, ct);
        }
        if (consignment.ConsignmentStatus is not ("DRAFT" or "LABEL_READY"))
            throw InvalidTransition("Chỉ có thể hủy vận đơn trước khi bàn giao.");
        var before = Snapshot(consignment);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        consignment.ConsignmentStatus = "CANCELLED";
        consignment.CancelledAt = now;
        Touch(consignment, staffId);
        db.CarrierTrackingEvents.Add(Event(consignment, "consignment-cancelled", "CANCELLED",
            "CANCELLED", null, "Vận đơn đã bị hủy.", now, "STAFF", staffId,
            HashPayload("CANCELLED", "CANCELLED", null, null, now)));
        db.AuditLogs.Add(Audit(staffId, "CARRIER_CONSIGNMENT_CANCELLED", consignment.Id,
            before, Snapshot(consignment), correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(consignmentId, ct);
    }

    private async Task PersistCreatedAsync(
        CarrierConsignment consignment, Guid staffId, string correlationId, CancellationToken ct)
    {
        db.CarrierConsignments.Add(consignment);
        db.CarrierTrackingEvents.Add(Event(consignment, "consignment-created", "CONSIGNMENT_CREATED",
            "DRAFT", null, "Vận đơn đã được tạo.", consignment.CreatedAt, "STAFF", staffId,
            HashPayload("CONSIGNMENT_CREATED", "DRAFT", null, null, consignment.CreatedAt)));
        db.AuditLogs.Add(Audit(staffId, "CARRIER_CONSIGNMENT_CREATED", consignment.Id,
            null, Snapshot(consignment), correlationId, consignment.CreatedAt));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            if (pg.ConstraintName is "carrier_consignments_sales_shipment_id_key" or
                "carrier_consignments_exchange_shipment_id_key")
                throw new CarrierTrackingFailure("CONSIGNMENT_ALREADY_EXISTS",
                    "Shipment đã có vận đơn.", 409);
            if (pg.ConstraintName == "ux_carrier_consignments_tracking")
                throw new CarrierTrackingFailure("TRACKING_NUMBER_CONFLICT",
                    "Mã vận đơn đã tồn tại cho đơn vị vận chuyển này.", 409);
            throw;
        }
    }

    private async Task<Carrier> LockActiveCarrierAsync(Guid carrierId, CancellationToken ct)
    {
        var carrier = await db.Carriers
            .FromSqlInterpolated($"SELECT * FROM store.carriers WHERE id = {carrierId} FOR SHARE")
            .SingleOrDefaultAsync(ct) ?? throw new CarrierTrackingFailure(
                "CARRIER_NOT_FOUND", "Không tìm thấy đơn vị vận chuyển.", 404);
        if (carrier.CarrierStatus != "ACTIVE")
            throw new CarrierTrackingFailure("CARRIER_INACTIVE",
                "Đơn vị vận chuyển đang ngừng nhận vận đơn mới.", 409);
        return carrier;
    }

    private async Task<CarrierConsignment> LockConsignmentAsync(Guid id, CancellationToken ct) =>
        await db.CarrierConsignments
            .FromSqlInterpolated($"SELECT * FROM store.carrier_consignments WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw TrackingNotFound();

    private async Task<CarrierConsignment> LoadAsync(Guid id, CancellationToken ct) =>
        await db.CarrierConsignments.AsNoTracking().Include(x => x.Carrier).Include(x => x.TrackingEvents)
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw TrackingNotFound();

    private async Task<(string Status, DateTime CreatedAt, Guid CustomerId, string OrderNumber)> LoadShipmentStateAsync(
        CarrierConsignment consignment, CancellationToken ct)
    {
        if (consignment.SalesShipmentId is { } salesId)
        {
            var value = await db.Shipments.AsNoTracking().Where(x => x.Id == salesId)
                .Select(x => new
                {
                    x.ShipmentStatus, x.CreatedAt, x.Order.CustomerId, x.Order.OrderNumber
                }).SingleAsync(ct);
            return (value.ShipmentStatus, value.CreatedAt, value.CustomerId, value.OrderNumber);
        }
        var exchange = await db.ExchangeShipments.AsNoTracking().Where(x => x.Id == consignment.ExchangeShipmentId)
            .Select(x => new
            {
                x.ShipmentStatus, x.CreatedAt,
                x.ReturnRequest.CustomerId,
                x.ReturnRequest.Order.OrderNumber
            }).SingleAsync(ct);
        return (exchange.ShipmentStatus, exchange.CreatedAt, exchange.CustomerId, exchange.OrderNumber);
    }

    private CarrierConsignment CreateEntity(
        Guid staffId, Guid carrierId, string tracking, string? serviceCode, AddressSnapshot address,
        string? shippingCode, string? shippingName, Guid? salesShipmentId, Guid? exchangeShipmentId)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new CarrierConsignment
        {
            Id = Guid.NewGuid(), SalesShipmentId = salesShipmentId, ExchangeShipmentId = exchangeShipmentId,
            CarrierId = carrierId, TrackingNumber = tracking, ConsignmentStatus = "DRAFT",
            ServiceCode = serviceCode, RecipientNameSnapshot = address.RecipientName,
            RecipientPhoneSnapshot = address.Phone, DestinationSnapshot = FormatDestination(address),
            ShippingMethodCodeSnapshot = shippingCode, ShippingMethodNameSnapshot = shippingName,
            Revision = 1, CreatedBy = staffId, UpdatedBy = staffId, CreatedAt = now, UpdatedAt = now
        };
    }

    private static CarrierConsignmentDto ToDto(CarrierConsignment value)
    {
        var targetId = value.SalesShipmentId ?? value.ExchangeShipmentId
            ?? throw new InvalidOperationException("Consignment target is missing.");
        var trackingUrl = value.Carrier.TrackingUrlTemplate?.Replace(
            "{trackingNumber}", Uri.EscapeDataString(value.TrackingNumber), StringComparison.Ordinal);
        var label = value.LabelFileKey is null ? null : new CarrierLabelDto(
            value.LabelContentType!, value.LabelSizeBytes!.Value, value.LabelSha256!);
        return new CarrierConsignmentDto(
            value.Id, value.SalesShipmentId is null ? "EXCHANGE" : "SALES", targetId,
            value.CarrierId, value.Carrier.Code, value.Carrier.Name, value.TrackingNumber, trackingUrl,
            value.ConsignmentStatus, value.ServiceCode, value.RecipientNameSnapshot,
            value.RecipientPhoneSnapshot, value.DestinationSnapshot,
            value.ShippingMethodCodeSnapshot, value.ShippingMethodNameSnapshot, label, value.Revision,
            value.HandedOverAt, value.DeliveredAt, value.CancelledAt, value.CreatedAt, value.UpdatedAt,
            value.TrackingEvents.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                .Select(x => new CarrierTrackingEventDto(x.Id, x.EventKey, x.EventType, x.EventStatus,
                    x.Location, x.Description, x.OccurredAt, x.RecordedAt, x.SourceType)).ToList());
    }

    private static void EnsureShipmentEligible(string status)
    {
        if (status is not ("PENDING_PACKING" or "PACKED"))
            throw new CarrierTrackingFailure("SHIPMENT_STATE_CONFLICT",
                "Shipment phải đang chờ đóng gói hoặc đã đóng gói để tạo vận đơn.", 409);
    }

    private static void EnsureManualTransition(string current, string eventType, string shipmentStatus)
    {
        if (shipmentStatus is not ("SHIPPING" or "DELIVERY_FAILED"))
            throw new CarrierTrackingFailure("SHIPMENT_STATE_CONFLICT",
                "Shipment chưa ở trạng thái giao hàng phù hợp.", 409);
        var allowed = eventType switch
        {
            "PICKED_UP" => current == "HANDED_OVER",
            "IN_TRANSIT" => current is "HANDED_OVER" or "IN_TRANSIT",
            "OUT_FOR_DELIVERY" => current == "IN_TRANSIT",
            "DELIVERY_FAILED" => current == "OUT_FOR_DELIVERY",
            "RETURNING" => current == "DELIVERY_FAILED" && shipmentStatus == "DELIVERY_FAILED",
            _ => false
        };
        if (!allowed) throw InvalidTransition($"Event {eventType} không hợp lệ từ trạng thái {current}.");
    }

    private void ValidateEventTime(DateTime occurredAt, DateTime shipmentCreatedAt)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (occurredAt < shipmentCreatedAt || occurredAt > now.AddMinutes(5))
            throw InvalidArgument("Thời điểm event phải sau khi tạo shipment và không quá 5 phút trong tương lai.");
    }

    private static string TargetStatus(string eventType) => eventType switch
    {
        "PICKED_UP" or "IN_TRANSIT" => "IN_TRANSIT",
        "OUT_FOR_DELIVERY" => "OUT_FOR_DELIVERY",
        "DELIVERY_FAILED" => "DELIVERY_FAILED",
        "RETURNING" => "RETURNING",
        _ => throw InvalidArgument("Loại tracking event không hợp lệ.")
    };

    private void Touch(CarrierConsignment value, Guid staffId)
    {
        value.Revision++;
        value.UpdatedBy = staffId;
        value.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static void EnsureRevision(CarrierConsignment value, long expected)
    {
        if (value.Revision != expected)
            throw new CarrierTrackingFailure("CONSIGNMENT_STALE",
                $"Vận đơn đã thay đổi (revision hiện tại: {value.Revision}). Hãy tải lại dữ liệu.", 409);
    }

    private static CarrierTrackingEvent Event(
        CarrierConsignment consignment, string key, string type, string status,
        string? location, string? description, DateTime occurredAt, string source,
        Guid? actorId, string payloadHash) => new()
    {
        Id = Guid.NewGuid(), ConsignmentId = consignment.Id, EventKey = key, EventType = type,
        EventStatus = status, Location = location, Description = description,
        OccurredAt = occurredAt, RecordedAt = DateTime.UtcNow, SourceType = source,
        ActorId = actorId, PayloadHash = payloadHash
    };

    private static string HashPayload(
        string type, string status, string? location, string? description, DateTime occurredAt)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            type, status, location, description,
            occurredAt = occurredAt.ToUniversalTime().ToString("O")
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string HashKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static (string Title, string Body) NotificationContent(string eventType, string orderNumber) =>
        eventType switch
        {
            "PICKED_UP" => ("Đơn vị vận chuyển đã nhận hàng", $"Đơn {orderNumber} đã được đơn vị vận chuyển tiếp nhận."),
            "IN_TRANSIT" => ("Đơn hàng đang trung chuyển", $"Đơn {orderNumber} đang được vận chuyển đến khu vực giao hàng."),
            "OUT_FOR_DELIVERY" => ("Đơn hàng sắp được giao", $"Đơn {orderNumber} đang trên đường giao đến bạn."),
            "DELIVERY_FAILED" => ("Giao hàng chưa thành công", $"Lần giao gần nhất của đơn {orderNumber} chưa thành công."),
            "RETURNING" => ("Đơn hàng đang được hoàn về", $"Đơn {orderNumber} đang được vận chuyển hoàn về cửa hàng."),
            _ => ("Cập nhật vận chuyển", $"Đơn {orderNumber} có cập nhật vận chuyển mới.")
        };

    private static string Snapshot(CarrierConsignment value) => JsonSerializer.Serialize(new
    {
        value.Id, value.SalesShipmentId, value.ExchangeShipmentId, value.CarrierId,
        value.TrackingNumber, value.ConsignmentStatus, value.ServiceCode,
        value.LabelContentType, value.LabelSizeBytes, value.LabelSha256, value.Revision
    });

    private static AuditLog Audit(
        Guid actorId, string action, Guid entityId, string? before, string? after,
        string correlationId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER", Action = action,
        EntityType = "CarrierConsignment", EntityId = entityId, BeforeData = before,
        AfterData = after, CorrelationId = correlationId, OccurredAt = now,
        CreatedAt = now, UpdatedAt = now
    };

    private static AddressSnapshot ParseAddress(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AddressSnapshot>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidArgument("Snapshot địa chỉ giao hàng không hợp lệ.");
        }
    }

    private static string FormatDestination(AddressSnapshot value) => string.Join(", ",
        new[] { value.AddressLine, value.Ward, value.Province }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string NormalizeTracking(string value) => value.Trim();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static CarrierTrackingFailure InvalidArgument(string message) =>
        new("TRACKING_INVALID_ARGUMENT", message);
    private static CarrierTrackingFailure InvalidTransition(string message) =>
        new("TRACKING_INVALID_TRANSITION", message);
    private static CarrierTrackingFailure TrackingNotFound() =>
        new("TRACKING_NOT_FOUND", "Không tìm thấy vận đơn.", 404);
    private sealed record AddressSnapshot(string RecipientName, string Phone, string AddressLine, string? Ward, string Province);
}
