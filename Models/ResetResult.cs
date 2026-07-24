namespace ExistenciasReset.Models;

public record ResetResult(bool Success, IReadOnlyList<ResetStepResult> Steps, int? TotalGenerado, string ErrorMessage, long TotalDurationMs);
