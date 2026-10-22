using System.Globalization;
using ExistenciasReset.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ExistenciasReset.Services;

public class FechasCortesService(ILogger<FechasCortesService> logger, FirebirdMonitoringService monitoringService)
{
    private static readonly double[] Denominaciones = [1000, 500, 200, 100, 50, 20, 10, 5, 2, 1, 0.5, 0.1];
    private const int TipoPagoEfectivo = 1;
    private const int TipoPagoTarjeta = 4;
    private const decimal Tolerancia = 1m;
    private const decimal ToleranciaConteo = 500m;

    public async Task<FechasCortesDiagnostics> DiagnoseAsync(TenantOptions tenant, int dias, CancellationToken cancellationToken)
    {
        FechasCortesDiagnostics diagnostics;

        try
        {
            var desde = DateTime.Today.AddDays(-dias);

            var instancias = await GetInstanciasAsync(tenant, cancellationToken);

            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var tickets = await GetTicketsAsync(connection, desde, cancellationToken);
            var pagos = await GetPagosAsync(connection, desde, cancellationToken);
            var cortes = await GetCortesAsync(connection, desde, cancellationToken);

            diagnostics = new FechasCortesDiagnostics(instancias, tickets, pagos, cortes, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo diagnosticar fechas y cortes en el tenant {TenantId}", tenant.Id);
            diagnostics = new FechasCortesDiagnostics([], [], [], [], ex.Message);
        }

        return diagnostics;
    }

    private async Task<IReadOnlyList<InstanciaNiuxAbierta>> GetInstanciasAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        var attachments = await monitoringService.GetAttachmentsAsync(tenant, cancellationToken);

        return attachments
            .Where(a => a.RemoteProcess.Contains("NiuX", StringComparison.OrdinalIgnoreCase) && a.ConnectedAt.Date < DateTime.Today)
            .GroupBy(a => (a.RemotePid, a.RemoteProcess))
            .Select(g =>
            {
                var primera = g.OrderBy(a => a.ConnectedAt).First();
                var host = primera.RemoteAddress.Split('/')[0];
                return new InstanciaNiuxAbierta(
                    primera.Id, g.Key.RemoteProcess, g.Key.RemotePid, host, primera.ConnectedAt,
                    (DateTime.Today - primera.ConnectedAt.Date).Days, g.Count());
            })
            .OrderBy(i => i.Desde)
            .ToList();
    }

    private static async Task<IReadOnlyList<TicketFechaAtrasada>> GetTicketsAsync(
        FbConnection connection, DateTime desde, CancellationToken cancellationToken)
    {
        var tickets = new List<TicketFechaAtrasada>();

        await using var command = new FbCommand(@"
            SELECT FOLIO, FECHA, FECHA2, HORA, USUARIO, TOTAL
            FROM NOTAS_VTA
            WHERE HORA >= @desde AND TIPO_MOVIMIENTO = 'V' AND (CANCELADA IS NULL OR CANCELADA = '')
            ORDER BY FOLIO DESC", connection) { CommandTimeout = 120 };
        command.Parameters.Add("@desde", FbDbType.TimeStamp).Value = desde;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var fecha = Convert.ToDateTime(reader.GetValue(1));
            var hora = Convert.ToDateTime(reader.GetValue(3));

            if (fecha.Date != hora.Date)
            {
                tickets.Add(new TicketFechaAtrasada(
                    Convert.ToInt32(reader.GetValue(0)),
                    fecha,
                    reader.IsDBNull(2) ? null : Convert.ToDateTime(reader.GetValue(2)),
                    hora,
                    reader.GetValue(4)?.ToString()?.Trim() ?? string.Empty,
                    reader.IsDBNull(5) ? null : Convert.ToDecimal(reader.GetValue(5)),
                    (hora.Date - fecha.Date).Days));
            }
        }

        return tickets;
    }

    private static async Task<IReadOnlyList<PagoMalAsignado>> GetPagosAsync(
        FbConnection connection, DateTime desde, CancellationToken cancellationToken)
    {
        var pagos = new List<PagoMalAsignado>();

        await using var command = new FbCommand(@"
            SELECT E.NO_ORDEN, E.FECHA, N.HORA, E.CVE_CAJA, E.TIPO_PAGO, E.IMPORTE, C.FECHA_TIME_CIERRE
            FROM ING_EGR E
            JOIN NOTAS_VTA N ON N.FOLIO = E.NO_ORDEN
            LEFT JOIN POS_CAJAS C ON C.SECUENCIA = E.CVE_CAJA
            WHERE E.ES_PUNTO_VENTA = 'SI' AND E.CPTO = 99 AND N.HORA >= @desde
            ORDER BY E.NO_ORDEN DESC", connection) { CommandTimeout = 120 };
        command.Parameters.Add("@desde", FbDbType.TimeStamp).Value = desde;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var fechaPago = Convert.ToDateTime(reader.GetValue(1));
            var horaCobro = Convert.ToDateTime(reader.GetValue(2));
            var cierreCaja = reader.IsDBNull(6) ? (DateTime?)null : Convert.ToDateTime(reader.GetValue(6));
            var motivos = new List<string>();

            if (fechaPago.Date != horaCobro.Date)
            {
                motivos.Add("fecha del pago distinta al día del cobro");
            }

            if (cierreCaja.HasValue && horaCobro > cierreCaja.Value)
            {
                motivos.Add("la caja ya estaba cerrada cuando se cobró");
            }

            if (motivos.Count > 0)
            {
                pagos.Add(new PagoMalAsignado(
                    Convert.ToInt32(reader.GetValue(0)),
                    fechaPago,
                    horaCobro,
                    Convert.ToInt32(reader.GetValue(3)),
                    NombreTipoPago(reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4))),
                    Convert.ToDecimal(reader.GetValue(5)),
                    string.Join("; ", motivos)));
            }
        }

        return pagos;
    }

    private static async Task<IReadOnlyList<CorteConDiferencia>> GetCortesAsync(
        FbConnection connection, DateTime desde, CancellationToken cancellationToken)
    {
        var cajas = new List<(int Sec, string Usuario, DateTime Apertura, DateTime? Cierre, decimal Fondo, decimal Contado, string Obs)>();

        await using (var command = new FbCommand(@"
            SELECT SECUENCIA, USUARIO, FECHA_TIME_APERTURA, FECHA_TIME_CIERRE, OBSERVACIONES_CIERRE,
                   FONDO_1000, FONDO_500, FONDO_200, FONDO_100, FONDO_50, FONDO_20, FONDO_10, FONDO_5, FONDO_2, FONDO_1, FONDO_05, FONDO_010,
                   CIERRE_1000, CIERRE_500, CIERRE_200, CIERRE_100, CIERRE_50, CIERRE_20, CIERRE_10, CIERRE_5, CIERRE_2, CIERRE_1, CIERRE_05, CIERRE_010
            FROM POS_CAJAS WHERE FECHA_TIME_APERTURA >= @desde ORDER BY SECUENCIA", connection) { CommandTimeout = 120 })
        {
            command.Parameters.Add("@desde", FbDbType.TimeStamp).Value = desde;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                double fondo = 0, contado = 0;

                for (var i = 0; i < Denominaciones.Length; i++)
                {
                    fondo += ToDouble(reader.GetValue(5 + i)) * Denominaciones[i];
                    contado += ToDouble(reader.GetValue(17 + i)) * Denominaciones[i];
                }

                cajas.Add((
                    Convert.ToInt32(reader.GetValue(0)),
                    reader.GetValue(1)?.ToString()?.Trim() ?? string.Empty,
                    Convert.ToDateTime(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToDateTime(reader.GetValue(3)),
                    (decimal)fondo,
                    (decimal)contado,
                    reader.IsDBNull(4) ? string.Empty : reader.GetValue(4).ToString()!.Trim()));
            }
        }

        var cortes = new List<CorteConDiferencia>();

        if (cajas.Count == 0)
        {
            return cortes;
        }

        var minCaja = cajas.Min(c => c.Sec);
        var maxCaja = cajas.Max(c => c.Sec);
        var resumen = await GetSumasPorCajaAsync(connection,
            $"SELECT CVE_CAJA, TIPO_PAGO, SUM(IMPORTE) FROM POS_CAJAS_PAGOS WHERE CVE_CAJA BETWEEN {minCaja} AND {maxCaja} GROUP BY CVE_CAJA, TIPO_PAGO",
            cancellationToken);
        var movimientos = await GetSumasPorCajaAsync(connection,
            $"SELECT CVE_CAJA, TIPO_PAGO, SUM(IMPORTE) FROM ING_EGR WHERE TIPO_MOV = 1 AND CVE_CAJA BETWEEN {minCaja} AND {maxCaja} GROUP BY CVE_CAJA, TIPO_PAGO",
            cancellationToken);

        foreach (var c in cajas)
        {
            var efectivoResumen = Suma(resumen, c.Sec, TipoPagoEfectivo);
            var efectivoMov = Suma(movimientos, c.Sec, TipoPagoEfectivo);
            var tarjetaResumen = Suma(resumen, c.Sec, TipoPagoTarjeta);
            var tarjetaMov = Suma(movimientos, c.Sec, TipoPagoTarjeta);

            var corte = new CorteConDiferencia(
                c.Sec, c.Usuario, c.Apertura, c.Cierre, c.Fondo, c.Contado,
                efectivoResumen, efectivoMov, tarjetaResumen, tarjetaMov, c.Obs, string.Empty);

            var motivos = new List<string>();

            if (!corte.Abierta &&
                (Math.Abs(corte.DifEfectivoMovimientos) > Tolerancia || Math.Abs(corte.DifTarjetaMovimientos) > Tolerancia))
            {
                motivos.Add("los movimientos no coinciden con el resumen del corte");
            }

            var abiertaDias = ((c.Cierre ?? DateTime.Now).Date - c.Apertura.Date).Days;

            if (abiertaDias >= 1)
            {
                motivos.Add($"la caja estuvo abierta {abiertaDias} día(s)");
            }

            if (c.Cierre.HasValue && efectivoResumen > 0 && c.Contado == 0)
            {
                motivos.Add("sin conteo de efectivo al cierre");
            }
            else if (c.Cierre.HasValue && c.Contado > 0 && Math.Abs(corte.DifConteo) >= ToleranciaConteo)
            {
                motivos.Add("conteo distinto al esperado (más de $500)");
            }

            if (motivos.Count > 0)
            {
                cortes.Add(corte with { Motivos = string.Join("; ", motivos) });
            }
        }

        return cortes.OrderByDescending(c => c.Caja).ToList();
    }

    private static async Task<Dictionary<(int Caja, int Tipo), decimal>> GetSumasPorCajaAsync(
        FbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var sumas = new Dictionary<(int, int), decimal>();

        await using var command = new FbCommand(sql, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var caja = Convert.ToInt32(reader.GetValue(0));
            var tipo = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
            var importe = reader.IsDBNull(2) ? 0m : Convert.ToDecimal(reader.GetValue(2));
            sumas[(caja, tipo)] = sumas.GetValueOrDefault((caja, tipo)) + importe;
        }

        return sumas;
    }

    private static decimal Suma(Dictionary<(int Caja, int Tipo), decimal> sumas, int caja, int tipo) =>
        sumas.GetValueOrDefault((caja, tipo));

    private static double ToDouble(object value) =>
        value is DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static string NombreTipoPago(int tipo) => tipo switch
    {
        TipoPagoEfectivo => "Efectivo",
        TipoPagoTarjeta => "Tarjeta",
        _ => $"Tipo {tipo}"
    };
}
