using ExistenciasReset.Models;
using ExistenciasReset.Services;
using Microsoft.AspNetCore.Components;

namespace ExistenciasReset.Components.Pages;

public partial class Home(ExistenciasResetService resetService, FirebirdMonitoringService monitoringService, IReadOnlyList<TenantOptions> tenants)
{
    private List<ResetStepResult> Steps { get; set; } = [];
    private ResetResult Result { get; set; }
    private TenantDiagnostics Diagnostics { get; set; }
    private List<StatementInfo> ActiveStatements { get; set; } = [];
    private string SelectedTenantId { get; set; } = string.Empty;
    private bool ConfirmedBackup { get; set; }
    private bool IsRunning { get; set; }
    private bool IsDiagnosing { get; set; }
    private int? PendingDisconnectId { get; set; }
    private bool PendingBulkDisconnect { get; set; }
    private int? PendingCancelStatementId { get; set; }

    [SupplyParameterFromQuery(Name = "tenant")]
    public string TenantQueryParam { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (!string.IsNullOrEmpty(TenantQueryParam) && tenants.Any(t => t.Id == TenantQueryParam))
        {
            SelectedTenantId = TenantQueryParam;
            await LoadDiagnosticsAsync();
        }
    }

    private int StaleAttachmentCount =>
        Diagnostics is null ? 0 : Diagnostics.ActiveAttachments.Count(a => a.ConnectedAt.Date < DateTime.Today);

    private IReadOnlyList<TenantOptions> ActiveTenants =>
        tenants.Where(t => t.Active).OrderBy(t => t.Name).ToList();

    private TenantOptions SelectedTenant =>
        tenants.FirstOrDefault(t => t.Id == SelectedTenantId);

    private string DatabasePath
    {
        get
        {
            var databasePart = SelectedTenant.ConnectionString
                .Split(';')
                .FirstOrDefault(p => p.TrimStart().StartsWith("Database=", StringComparison.OrdinalIgnoreCase));

            return databasePart is null ? string.Empty : databasePart.Split('=', 2)[1];
        }
    }

    private async Task OnTenantChanged(ChangeEventArgs e)
    {
        SelectedTenantId = e.Value?.ToString() ?? string.Empty;
        ConfirmedBackup = false;
        Steps = [];
        Result = null;
        Diagnostics = null;
        ActiveStatements = [];
        PendingDisconnectId = null;
        PendingCancelStatementId = null;

        if (SelectedTenant is not null)
        {
            await LoadDiagnosticsAsync();
        }
    }

    private void OnConfirmedBackupChanged(ChangeEventArgs e)
    {
        ConfirmedBackup = e.Value is bool value && value;
    }

    private async Task LoadDiagnosticsAsync()
    {
        IsDiagnosing = true;
        StateHasChanged();

        Diagnostics = await resetService.DiagnoseAsync(SelectedTenant, CancellationToken.None);
        ActiveStatements = (await monitoringService.GetActiveStatementsAsync(SelectedTenant, CancellationToken.None)).ToList();
        IsDiagnosing = false;
    }

    private void RequestForceDisconnect(int attachmentId)
    {
        PendingDisconnectId = attachmentId;
    }

    private void CancelForceDisconnect()
    {
        PendingDisconnectId = null;
    }

    private async Task ConfirmForceDisconnectAsync(int attachmentId)
    {
        PendingDisconnectId = null;
        await monitoringService.ForceDisconnectAsync(SelectedTenant, attachmentId, CancellationToken.None);
        await LoadDiagnosticsAsync();
    }

    private void RequestBulkDisconnect()
    {
        PendingBulkDisconnect = true;
    }

    private void CancelBulkDisconnect()
    {
        PendingBulkDisconnect = false;
    }

    private async Task ConfirmBulkDisconnectAsync()
    {
        PendingBulkDisconnect = false;
        await monitoringService.ForceDisconnectOlderThanAsync(SelectedTenant, DateTime.Today, CancellationToken.None);
        await LoadDiagnosticsAsync();
    }

    private void RequestCancelStatement(int statementId)
    {
        PendingCancelStatementId = statementId;
    }

    private void DismissCancelStatement()
    {
        PendingCancelStatementId = null;
    }

    private async Task ConfirmCancelStatementAsync(int statementId)
    {
        PendingCancelStatementId = null;
        await monitoringService.CancelStatementAsync(SelectedTenant, statementId, CancellationToken.None);
        await LoadDiagnosticsAsync();
    }

    private string ErrorHint => Result is null ? string.Empty : GetErrorHint(Result.ErrorMessage);

    private static string FormatDuration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        string formatted;

        if (span.TotalMinutes >= 1)
        {
            formatted = $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
        else
        {
            formatted = $"{span.TotalSeconds:0.0}s";
        }

        return formatted;
    }

    private static string GetErrorHint(string errorMessage)
    {
        string hint;

        if (errorMessage.Contains("lock conflict", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("lock time-out", StringComparison.OrdinalIgnoreCase))
        {
            hint = "Otra conexión activa tiene la tabla bloqueada. Revisa la lista de conexiones/consultas de arriba y pide que cierren la pantalla de Inventario/Existencias, o cancela/desconecta la sesión responsable.";
        }
        else if (errorMessage.Contains("RDB$INDICES", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("RDB$INDEX_5", StringComparison.OrdinalIgnoreCase))
        {
            hint = "Ya existe un objeto con ese nombre en una transacción sin confirmar de otra sesión SQL (ISQL, FlameRobin, DBeaver, otra pestaña de esta app, etc.). Cierra esa sesión o haz COMMIT/ROLLBACK en ella y vuelve a intentar.";
        }
        else
        {
            hint = string.Empty;
        }

        return hint;
    }

    private async Task RunResetAsync()
    {
        IsRunning = true;
        Steps = [];
        Result = null;
        StateHasChanged();

        var result = await resetService.ResetAsync(SelectedTenant, CancellationToken.None);

        Steps = result.Steps.ToList();
        Result = result;
        IsRunning = false;

        await LoadDiagnosticsAsync();
    }
}
