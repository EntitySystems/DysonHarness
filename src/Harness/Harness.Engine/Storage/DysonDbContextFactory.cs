using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DysonHarness;

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
public sealed class DysonDbContextFactory : IDesignTimeDbContextFactory<DysonDbContext>
{
    public DysonDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DysonDbContext>()
            .UseSqlite("Data Source=dyson-design.db")
            .Options;

        return new DysonDbContext(options);
    }
}
