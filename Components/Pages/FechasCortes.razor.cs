using ExistenciasReset.Models;
using ExistenciasReset.Services;
using Microsoft.AspNetCore.Components;

namespace ExistenciasReset.Components.Pages;

public partial class FechasCortes(FechasCortesService fechasCortesService, IReadOnlyList<TenantOptions> tenants)
{
    private string SelectedTenantId { get; set; } = string.Empty;
    private int Dias { get; set; } = 30;
    private FechasCortesDiagnostics? Diagnostics { get; set; }
    private bool IsDiagnosing { get; set; }

    private IReadOnlyList<TenantOptions> ActiveTenants =>
        tenants.Where(t => t.Active).OrderBy(t => t.Name).ToList();

    private TenantOptions? SelectedTenant =>
        tenants.FirstOrDefault(t => t.Id == SelectedTenantId);

    private async Task OnTenantChanged(ChangeEventArgs e)
    {
        SelectedTenantId = e.Value?.ToString() ?? string.Empty;
        Diagnostics = null;

        if (SelectedTenant is not null)
        {
            await LoadDiagnosticsAsync();
        }
    }

    private async Task OnDiasChanged(ChangeEventArgs e)
    {
        Dias = int.TryParse(e.Value?.ToString(), out var dias) ? dias : 30;

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

        Diagnostics = await fechasCortesService.DiagnoseAsync(SelectedTenant, Dias, CancellationToken.None);
        IsDiagnosing = false;
    }

    private static string Dinero(decimal value) => value.ToString("N2");

    private static string Signo(decimal value) => value > 0 ? $"+{value:N2}" : value.ToString("N2");
}
