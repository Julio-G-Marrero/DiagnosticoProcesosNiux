using ExistenciasReset.Models;
using ExistenciasReset.Services;
using Microsoft.AspNetCore.Components;

namespace ExistenciasReset.Components.Pages;

public partial class CargaOrden(CargaOrdenService cargaOrdenService, IReadOnlyList<TenantOptions> tenants)
{
    private string SelectedTenantId { get; set; } = string.Empty;
    private CargaOrdenDiagnostics? Diagnostics { get; set; }
    private bool IsDiagnosing { get; set; }
    private bool PendingFixOrden { get; set; }
    private bool PendingFixEmp { get; set; }
    private bool IsFixing { get; set; }
    private CargaOrdenFixResult? LastFixResult { get; set; }

    private IReadOnlyList<TenantOptions> ActiveTenants =>
        tenants.Where(t => t.Active).OrderBy(t => t.Name).ToList();

    private TenantOptions? SelectedTenant =>
        tenants.FirstOrDefault(t => t.Id == SelectedTenantId);

    private async Task OnTenantChanged(ChangeEventArgs e)
    {
        SelectedTenantId = e.Value?.ToString() ?? string.Empty;
        Diagnostics = null;
        LastFixResult = null;
        PendingFixOrden = false;
        PendingFixEmp = false;

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

        Diagnostics = await cargaOrdenService.DiagnoseAsync(SelectedTenant, CancellationToken.None);
        IsDiagnosing = false;
    }

    private void RequestFixOrden() => PendingFixOrden = true;
    private void CancelFixOrden() => PendingFixOrden = false;
    private void RequestFixEmp() => PendingFixEmp = true;
    private void CancelFixEmp() => PendingFixEmp = false;

    private async Task ConfirmFixOrdenAsync()
    {
        if (SelectedTenant is null)
        {
            return;
        }

        PendingFixOrden = false;
        IsFixing = true;
        StateHasChanged();

        LastFixResult = await cargaOrdenService.FixCargaAOrdenAsync(SelectedTenant, CancellationToken.None);
        IsFixing = false;
        await LoadDiagnosticsAsync();
    }

    private async Task ConfirmFixEmpAsync()
    {
        if (SelectedTenant is null)
        {
            return;
        }

        PendingFixEmp = false;
        IsFixing = true;
        StateHasChanged();

        LastFixResult = await cargaOrdenService.FixCargaAEmpAsync(SelectedTenant, CancellationToken.None);
        IsFixing = false;
        await LoadDiagnosticsAsync();
    }
}
