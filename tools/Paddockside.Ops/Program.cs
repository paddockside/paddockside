using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Paddockside.Infrastructure.Identity;

// Paddockside operator tasks that must never be possible from inside the app (identity-access.md §7).
//
//   dotnet run --project tools/Paddockside.Ops -- list-operators
//   dotnet run --project tools/Paddockside.Ops -- grant-operator you@example.com
//   dotnet run --project tools/Paddockside.Ops -- revoke-operator you@example.com
//
// It connects to the dev database as you (az login), with the connection string from Key Vault; set
// ConnectionStrings__Paddockside to point it elsewhere. Granting needs an existing Paddockside sign-in; after the
// first operator exists, invite others from the operator console instead.

Environment.SetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS", "dev");
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddAzureKeyVault(new Uri(Environment.GetEnvironmentVariable("PADDOCKSIDE_KEYVAULT") ?? "https://kv-paddockside-dev-cd63.vault.azure.net/"), new DefaultAzureCredential())
    .Build();
var connection = config.GetConnectionString("Paddockside") ?? throw new InvalidOperationException("No ConnectionStrings:Paddockside.");

await using var identity = new PaddocksideIdentityDbContext(
    new DbContextOptionsBuilder<PaddocksideIdentityDbContext>().UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options);

switch (args)
{
    case ["list-operators"]:
        foreach (var email in await identity.Users.Where(p => p.IsOperator).Select(p => p.Email).ToListAsync())
            Console.WriteLine(email);
        return 0;

    case ["grant-operator" or "revoke-operator", var address]:
        var grant = args[0] == "grant-operator";
        var person = await identity.Users.SingleOrDefaultAsync(p => p.NormalizedEmail == address.Trim().ToUpperInvariant());
        if (person is null)
        {
            Console.Error.WriteLine($"No Paddockside sign-in for {address}. Accept any invitation with it first, or have an operator invite it from the console.");
            return 1;
        }

        if (!grant && person.IsOperator && await identity.Users.CountAsync(p => p.IsOperator) == 1)
        {
            Console.Error.WriteLine("That is the last operator; grant someone else first.");
            return 1;
        }

        person.IsOperator = grant;
        person.SecurityStamp = Guid.NewGuid().ToString(); // their sessions pick up the change at the next check (within a minute)
        await identity.SaveChangesAsync();
        Console.WriteLine($"{address} is {(grant ? "now" : "no longer")} an operator.");
        return 0;

    default:
        Console.Error.WriteLine("Usage: list-operators | grant-operator <email> | revoke-operator <email>");
        return 2;
}
