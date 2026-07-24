namespace ExistenciasReset.Models;

public record ResetStepResult(string Name, string Sql, bool Success, string Message, long DurationMs, object? ScalarResult = null);
