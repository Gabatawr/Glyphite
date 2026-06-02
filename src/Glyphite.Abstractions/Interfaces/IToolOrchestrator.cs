using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface IToolOrchestrator
{
    Task<List<ChatMessage>> ExecuteToolsAsync(
        IReadOnlyList<FunctionCallContent> fccs,
        ChatOptions? options,
        CancellationToken ct);
}
