using ExistenciasReset.Models;
using ExistenciasReset.Services;

namespace ExistenciasReset.Components.Pages;

public partial class Monitor(FirebirdMonitoringService monitoringService, IReadOnlyList<TenantOptions> tenants)
{
    private List<TenantHealthSummary> Summaries { get; set; } = [];
    private bool IsLoading { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        StateHasChanged();

        var activeTenants = tenants.Where(t => t.Active).ToList();
        var tasks = activeTenants.Select(t => monitoringService.GetHealthSummaryAsync(t, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Summaries = results.ToList();
        IsLoading = false;
    }

    private static string RowClass(TenantHealthSummary summary)
    {
        string cssClass;

        if (!summary.Reachable)
        {
            cssClass = "table-danger";
        }
        else if (summary.LongestRunningSeconds > 300)
        {
            cssClass = "table-danger";
        }
        else if (summary.LongestRunningSeconds > 60)
        {
            cssClass = "table-warning";
        }
        else
        {
            cssClass = string.Empty;
        }

        return cssClass;
    }
}
