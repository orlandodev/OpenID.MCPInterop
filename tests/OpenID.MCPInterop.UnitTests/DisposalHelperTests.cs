using OpenID.MCPInterop.Client;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

/// <summary>
/// Covers the McpClient-leak regression: SetConnectedAsync used to overwrite
/// its stored client with no disposal of the previous one. Tests against a
/// fake IAsyncDisposable (per "mock via interfaces") since McpClient itself
/// has no usable public constructor.
/// </summary>
public sealed class DisposalHelperTests
{
    [Fact]
    public async Task DisposeIfNotNullAsync_PreviousValueProvided_DisposesIt()
    {
        var previous = new FakeAsyncDisposable();

        await DisposalHelper.DisposeIfNotNullAsync(previous);

        Assert.Equal(1, previous.DisposeCount);
    }

    [Fact]
    public async Task DisposeIfNotNullAsync_NullPrevious_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => DisposalHelper.DisposeIfNotNullAsync(null).AsTask());

        Assert.Null(exception);
    }

    private sealed class FakeAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
