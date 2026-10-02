Paddockside — owner-communication platform for the thoroughbred industry. See docs/ for the brief

## Running it locally

Needs the .NET 9 SDK, Node 22+ and SQL Server LocalDB (installed with Visual Studio).

```
dotnet run --project src/Paddockside.Api --launch-profile https
```

Then open https://localhost:7175. The first run creates a local database with a demo tenant and one staff
login; the email and password are in `src/Paddockside.Api/appsettings.Development.json` under `DevSeed`.
The first sign-in asks you to set up an authenticator app.

To run against the Azure dev database instead of LocalDB, sign in with `az login` first, then:

```
dotnet run --project src/Paddockside.Api --launch-profile https-azure
```

The first run seeds the same demo tenant there. Azure resources and costs are described in
[infra/README.md](infra/README.md).

## Design tokens

Colours, type and spacing are defined only in `src/Paddockside.Design/tokens.json`. After editing it:

```
node src/Paddockside.Design/build/tokens.mjs
```

This checks contrast and regenerates `wwwroot/tokens.css` and `tailwind.config.js`. CI fails if the
generated files are stale or if a hex colour appears in any `.razor`, `.css` or `.cs` file
(`node build/check-no-hex-colours.mjs`).

## Tests

```
dotnet test
```

`Paddockside.Isolation.Tests` needs SQL Server: LocalDB by default, or set `PADDOCKSIDE_TEST_SQL`.
