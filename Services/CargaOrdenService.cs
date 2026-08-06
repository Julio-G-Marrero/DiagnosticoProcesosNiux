using ExistenciasReset.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ExistenciasReset.Services;

public class CargaOrdenService(ILogger<CargaOrdenService> logger)
{
    private const string OrdenIssueFilter =
        "CARGA_A_ORDEN IS NOT NULL AND TRIM(CARGA_A_ORDEN) <> '' AND TRIM(CARGA_A_ORDEN) NOT SIMILAR TO '[0-9]+'";

    private const string EmpIssueFilter =
        "CARGA_A_EMP IS NOT NULL AND TRIM(CARGA_A_EMP) <> '' AND TRIM(CARGA_A_EMP) NOT SIMILAR TO '[0-9]+'";

    public async Task<CargaOrdenDiagnostics> DiagnoseAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        CargaOrdenDiagnostics diagnostics;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var ordenIssues = await GetIssuesAsync(connection, OrdenIssueFilter, cancellationToken);
            var empIssues = await GetIssuesAsync(connection, EmpIssueFilter, cancellationToken);

            diagnostics = new CargaOrdenDiagnostics(ordenIssues, empIssues, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo diagnosticar CARGA_A_ORDEN/CARGA_A_EMP en el tenant {TenantId}", tenant.Id);
            diagnostics = new CargaOrdenDiagnostics([], [], ex.Message);
        }

        return diagnostics;
    }

    public Task<CargaOrdenFixResult> FixCargaAOrdenAsync(TenantOptions tenant, CancellationToken cancellationToken) =>
        FixColumnAsync(tenant, "CARGA_A_ORDEN", OrdenIssueFilter, cancellationToken);

    public Task<CargaOrdenFixResult> FixCargaAEmpAsync(TenantOptions tenant, CancellationToken cancellationToken) =>
        FixColumnAsync(tenant, "CARGA_A_EMP", EmpIssueFilter, cancellationToken);

    private async Task<CargaOrdenFixResult> FixColumnAsync(
        TenantOptions tenant, string columnName, string filter, CancellationToken cancellationToken)
    {
        CargaOrdenFixResult result;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new FbCommand(
                $"UPDATE NOTAS_VTA SET {columnName} = NULL WHERE {filter}", connection, transaction);

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = new CargaOrdenFixResult(true, rowsAffected, $"{rowsAffected} registro(s) puestos en NULL en {columnName}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo limpiar {Column} en el tenant {TenantId}", columnName, tenant.Id);
            result = new CargaOrdenFixResult(false, 0, ex.Message);
        }

        return result;
    }

    private static async Task<IReadOnlyList<CargaOrdenIssue>> GetIssuesAsync(
        FbConnection connection, string filter, CancellationToken cancellationToken)
    {
        var issues = new List<CargaOrdenIssue>();

        await using var command = new FbCommand(
            $@"SELECT FOLIO, FECHA_STR, USUARIO, CARGA_A_ORDEN, CARGA_A_EMP
               FROM NOTAS_VTA
               WHERE {filter}
               ORDER BY FECHA_STR DESC", connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            issues.Add(new CargaOrdenIssue(
                Convert.ToInt32(reader.GetValue(0)),
                reader.GetValue(1)?.ToString()?.Trim() ?? string.Empty,
                reader.GetValue(2)?.ToString()?.Trim() ?? string.Empty,
                reader.IsDBNull(3) ? null : reader.GetValue(3).ToString()?.Trim(),
                reader.IsDBNull(4) ? null : reader.GetValue(4).ToString()?.Trim()));
        }

        return issues;
    }
}
