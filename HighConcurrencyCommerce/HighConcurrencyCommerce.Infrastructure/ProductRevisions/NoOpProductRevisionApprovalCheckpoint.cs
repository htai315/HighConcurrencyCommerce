using HighConcurrencyCommerce.Application.ProductRevisions;

namespace HighConcurrencyCommerce.Infrastructure.ProductRevisions;

public sealed class NoOpProductRevisionApprovalCheckpoint : IProductRevisionApprovalCheckpoint
{
    public Task ReachAsync(ProductRevisionApprovalStage stage, CancellationToken ct = default) => Task.CompletedTask;
}
