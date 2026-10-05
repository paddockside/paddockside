using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paddockside.Application.Tenancy;

namespace Paddockside.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model to create migrations. It never connects for that; for
/// <c>database update</c>, pass <c>--connection</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaddocksideDbContext>
{
    public PaddocksideDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaddocksideDbContext>()
            .UseSqlServer("Server=(localdb)\\Paddockside;Database=Paddockside;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        return new PaddocksideDbContext(options, new NoTenant());
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
