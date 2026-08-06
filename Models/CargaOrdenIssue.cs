namespace ExistenciasReset.Models;

public record CargaOrdenIssue(
    int Folio,
    string FechaStr,
    string Usuario,
    string? CargaAOrden,
    string? CargaAEmp);
