using Azure;
using Azure.Data.Tables;
using Craft.Storage;

namespace Craft.Tests;

/// <summary>
/// A transaction the service refuses as busy comes back as a 202 with the 503 inside it, which the SDK's retry policy
/// never retries, so the store sends it again itself; anything else is the caller's to handle.
/// </summary>
public class AzureTableStoreTransactionRetryTests
{
    /// <summary>Answers its first <c>Failures</c> transactions with the given status, then accepts them.</summary>
    private sealed class RefusingClient(int failures, int status) : TableClient
    {
        public int Calls;

        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions,
            CancellationToken cancellationToken = default) =>
            ++Calls <= failures
                ? throw new TableTransactionFailedException(new RequestFailedException(status, "The server is busy.", "ServerBusy", null))
                : Task.FromResult<Response<IReadOnlyList<Response>>>(null!);
    }

    private static readonly TableTransactionAction[] Batch = [new(TableTransactionActionType.UpsertReplace, new TableEntity("p", "r"))];

    [Fact]
    public async Task ABusyTransactionIsSentAgainUntilItLands_ThenTheLastRefusalIsThrown()
    {
        var delays = AzureTableStore.TransactionRetryDelays;
        AzureTableStore.TransactionRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        try
        {
            var recovers = new RefusingClient(failures: 3, status: 503);
            await AzureTableStore.SubmitTransactionAsync(recovers, Batch, CancellationToken.None);
            Assert.Equal(4, recovers.Calls);

            var staysBusy = new RefusingClient(failures: 10, status: 503);
            Assert.Equal(503, (await Assert.ThrowsAsync<TableTransactionFailedException>(
                () => AzureTableStore.SubmitTransactionAsync(staysBusy, Batch, CancellationToken.None))).Status);
            Assert.Equal(4, staysBusy.Calls);

            var conflict = new RefusingClient(failures: 1, status: 409);
            await Assert.ThrowsAsync<TableTransactionFailedException>(() => AzureTableStore.SubmitTransactionAsync(conflict, Batch, CancellationToken.None));
            Assert.Equal(1, conflict.Calls);
        }
        finally
        {
            AzureTableStore.TransactionRetryDelays = delays;
        }
    }
}
