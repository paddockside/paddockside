using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Paddockside.Infrastructure.Identity;

/// <summary>Lets <c>dotnet ef</c> build the identity model to create migrations. It never connects for that.</summary>
internal sealed class IdentityDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaddocksideIdentityDbContext>
{
    public PaddocksideIdentityDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaddocksideIdentityDbContext>()
            .UseSqlServer("Server=(localdb)\\Paddockside;Database=Paddockside;Integrated Security=true;TrustServerCertificate=true")
            .Options);
}
