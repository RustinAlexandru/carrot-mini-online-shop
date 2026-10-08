# Mini Online Shop

The .NET 10 API, SQL Server 2022, an EF migration with an idempotent seed, JWT login, the paged/sorted catalog, the owned-order API and an assertion-running gate are in place. The draft-expiry job is in place; the full UI follows in M5.

Install Docker with Linux containers and Compose, and a POSIX shell. Recommended Docker resources: 8 GB RAM and 15 GB free disk.

```sh
docker compose up --build
```

Open http://localhost:8080/; `/health` checks connectivity to the app database. Startup applies the migration and inserts any missing seed data (user `demo@shop.test` / `DemoShop123!`, 12 products, coupons SAVE5 and SAVE10).

```sh
# Log in as the seeded user; the bearer token is what the order endpoints require.
curl -X POST http://localhost:8080/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"demo@shop.test","password":"DemoShop123!"}'
# The catalog is public.
curl 'http://localhost:8080/products?page=1&pageSize=5&sortBy=price&sortDirection=desc'
```

`/products` accepts `page`, `pageSize` (1-50), `sortBy` (`name`, `price`, `createdAt`) and `sortDirection` (`asc`, `desc`) and needs no token; only the order endpoints require one.

Orders belong to the logged-in user and all four routes need `Authorization: Bearer <token>`:

| Request | Result |
| --- | --- |
| `POST /orders` `{"items":[{"productId":"<guid>","quantity":2}],"couponCode":"SAVE5"}` | 201 + `Location`; `couponCode` may be omitted |
| `GET /orders/{id}` | 200 with items, coupon snapshot, `subtotal`, `discount`, `total`, `currency` (USD) |
| `PUT /orders/{id}` `{"items":[...],"couponCode":null}` | 200; replaces the whole order (adds, changes, removes lines); `couponCode` is required, `null` removes the coupon |
| `DELETE /orders/{id}` | 204 |

Clients send product ids and quantities only; prices come from the catalog. GET shows the prices and coupon amount saved with the order, and a successful PUT refreshes them from the current catalog. The discount is the coupon amount capped at the subtotal. Another user's order and a missing order both return 404; invalid input is 400 (Problem Details with field errors); an edit of an expired order or a stale concurrent write is 409 (reload and retry). Unknown JSON members such as prices or totals are rejected.

A background sweeper expires abandoned Draft orders: every `DraftSweeper:Interval` (default 1 minute) it marks Drafts last updated more than `DraftSweeper:DraftTtl` (default 30 minutes) ago as `Expired`. Expired orders stay viewable and deletable but cannot be edited (409); the sweeper never deletes orders or changes stock, and every run logs one line (`Draft sweep completed: cutoff=… scanned=… expired=… conflicts=… skipped=… durationMs=…`). Configure it with `DraftSweeper__Enabled`, `DraftSweeper__Interval` and `DraftSweeper__DraftTtl` (time spans such as `00:00:05`); Compose forwards `SHOP_SWEEPER_ENABLED`, `SHOP_SWEEPER_INTERVAL` and `SHOP_SWEEPER_DRAFT_TTL`, for example `SHOP_SWEEPER_INTERVAL=00:00:05 SHOP_SWEEPER_DRAFT_TTL=00:00:15 docker compose up --build` to watch an order expire after about 20 seconds. Non-positive values stop the API at startup.

Stop with `docker compose down`, which preserves the named demo volume. `docker compose down -v` deletes demo data.

```sh
sh scripts/test.sh
```

Run only one gate at a time. If a crash leaves `.test-gate.lock/`, first verify no gate is running, then use `rmdir .test-gate.lock` from the repository root and rerun. The gate builds/runs xUnit unit and real SQL-backed integration tests, then Node assertions. It requires no host SDK, Node or SQL tools. It refuses zero executed tests, propagates failures and removes only its disposable `test-db` service. Integration uses one serialized collection/factory, fixed `test-db,1433` / `ShopTests`, distinct credentials and no volume or published SQL port. The reset guard rejects the runtime database and a non-Testing environment. Test classes that need known data call the fixture's FK-ordered reset, which deletes all rows and re-runs the seed (migration history is kept).

The gate exercises the test image and TestServer; it does not build or start the published runtime image. Runtime delivery was smoke-tested manually at M1 and will be certified again at M6 with `docker compose up --build`, root and health requests, and the browser flow. Automated published-runtime smoke is deferred to future CI work.

With .NET SDK 10.0.401 installed, unit-only development uses `dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj`. All package versions and the EF tool are pinned. NuGet audit is explicitly enabled for direct and transitive dependencies. Advisory warnings remain errors under `TreatWarningsAsErrors`: a newly disclosed vulnerability can intentionally stop restore even without a source change. Review the reported NU1901–NU1904 advisory and update the affected dependency; audit data is an external input to the gate.

Compose accepts `SHOP_DB_SA_PASSWORD`, `SHOP_TEST_DB_SA_PASSWORD` and `SHOP_JWT_SIGNING_KEY` overrides. Defaults are local demo values; SA access and trusting the local SQL certificate are local development trade-offs. Changing a password variable does not rotate the password inside an existing database volume. The JWT signing key (at least 32 bytes) is supplied through the environment (`Jwt__SigningKey`) and is never stored in appsettings; startup fails with a clear message when it is missing or too short. SQL Server Developer edition is for development/testing; Compose accepts its EULA.

On Apple Silicon, select Docker Desktop's Apple Virtualization framework and enable **Use Rosetta for x86_64/amd64 emulation**; Compose retains `platform: linux/amd64`. This is a practical local workaround. Microsoft does not test/support translated SQL Server environments, and this workaround is untested by the author on Apple Silicon. See [Docker settings](https://docs.docker.com/desktop/settings-and-maintenance/settings/) and [Microsoft prerequisites](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver16&tabs=cli).

The full seeded-user instructions, request examples and trade-off/next-step NOTES are scheduled for M6.
