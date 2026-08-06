namespace ExistenciasReset.Models;

public record CargaOrdenDiagnostics(
    IReadOnlyList<CargaOrdenIssue> OrdenIssues,
    IReadOnlyList<CargaOrdenIssue> EmpIssues,
    string ErrorMessage);
