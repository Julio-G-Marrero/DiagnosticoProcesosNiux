using ExistenciasReset.Models;
using ExistenciasReset.Services;
using Microsoft.AspNetCore.Components;

namespace ExistenciasReset.Components.Pages;

public partial class FechaAnomalias(FechaAnomaliaService fechaAnomaliaService, IReadOnlyList<TenantOptions> tenants)
{
    private string SelectedTenantId { get; set; } = string.Empty;
    private FechaAnomaliaDiagnostics? Diagnostics { get; set; }
    private bool IsDiagnosing { get; set; }
    private bool PendingFix { get; set; }
    private bool IsFixing { get; set; }
    private FechaAnomaliaFixResult? LastFixResult { get; set; }

    private IReadOnlyList<TenantOptions> ActiveTenants =>
        tenants.Where(t => t.Active).OrderBy(t => t.Name).ToList();

    private TenantOptions? SelectedTenant =>
        tenants.FirstOrDefault(t => t.Id == SelectedTenantId);

    private async Task OnTenantChanged(ChangeEventArgs e)
    {
        SelectedTenantId = e.Value?.ToString() ?? string.Empty;
        Diagnostics = null;
        LastFixResult = null;
        PendingFix = false;

        if (SelectedTenant is not null)
        {
            await LoadDiagnosticsAsync();
        }
    }

    private async Task LoadDiagnosticsAsync()
    {
        if (SelectedTenant is null)
        {
            return;
        }

        IsDiagnosing = true;
        StateHasChanged();

        Diagnostics = await fechaAnomaliaService.DiagnoseAsync(SelectedTenant, CancellationToken.None);
        IsDiagnosing = false;
    }

    private void RequestFix() => PendingFix = true;
    private void CancelFix() => PendingFix = false;

    private async Task ConfirmFixAsync()
    {
        if (SelectedTenant is null)
        {
            return;
        }

        PendingFix = false;
        IsFixing = true;
        StateHasChanged();

        LastFixResult = await fechaAnomaliaService.FixAllAsync(SelectedTenant, CancellationToken.None);
        IsFixing = false;
        await LoadDiagnosticsAsync();
    }

    private static string FormatFechaStr(string raw)
    {
        raw = raw.Trim();
        var formatted = raw;

        if (raw.Length == 12 &&
            DateTime.TryParseExact(raw, "yyyyMMddHHmm", null,
                System.Globalization.DateTimeStyles.None, out var fecha))
        {
            formatted = fecha.ToString("dd/MM/yyyy HH:mm");
        }

        return formatted;
    }
}
