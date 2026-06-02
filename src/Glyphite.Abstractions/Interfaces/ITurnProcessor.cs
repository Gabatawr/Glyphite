using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface ITurnProcessor
{
    IAsyncEnumerable<TurnEvent> ProcessAsync(
        string sessionId,
        string input,
        ChatOptions chatOptions,
        CancellationToken ct);
}
