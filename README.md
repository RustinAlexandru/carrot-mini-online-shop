# Mini Online Shop

A small, runnable online shop: a .NET 10 minimal API on Microsoft SQL Server 2022 (EF Core migrations, one seeded user, JWT login, a paged and sorted catalog, owned orders with coupons), a draft-expiry background job, and a browser UI built from five native Web Components. Everything starts with one command.

## Quick start

**Prerequisites**

- Docker Desktop or Docker Engine with **Linux containers and Docker Compose** (v2). Nothing else is installed on the host: .NET, Node and SQL Server all run in containers.
- A POSIX shell (`sh`) for the test gate script.
- Memory and disk: Microsoft lists **2 GB of RAM** as the minimum for a SQL Server container. This project was developed with 8 GB assigned to Docker and recommends **8 GB RAM and about 15 GB free disk** (the runtime database, a separate test database and the .NET SDK image). The recommendation is a planning figure, not a measured minimum.
- Intel/AMD machines run everything natively. For Apple Silicon see the [note below](#apple-silicon).

**Run it**

```sh
docker compose up --build
```

Then open <http://localhost:8080/>. `/health` returns `{"status":"healthy"}` once the API can reach its database. The first start pulls the SQL Server image and builds the API, so it takes a few minutes; startup applies the EF migration and inserts the seed data before the API accepts requests.

**Log in**

| | |
| --- | --- |
| Email | `demo@shop.test` |
| Password | `DemoShop123!` |
| Coupons | `SAVE5` (5.00 off), `SAVE10` (10.00 off) |

The seed also creates 12 products (coffee, tea, accessories; one with stock 3 and one with stock 0) and is idempotent: restarting adds nothing.

**Stop it**

```sh
docker compose down      # stops the containers, keeps the named database volume (your data)
docker compose down -v   # also deletes the volume: a destructive reset back to the seed data
```

Compose binds the API to `127.0.0.1:8080` only; SQL Server is reachable only inside the Compose network.

## Using the app

Open <http://localhost:8080/>:

1. Browse the catalog (public, six products per page, **Previous/Next**).
2. Log in with the demo account. The login token is kept **in memory only**.
3. Set a quantity on a product and press **Add to order**; adjust or remove lines in "Your order" and optionally enter `SAVE5` or `SAVE10`.
4. Press **Place order**. The page then shows the order exactly as the server stored it (status, lines, subtotal, discount, total, in USD).
5. Press **Delete order** to remove it. **Log out** drops the session, the draft and the current order.

Reloading the page logs you out and forgets the current order (see [NOTES](#notes)).

The same API can be driven without the UI: [`requests/shop.http`](requests/shop.http) is a short flow (login, products, create with `SAVE5`, get, replace, delete, get again, one failure) written for the VS Code **REST Client** extension (its chained `{{login.response.body.$.token}}` references are that extension's syntax; other HTTP clients need their own response handlers). Plain `curl` works too:

```sh
curl -X POST http://localhost:8080/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"demo@shop.test","password":"DemoShop123!"}'
curl 'http://localhost:8080/products?page=1&pageSize=5&sortBy=price&sortDirection=desc'
```

## API summary

| Route | Auth | Purpose |
| --- | --- | --- |
| `POST /auth/login` `{"email","password"}` | none | 200 `{token, expiresAt}` (JWT, 60 minutes); 401 for a wrong email or password (same answer for both); 400 for blank fields |
| `GET /products?page&pageSize&sortBy&sortDirection` | none | 200 `{items, page, pageSize, totalCount, totalPages}`; `page` ≥ 1 (default 1), `pageSize` 1–50 (default 12), `sortBy` `name` \| `price` \| `createdAt` (default `name`), `sortDirection` `asc` \| `desc` (default `asc`); out-of-range or unlisted values are 400 with field `errors`; values that cannot be parsed (`page=abc`) are a generic 400 |
| `POST /orders` `{"items":[{"productId","quantity"}],"couponCode"}` | bearer | 201 + `Location`; `couponCode` may be omitted |
| `GET /orders/{id}` | bearer | 200 with items (saved SKU, name, unit price), coupon snapshot, `subtotal`, `discount`, `total`, `currency` |
| `PUT /orders/{id}` `{"items":[...],"couponCode":null}` | bearer | 200; replaces the whole order (adds, changes, removes lines); `couponCode` **must be present**, `null` removes the coupon, a missing member is 400 |
| `DELETE /orders/{id}` | bearer | 204 (items are removed with it) |
| `GET /health` | none | 200 when the database is reachable, otherwise 503 |

Rules worth knowing:

- Clients send **product ids and quantities only**. Unit prices come from the catalog; unknown JSON members (prices, totals, user ids, status) are rejected with 400.
- Quantity is 1–10000 per line and may not exceed the product's current stock; an order has 1–50 distinct products. Stock is only *checked*: nothing is reserved or decremented.
- `discount = min(coupon amount, subtotal)`, so a total is never negative. Money is exact `decimal`, USD.
- `GET` returns the prices and coupon amount **saved with the order**; a successful `PUT` re-reads the current catalog and coupon.
- Every order query is scoped by order id **and** the token's user. Another user's order and a missing order both answer **404**.
- Errors are Problem Details (`application/problem+json`). A **400 from validation** (blank login fields, out-of-range paging or sort values, an unknown product or coupon, a quantity outside 1–10000 or above stock, empty or duplicate lines) carries an `errors` map keyed by field, for example `items[0].quantity` or `couponCode`. A **400 from request parsing** (malformed JSON, a wrong type such as `page=abc` or `"quantity":"two"`, an unknown JSON member, or a missing required member such as `couponCode` on `PUT`) is a generic `{"title":"Bad Request","status":400}` without `errors`. 401 is an unauthenticated or invalid token; 409 means an expired order cannot be edited or the order changed underneath you (reload and retry).

## Draft-expiry job

A hosted background service marks abandoned **Draft** orders as **Expired**: every `DraftSweeper:Interval` (default **1 minute**) it expires Drafts whose last update is older than `DraftSweeper:DraftTtl` (default **30 minutes**; an order exactly at the cutoff is still live). Expired orders stay viewable and deletable but cannot be edited (409). The job never deletes orders and never touches stock. Every run logs one line, including empty and failed runs:

```text
Draft sweep completed: cutoff=… scanned=… expired=… conflicts=… skipped=… durationMs=…
```

See it with `docker compose logs api`. Configure it with environment variables `DraftSweeper__Enabled`, `DraftSweeper__Interval` and `DraftSweeper__DraftTtl` (time spans such as `00:00:05`); Compose forwards `SHOP_SWEEPER_ENABLED`, `SHOP_SWEEPER_INTERVAL` and `SHOP_SWEEPER_DRAFT_TTL`. To watch an order expire in about 20 seconds:

```sh
SHOP_SWEEPER_INTERVAL=00:00:05 SHOP_SWEEPER_DRAFT_TTL=00:00:15 docker compose up --build
```

Valid ranges are checked at startup, even when the job is disabled; an out-of-range value stops the API with a message naming the setting: the interval must be between 1 millisecond and about 49.7 days (the .NET timer limit), and the TTL must be positive and at most 100 years.

## Running the tests

```sh
sh scripts/test.sh
```

The **gate** builds a test image, starts a disposable SQL Server (`test-db`, no volume, no published port) and runs, in order: the xUnit unit tests, the xUnit integration tests against that real SQL Server (through the real HTTP pipeline, JWT validation and EF), then the Node assertions for the browser code. It needs no host SDK, Node or SQL tools, fails when any suite fails or when a suite executes zero tests, and ends with a line such as:

```text
Gate passed: unit=101; SQL-backed integration=145; JS=41; failed=0
```

- **Run one gate at a time.** The gate uses a fixed test database (`ShopTests`), guarded by a lock directory. If a crash leaves `.test-gate.lock/` behind, first check that no gate is running, then remove it from the repository root and rerun:

  ```sh
  rmdir .test-gate.lock
  ```

- Integration tests share one SQL fixture and one application host, run serially, and refuse to reset anything but `test-db,1433` / `ShopTests` in the `Testing` environment. The runtime database is never touched.
- With the .NET SDK 10.0.401 installed, the unit tests alone run locally with `dotnet test tests/Shop.UnitTests/Shop.UnitTests.csproj`, and with Node 24 the browser-code tests run with `node --test tests/frontend/store.test.mjs`. The integration tests need the Compose test database, so use the gate for those.
- NuGet audit is on for direct and transitive packages and advisories are errors: a newly published vulnerability can stop restore without a source change. Update the reported package (NU1901–NU1904) and rerun.
- The gate exercises the test image and an in-process test server, not the published runtime image. The published runtime image is checked by hand with the Quick start steps (start, log in, browse, order, restart).

## Architecture

One deployable ASP.NET Core project (`src/Shop.Api`) with folders that own the rules, and two test projects plus one frontend test file.

```text
src/Shop.Api/
  Domain/     Order aggregate (create, replace, expire, totals), OrderPricing, QuantityRules, entities. No EF, no ASP.NET.
  Data/       ShopDbContext, one IEntityTypeConfiguration per entity, migrations, startup seed.
  Features/   Auth (JWT options/issuance/validation, login), Products (query + endpoint), Orders (contracts, service, endpoints).
  Common/     AppError (NotFound/Conflict/Validation), Result<T>, the single HTTP mapping (ApiProblems), persistence exception handler.
  Jobs/       DraftSweeper (timer loop), DraftSweepRunner (one sweep), options.
  wwwroot/    index.html, styles.css, js/api.js (transport), js/store.js (state + async flows),
              js/components/ (shop-app, login-form, product-list, order-editor, order-detail).
tests/        Shop.UnitTests, Shop.IntegrationTests, frontend/store.test.mjs, Shared/ (helpers linked into both .NET projects)
requests/     shop.http
```

How the pieces fit:

- **Business rules live in the domain.** `Order` validates complete input before it mutates anything, owns the item diff, the Draft-only edit rule, the coupon snapshot and the totals; the sweeper reuses `IsAbandoned` and `Expire`. `QuantityRules` is the only C# place that knows the 1–10000 bound. Services return values or `AppError`; only `ApiProblems` turns an error into HTTP.
- **One save per mutation.** An order change is a single `SaveChangesAsync` guarded by the order's SQL Server `rowversion`; a stale write raises a concurrency error, which (like a deadlock victim) becomes a 409.
- **Frontend.** `api.js` is transport only (injected `fetch`, explicit token, structured errors, never touches state). `store.js` owns the token (memory only), the product page, the draft, the current order and every async flow, tagging each operation with a *session generation* so a response from an older session can neither fill nor log out a newer one, and keeping only the newest requested product page. `shop-app` coordinates; the four other components receive properties and emit bubbling, composed events and never import the store or the API. Server totals are displayed, never computed in the browser.
- **Configuration** is environment-driven: `ConnectionStrings__Shop`, `Jwt__SigningKey` (at least 32 bytes, never stored in `appsettings.json`; the API refuses to start without a valid one) and `DraftSweeper__*`. Compose accepts `SHOP_DB_SA_PASSWORD`, `SHOP_TEST_DB_SA_PASSWORD`, `SHOP_JWT_SIGNING_KEY` and the sweeper variables above; the defaults are local demo values. Changing a database password variable does not rotate the password inside an existing volume.

## Apple Silicon

SQL Server's Linux container image is an x86-64 image, and Microsoft documents it for x86-64 hosts. On Apple Silicon, select Docker Desktop's *Apple Virtualization framework* and enable **Use Rosetta for x86_64/amd64 emulation**; Compose keeps `platform: linux/amd64`. This is a practical local workaround, **Microsoft does not test or support translated SQL Server environments, and it has not been verified by the author**: this project was only run on an Intel Mac. See Docker's [settings](https://docs.docker.com/desktop/settings-and-maintenance/settings/) and Microsoft's [container prerequisites](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver16&tabs=cli).

## NOTES

### Decisions and trade-offs

- **Database: SQL Server 2022 (Developer edition) in Docker**, pinned to `2022-CU23-ubuntu-22.04` for both the runtime and the test database. Compose accepts the Developer-edition EULA. The `sa` login and trusting the container's self-signed certificate are local-development shortcuts; do not copy them to production.
- **EF Core migrations run at startup**, followed by an insert-missing seed keyed by email / SKU / coupon code. It never overwrites existing rows. One migration (`InitialShopSchema`) holds the whole schema, including check constraints (non-negative prices and stock, a positive coupon amount, an all-or-none coupon snapshot on orders).
- **Case handling:** the database collation is case-insensitive (`SQL_Latin1_General_CP1_CI_AS`); emails are trimmed and lower-cased, SKUs and coupon codes upper-cased before lookup, so `Demo@Shop.Test` and `save5` work.
- **The catalog is public.** The task requires authentication on the order endpoints; products have no per-user data, so `GET /products` needs no token and the page shows products before login. Ordering requires a login.
- **The JWT lives in memory only.** There is no `localStorage`, cookie or refresh token, so a page reload logs you out, and because the UI only knows the order it just created, **the current order cannot be recovered after a reload** (the API can still return it by id). Tokens last 60 minutes; the signature, algorithm (HS256 only), issuer, audience, expiry and a GUID subject are all validated, and an expired session shows a message and clears the draft.
- **Prices are server-side snapshots.** An order stores each line's SKU, name and unit price and the coupon's code and amount. A later catalog or coupon change does not alter a stored order on `GET`; the next successful `PUT` refreshes them.
- **Coupons** are two fixed amounts (`SAVE5`, `SAVE10`) with no expiry, usage limits or minimum spend; the discount is capped at the subtotal.
- **Stock is a per-order ceiling, not a reservation.** An order line cannot exceed the product's current stock when it is written, but a draft does not reserve or decrement stock, so two drafts can each ask for the last item. There is no checkout, payment or inventory movement.
- **Deleting is a hard delete** (the order and its lines are removed), while abandoned drafts *expire* (kept, read-only) rather than being deleted by the job.
- **Quantity limits are mirrored in JavaScript** (`store.js`, 1–10000) purely for convenience; `QuantityRules` in the domain is the authority and the API rejects anything else.
- **Concurrency.** Each order change is one atomic save guarded by `rowversion`, with the order's `UpdatedAt` forced as modified so even child-only edits are checked. Two stale writers, or a stale writer after a delete, get **409**. EF can insert order lines *before* the guarded header update, so such a writer can first hit a unique-index error (SQL 2601) or a foreign-key error (547); only those errors raised by order lines, and only after re-reading the order (by id and user) proves it was deleted or changed, are translated to 409. Other constraint errors stay server errors. A SQL deadlock victim (1205) is also a 409.
- **Time-based behaviour uses an injected clock.** The sweeper, token issuance and the tests share `TimeProvider`.
- **Single instance.** One API process is assumed (the sweeper has no distributed lock). Money is USD only.
- **No build step for the UI.** Plain ES modules, no framework, no npm install; the browser tests run on Node's built-in test runner with a tiny in-file DOM host.

### What was skipped (and why)

The scope was an 8-hour task, so these were consciously cut. Each is listed as a possible next step.

- **F01 — Rich coupon policy:** percentage coupons, type/active/start/end window/minimum-spend fields, a `WELCOME10`-style code and a percentage rounding rule. Fixed `SAVE5`/`SAVE10` amounts with a cap at the subtotal are enough for now.
- **F02 — Collation edge tests:** accent, name-tie and per-key duplicate-variant tests. Explicit collation and canonicalisation plus mixed-case login and coupon tests cover the realistic risk.
- **F03 — Schema-metadata assertions:** compatibility level, column types and rowversion metadata checks. Real migrations, a no-pending-model-changes test and behaviour tests stand in.
- **F04 — Full malformed-input matrix:** every challenge and error-body shape. There is one malformed-body test, one unknown-member test, explicit DTO checks and the `AppError` status mapping.
- **F05 — Dedicated issuer/audience rejection tests and overflow-offset cases:** real full JWT validation (good, bad, missing, forged, expired, bad subject) and safe bounded paging with every sort field/direction are tested instead.
- **F06 — Synchronised concurrent-request race suite,** including edit/delete interleavings across real connections. A deterministic two-context stale-write test with exact surviving state, deterministic delete and add-only interleavings, and an HTTP 409 mapping test cover the behaviour.
- **F07 — Hosted-loop cancellation/recovery and edit-versus-sweep race suites.** Normal cancellation handling, one configured-tick test on a fake clock, old/recent/cutoff behaviour, conflict handling and run logging are tested.
- **F08 — Order-ID lookup UI, persistent retrieval/history and reload recovery** (a saved-order list). The UI shows and deletes the order it just created; `GET /orders/{id}` exists but there is no "list my orders" endpoint.
- **F09 — Product sort control in the UI.** The API sorts by name, price and creation date in both directions (tested); the UI shows the default name order.
- **F10 — A standalone `api.test.mjs` and a broader split frontend test harness.** One `store.test.mjs` runs the real `api.js` against a fake `fetch`, the real store and the real components in a small DOM host.
- **F11 — Per-run test database names and image digest pinning,** needed for parallel CI and stronger supply-chain reproducibility. A fixed `ShopTests` database on an isolated container, a one-run-at-a-time rule and a pinned version tag are used.
- **F12 — Certifying more than one startup command.** Only `docker compose up --build`, with a restart and a browser pass, is certified.
- **F13 — Explicit transactions, header-first two-phase writes and an `OrderWriter`** were judged unnecessary; reassess only if a workflow needs several saves. One guarded `SaveChangesAsync` per mutation is used.

Also not built: payments and checkout, registration/password reset, admin product management, multi-currency, user-facing order history or editing in the UI, automated browser tests, CI, and production hardening (secrets management, TLS termination, rate limiting, observability).

### What would come next

1. A "my orders" endpoint and UI (list, reopen, edit a saved Draft), which would also fix the lost-on-reload limitation.
2. Reservation or decrement of stock at checkout, with a real checkout state and payment integration.
3. Automated browser tests and a CI pipeline that also builds and starts the published runtime image (today that is a manual check).
4. A secrets story (no demo defaults, a non-`sa` database login, a trusted certificate), refresh tokens or cookies, and rate limiting on login.
5. The coupon policy of F01, a catalog sort control, and persisted idempotent order creation (an idempotency key) so a retried POST cannot duplicate an order.
6. A distributed lock or leader election for the sweeper if the API ever runs on more than one instance.
