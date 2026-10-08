# Mini Online Shop

M1 provides the .NET 10/static shell, SQL Server 2022 and an assertion-running gate. Catalog, authentication, orders and the full UI follow in M2–M5.

Install Docker with Linux containers and Compose, and a POSIX shell. Recommended Docker resources: 8 GB RAM and 15 GB free disk.

```sh
docker compose up --build
```

Open http://localhost:8080/; `/health` checks connectivity to the app database. Stop with `docker compose down`, which preserves the named demo volume. `docker compose down -v` deletes demo data.

```sh
sh scripts/test.sh
```

Run only one gate at a time. If a crash leaves `.test-gate.lock/`, first verify no gate is running, then use `rmdir .test-gate.lock` from the repository root and rerun. The gate builds/runs xUnit unit and real SQL-backed integration tests, then Node assertions. It requires no host SDK, Node or SQL tools. It refuses zero executed tests, propagates failures and removes only its disposable `test-db` service. Integration uses one serialized collection/factory, fixed `test-db,1433` / `ShopTests`, distinct credentials and no volume or published SQL port. The reset guard rejects the runtime database and a non-Testing environment. The M1 shell has no entity rows; FK-ordered row reset and seed belong to M2/M3.

The gate exercises the test image and TestServer; it does not build or start the published runtime image. Runtime delivery was smoke-tested manually at M1 and will be certified again at M6 with `docker compose up --build`, root and health requests, and the browser flow. Automated published-runtime smoke is deferred to future CI work.

With .NET SDK 10.0.401 installed, unit-only development uses `dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj`. All package versions and the EF tool are pinned. NuGet audit is explicitly enabled for direct and transitive dependencies. Advisory warnings remain errors under `TreatWarningsAsErrors`: a newly disclosed vulnerability can intentionally stop restore even without a source change. Review the reported NU1901–NU1904 advisory and update the affected dependency; audit data is an external input to the gate.

Compose accepts `SHOP_DB_SA_PASSWORD`, `SHOP_TEST_DB_SA_PASSWORD` and `SHOP_JWT_SIGNING_KEY` overrides. Defaults are local demo values; SA access and trusting the local SQL certificate are local development trade-offs. Changing a password variable does not rotate the password inside an existing database volume. The JWT key is supplied through the environment; JWT functionality arrives in M2. SQL Server Developer edition is for development/testing; Compose accepts its EULA.

On Apple Silicon, select Docker Desktop's Apple Virtualization framework and enable **Use Rosetta for x86_64/amd64 emulation**; Compose retains `platform: linux/amd64`. This is a practical local workaround. Microsoft does not test/support translated SQL Server environments, and this workaround is untested by the author on Apple Silicon. See [Docker settings](https://docs.docker.com/desktop/settings-and-maintenance/settings/) and [Microsoft prerequisites](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver16&tabs=cli).

The full seeded-user instructions, request examples and trade-off/next-step NOTES are scheduled for M6.
