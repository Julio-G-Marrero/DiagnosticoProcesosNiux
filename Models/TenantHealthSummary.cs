namespace ExistenciasReset.Models;

public record TenantHealthSummary(
    string TenantId,
    string TenantName,
    bool Reachable,
    int AttachmentCount,
    int ActiveStatementCount,
    long LongestRunningSeconds,
    string LongestRunningDescription,
    string ErrorMessage);
