using ExistenciasReset.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ExistenciasReset.Services;

public class FechaAnomaliaService(ILogger<FechaAnomaliaService> logger)
{
    private const string AnomalyFilter = "FECHA2 IS NOT NULL AND FECHA <> FECHA2";
    private const int MuestraMaxima = 200;

    public async Task<FechaAnomaliaDiagnostics> DiagnoseAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        FechaAnomaliaDiagnostics diagnostics;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var (total, minFecha, maxFecha) = await GetResumenAsync(connection, cancellationToken);
            var muestra = await GetMuestraAsync(connection, cancellationToken);

            diagnostics = new FechaAnomaliaDiagnostics(total, minFecha, maxFecha, muestra, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo diagnosticar fechas anomalas en el tenant {TenantId}", tenant.Id);
            diagnostics = new FechaAnomaliaDiagnostics(0, string.Empty, string.Empty, [], ex.Message);
        }

        return diagnostics;
    }

    public async Task<FechaAnomaliaFixResult> FixAllAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        FechaAnomaliaFixResult result;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var filas = await GetFilasParaCorregirAsync(connection, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var corregidas = 0;

            foreach (var fila in filas)
            {
                var fechaCorregida = fila.Fecha2.Date;
                var horaCorregida = fila.Fecha2.Date + fila.Hora.TimeOfDay;
                var fechaStrCorregida = fechaCorregida.ToString("yyyyMMdd") + fila.Hora.ToString("HHmm");

                await using var command = new FbCommand(@"
                    UPDATE NOTAS_VTA
                    SET FECHA = @fecha, HORA = @hora, FECHA_STR = @fechaStr
                    WHERE FOLIO = @folio", connection, transaction);
                command.Parameters.Add("@fecha", FbDbType.TimeStamp).Value = fechaCorregida;
                command.Parameters.Add("@hora", FbDbType.TimeStamp).Value = horaCorregida;
                command.Parameters.Add("@fechaStr", FbDbType.VarChar).Value = fechaStrCorregida;
                command.Parameters.Add("@folio", FbDbType.Integer).Value = fila.Folio;

                await command.ExecuteNonQueryAsync(cancellationToken);
                corregidas++;
            }

            await transaction.CommitAsync(cancellationToken);

            result = new FechaAnomaliaFixResult(true, corregidas, $"{corregidas} ticket(s) corregido(s): se restauro FECHA/HORA usando FECHA2.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo corregir fechas anomalas en el tenant {TenantId}", tenant.Id);
            result = new FechaAnomaliaFixResult(false, 0, ex.Message);
        }

        return result;
    }

    private static async Task<(int Total, string MinFecha, string MaxFecha)> GetResumenAsync(
        FbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new FbCommand($@"
            SELECT COUNT(*), MIN(FECHA_STR), MAX(FECHA_STR)
            FROM NOTAS_VTA
            WHERE {AnomalyFilter}", connection) { CommandTimeout = 60 };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var total = 0;
        var min = string.Empty;
        var max = string.Empty;

        if (await reader.ReadAsync(cancellationToken))
        {
            total = Convert.ToInt32(reader.GetValue(0));
            min = reader.GetValue(1) == DBNull.Value ? string.Empty : reader.GetValue(1).ToString() ?? string.Empty;
            max = reader.GetValue(2) == DBNull.Value ? string.Empty : reader.GetValue(2).ToString() ?? string.Empty;
        }

        return (total, min, max);
    }

    private static async Task<IReadOnlyList<FechaAnomaliaIssue>> GetMuestraAsync(
        FbConnection connection, CancellationToken cancellationToken)
    {
        var issues = new List<FechaAnomaliaIssue>();

        await using var command = new FbCommand($@"
            SELECT FIRST {MuestraMaxima} FOLIO, FECHA, FECHA2, HORA, FECHA_STR, USUARIO, TOTAL
            FROM NOTAS_VTA
            WHERE {AnomalyFilter}
            ORDER BY FECHA_STR DESC", connection) { CommandTimeout = 60 };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var fecha = Convert.ToDateTime(reader.GetValue(1));
            var fecha2 = Convert.ToDateTime(reader.GetValue(2));
            var hora = Convert.ToDateTime(reader.GetValue(3));
            var fechaStrActual = reader.GetValue(4)?.ToString()?.Trim() ?? string.Empty;
            var usuario = reader.GetValue(5)?.ToString()?.Trim() ?? string.Empty;
            var total = reader.IsDBNull(6) ? null : reader.GetValue(6).ToString();
            var fechaStrCorregida = fecha2.ToString("yyyyMMdd") + hora.ToString("HHmm");
            var dias = (fecha.Date - fecha2.Date).Days;

            issues.Add(new FechaAnomaliaIssue(
                Convert.ToInt32(reader.GetValue(0)), fechaStrActual, fechaStrCorregida, usuario, total, dias));
        }

        return issues;
    }

    private static async Task<IReadOnlyList<(int Folio, DateTime Fecha2, DateTime Hora)>> GetFilasParaCorregirAsync(
        FbConnection connection, CancellationToken cancellationToken)
    {
        var filas = new List<(int, DateTime, DateTime)>();

        await using var command = new FbCommand($@"
            SELECT FOLIO, FECHA2, HORA
            FROM NOTAS_VTA
            WHERE {AnomalyFilter}", connection) { CommandTimeout = 120 };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            filas.Add((
                Convert.ToInt32(reader.GetValue(0)),
                Convert.ToDateTime(reader.GetValue(1)),
                Convert.ToDateTime(reader.GetValue(2))));
        }

        return filas;
    }
}
