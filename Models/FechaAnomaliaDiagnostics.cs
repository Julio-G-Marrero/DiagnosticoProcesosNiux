namespace ExistenciasReset.Models;

public record FechaAnomaliaDiagnostics(
    int TotalAnomalias,
    string FechaMasAntigua,
    string FechaMasReciente,
    IReadOnlyList<FechaAnomaliaIssue> Muestra,
    string ErrorMessage);
