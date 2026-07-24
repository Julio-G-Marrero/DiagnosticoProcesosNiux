using ExistenciasReset.Components;
using ExistenciasReset.Models;
using ExistenciasReset.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var tenants = builder.Configuration.GetSection("Tenants").Get<List<TenantOptions>>() ?? [];
builder.Services.AddSingleton<IReadOnlyList<TenantOptions>>(tenants);
builder.Services.AddScoped<FirebirdMonitoringService>();
builder.Services.AddScoped<ExistenciasResetService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
