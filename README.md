Paddockside — owner-communication platform for the thoroughbred industry. See docs/ for the brief

## Running it locally

Needs the .NET 9 SDK, Node 22+ and SQL Server LocalDB 2025 (installed with Visual Studio 2026).

The app and the isolation tests use their own LocalDB instance, `(localdb)\Paddockside`. Create it once per
machine with the **2025** tool (the SQL driver always uses the newest LocalDB installed, and it cannot start
instances created by an older one, which is what broke the shared `MSSQLLocalDB` here):

```
"C:\Program Files\Microsoft SQL Server\170\Tools\Binn\SqlLocalDB.exe" create Paddockside -s
```

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

This checks contrast and regenerates `wwwroot/tokens.css` and `tailwind.config.js`; `dotnet build` also runs it
whenever `tokens.json` changes, so neither file is ever edited by hand. CI fails if the generated files are
stale or if a hex colour appears in any `.razor`, `.css` or `.cs` file (`node build/check-no-hex-colours.mjs`).

Styling is Tailwind 3, built by `dotnet build` of `Paddockside.Web`: it runs `npm ci` when the lockfile
changes, then builds `wwwroot/css/app.css` from `Styles/app.css` with the generated config, and copies the
self-hosted Figtree font into `wwwroot/fonts`. Both are build output and git-ignored. Every colour class is a
CSS variable, so light and dark need no `dark:` variants. Use the theme's classes (`bg-surface-raised`,
`text-text-muted`, `btn-primary`, …); Tailwind's default palette does not exist here.

## Tests

```
dotnet test
```

`Paddockside.Isolation.Tests` needs SQL Server: LocalDB by default, or set `PADDOCKSIDE_TEST_SQL`.
