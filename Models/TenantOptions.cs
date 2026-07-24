namespace ExistenciasReset.Models;

public class TenantOptions
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Domain { get; set; }
    public bool Active { get; set; }
    public string ConnectionString { get; set; }
}
