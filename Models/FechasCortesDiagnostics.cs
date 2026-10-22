namespace ExistenciasReset.Models;

public record InstanciaNiuxAbierta(int AttachmentId, string Proceso, int Pid, string Direccion, DateTime Desde, int DiasAbierta, int Conexiones);

public record TicketFechaAtrasada(
    int Folio, DateTime Fecha, DateTime? Fecha2, DateTime Hora, string Usuario, decimal? Total, int DiasAtraso);

public record PagoMalAsignado(
    int Folio, DateTime FechaPago, DateTime HoraCobro, int Caja, string TipoPago, decimal Importe, string Motivo);

public record CorteConDiferencia(
    int Caja,
    string Usuario,
    DateTime Apertura,
    DateTime? Cierre,
    decimal Fondo,
    decimal EfectivoContado,
    decimal EfectivoResumen,
    decimal EfectivoMovimientos,
    decimal TarjetaResumen,
    decimal TarjetaMovimientos,
    string Observaciones,
    string Motivos)
{
    public decimal DifEfectivoMovimientos => EfectivoMovimientos - EfectivoResumen;
    public decimal DifTarjetaMovimientos => TarjetaMovimientos - TarjetaResumen;
    public decimal EfectivoEsperado => Fondo + EfectivoResumen;
    public decimal DifConteo => EfectivoContado - EfectivoEsperado;
    public bool Abierta => Cierre is null;
}

public record FechasCortesDiagnostics(
    IReadOnlyList<InstanciaNiuxAbierta> Instancias,
    IReadOnlyList<TicketFechaAtrasada> Tickets,
    IReadOnlyList<PagoMalAsignado> Pagos,
    IReadOnlyList<CorteConDiferencia> Cortes,
    string ErrorMessage);
