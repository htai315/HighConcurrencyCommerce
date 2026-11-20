using System.Diagnostics.Metrics;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PayOsReconciliationMetrics
{
    public const string MeterName = "HighConcurrencyCommerce.PayOs";

    private readonly Counter<long> opened;
    private readonly Counter<long> claimed;
    private readonly Counter<long> resolved;
    private readonly Counter<long> refunds;
    private readonly Histogram<double> resolutionAgeMinutes;
    private readonly Histogram<double> refundAmount;

    public PayOsReconciliationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        opened = meter.CreateCounter<long>("payos.reconciliation.opened", "case", "Reconciliation cases opened");
        claimed = meter.CreateCounter<long>("payos.reconciliation.claimed", "case", "Cases claimed or reassigned");
        resolved = meter.CreateCounter<long>("payos.reconciliation.resolved", "case", "Cases resolved");
        refunds = meter.CreateCounter<long>("payos.reconciliation.refunds", "refund", "Manual refunds recorded");
        resolutionAgeMinutes = meter.CreateHistogram<double>("payos.reconciliation.resolution_age", "min", "Age when a case reaches a terminal state");
        refundAmount = meter.CreateHistogram<double>("payos.reconciliation.refund_amount", "VND", "Manual refund amount");
    }

    public void CaseOpened(string reason) => opened.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void CaseClaimed(bool reassigned) => claimed.Add(1,
        new KeyValuePair<string, object?>("operation", reassigned ? "reassigned" : "claimed"));

    public void CaseResolved(string reason, DateTime createdAt, DateTime resolvedAt)
    {
        resolved.Add(1, new KeyValuePair<string, object?>("reason", reason));
        resolutionAgeMinutes.Record(Math.Max(0, (resolvedAt - createdAt).TotalMinutes),
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RefundRecorded(string reason, decimal amount, DateTime createdAt, DateTime resolvedAt)
    {
        refunds.Add(1, new KeyValuePair<string, object?>("reason", reason));
        refundAmount.Record((double)amount, new KeyValuePair<string, object?>("reason", reason));
        CaseResolved(reason, createdAt, resolvedAt);
    }
}
