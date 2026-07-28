using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DysonHarness;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class DysonDbContextFactory : IDesignTimeDbContextFactory<DysonDbContext>
{
    public DysonDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DysonDbContext>();
        DysonSqliteConfigurator.Configure(optionsBuilder, "dyson-design.db");
        return new DysonDbContext(optionsBuilder.Options);
    }
}
