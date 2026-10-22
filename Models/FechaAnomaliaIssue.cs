namespace ExistenciasReset.Models;

public record FechaAnomaliaIssue(
    int Folio,
    string FechaStrActual,
    string FechaStrCorregida,
    string Usuario,
    string? Total,
    int DiasDiferencia);
