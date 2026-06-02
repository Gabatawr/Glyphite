namespace Glyphite.Abstractions.Models;

public record ModelChangeRequest(string SessionId, string Model);

public record SessionDeleteRequest(string SessionId);

public record ConfigUpdateRequest(
    Dictionary<string, string> Config,
    string? Scope = null,
    string? SessionId = null
);
