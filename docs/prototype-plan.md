# Barebone Builder prototype — plan and contracts

This file is the **shared contract** for everyone (humans and Devin sub-sessions) building the prototype.
If code and this file disagree, fix the code or update this file in the same PR.

Goal: prove that a shared, security-first platform (`platform/`) lets a new internal app ship by adding only
`apps/<slug>/` (manifest, policy, entities, endpoints, pages). Local and runnable; no Azure.

## 1. Folder layout and ownership

```
Devin.slnx                         solution (root)
Directory.Build.props              net10.0, Nullable, TreatWarningsAsErrors, analyzers
.editorconfig  global.json  .gitignore  .gitleaks.toml
package.json  tsconfig.json  eslint.config.js  vitest.config.ts   (single npm project at repo root)
config/dev-directory.json          seeded dev users + roles (stand-in for Entra groups)
platform/
  backend/Platform/                Barebone.Platform class library (the SDK)            [owner: platform-backend]
    Abstractions/                  PUBLIC API apps may use (written in the plan commit; extend, don't break)
    policy.yaml                    platform's own policy (catalog/audit actions)
  host/                            Barebone.Platform.Host web app (BFF). Discovers apps; serves SPA [owner: platform-backend]
  web/                             React shell + SPA entry (index.html, main.tsx, vite.config.ts)  [owner: platform-web]
    src/sdk/                       PUBLIC TS API apps may use (written in the plan commit; extend, don't break)
apps/
  barebone-builder/                the Builder portal, itself an app on the platform                 [owner: builder-app]
    platform.yaml  policy.yaml  backend/Barebone.Builder.csproj  web/index.tsx  templates/
  kyc-case-reviewer/               the demo app                                                      [owner: kyc-app]
    platform.yaml  policy.yaml  backend/Kyc.CaseReviewer.csproj  web/index.tsx
tests/
  Platform.Tests/                  xUnit backend tests (incl. builder + KYC behaviour via HTTP)      [owner: tests]
  web/                             Vitest + React Testing Library tests                              [owner: tests]
scripts/                           dev.sh, scaffold-app.sh, tamper-audit.py                           [owner: ci-docs]
.github/workflows/ci.yml  README.md                                                                  [owner: ci-docs]
```

**Rule:** apps never edit `platform/`. Apps never reference each other. `platform/` contains no app-specific
(e.g. KYC) code or strings. `platform/host` discovers apps without edits (see 3.1 and 5.1).

## 2. Stack

- .NET 10 minimal APIs, EF Core 10 + SQLite (`Microsoft.EntityFrameworkCore.Sqlite`), YamlDotNet, Bogus (inside platform only),
  OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `...Instrumentation.AspNetCore`, `...Exporter.Console`).
- React 19 + TypeScript + Vite + react-router v7 + Fluent UI v9 (`@fluentui/react-components`).
- Tests: xUnit + `Microsoft.AspNetCore.Mvc.Testing`; Vitest + @testing-library/react + jsdom.
- Ports: backend `http://localhost:5080`; Vite dev server `http://localhost:5173` proxies `/api` and `/health` to 5080.
  For the demo, `npm run build` writes the SPA to `platform/host/wwwroot/` (gitignored) and the host serves it on 5080
  (single origin, real CSP).

## 3. Backend architecture

### 3.1 App discovery (no host edits per app)
- `platform/host/Barebone.Platform.Host.csproj` has `<ProjectReference Include="../../apps/*/backend/*.csproj" />` (glob).
- Each app csproj copies `../platform.yaml` and `../policy.yaml` to output as `apps/<slug>/platform.yaml|policy.yaml`.
- At startup the platform scans `{Platform:AppsDirectory}` (default `AppContext.BaseDirectory/apps`) for `*/platform.yaml`,
  validates each manifest (fail closed: any invalid manifest or policy => host throws and does not start), loads
  `backend.assembly` and instantiates the single public `IPlatformApp` implementation in it.
- Per app the platform: adds its entities to the shared model (`ConfigureModel`), creates the route group
  `/api/apps/{slug}` with the platform filters, and calls `MapEndpoints(group)`.

### 3.2 Request pipeline (order matters)
1. Exception handler: generic RFC 7807 problem JSON `{type,title,status,correlationId}`; never stack traces.
2. Security headers on every response:
   `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'`,
   `Strict-Transport-Security: max-age=31536000; includeSubDomains`, `X-Content-Type-Options: nosniff`,
   `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Cross-Origin-Opener-Policy: same-origin`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`.
   (`'unsafe-inline'` only for styles: Fluent UI/Griffel injects style tags. Scripts never.)
3. Correlation id: accept `X-Correlation-Id` if it matches `^[A-Za-z0-9-]{8,64}$`, else generate; echo it in the response
   header, put it on the current Activity, in the log scope and in audit rows.
4. Session (BFF): cookie `__Host-session` (HttpOnly, Secure, SameSite=Strict, Path=/), value = 256-bit random id;
   server-side store behind `ISessionStore` (in-memory). Idle timeout `Platform:Session:IdleMinutes` (default 30),
   absolute `Platform:Session:AbsoluteHours` (default 8). Expired => treated as anonymous. Uses injected `TimeProvider`.
5. CSRF: every non-GET/HEAD/OPTIONS request must carry header `X-CSRF-Token` equal to the token bound to the session
   (or, before login, to the anonymous pre-session cookie `__Host-csrf`). Mismatch/missing => 403
   `{title:"CSRF token missing or invalid"}`. Token is returned by `GET /api/session`. Client re-fetches after login/logout.
6. Rate limiting (built-in limiter): login `Platform:RateLimits:LoginPerMinute` (default 20, per IP);
   mutations `Platform:RateLimits:MutationsPerMinute` (default 120, per user). 429 problem JSON.
7. App route group `/api/apps/{slug}` filters, in this order:
   a. authenticated, else 401;
   b. **stage gate** (see 3.4), else 403 + audit row (outcome `denied`);
   c. per-endpoint `RequireAction(...)` authorization, else 403 + audit row (outcome `denied`);
   d. **idempotency** for POST/PUT/PATCH/DELETE (see 3.6);
   e. handler.
   Every app endpoint MUST declare `RequireAction` (an endpoint without it is denied: deny by default).

### 3.3 Identity
- `IIdentityProvider` seam: `DevIdentityProvider` reads `config/dev-directory.json` (copied to host output).
  Entra ID OIDC replaces it later; the BFF/session model stays.
- Seeded users (ids are stable; names are fake):

| id | display | roles |
|---|---|---|
| `u-alice` | Alice Ops | Requester |
| `u-bob` | Bob Builder | Builder |
| `u-bianca` | Bianca Builder | Builder |
| `u-carol` | Carol Analyst | KYC Analyst |
| `u-dan` | Dan Analyst | KYC Analyst |
| `u-erin` | Erin Senior | KYC Senior Reviewer |
| `u-grace` | Grace Dual | KYC Analyst, KYC Senior Reviewer |
| `u-frank` | Frank Auditor | Auditor |
| `u-mallory` | Mallory Outsider | (none) |

### 3.4 Staged access (platform-enforced, from the **catalog**, not the file)
- App not registered in the catalog => 403 `app-not-registered` (audited).
- `builder-only`: only users with role `Builder`.
- `demo`: `Builder`s and the app's `requesterId` (from the approved intake).
- `live`: only users in the catalog `accessList` (user ids). `"*"` means any authenticated user and is only allowed for
  `dataClassification: internal|public` apps.
- When admitted the platform adds **pseudo-roles** for policy evaluation: `platform:builder` (admitted as Builder in
  builder-only/demo) and `platform:demo-viewer` (admitted as requester in demo). Apps may grant these read-only actions.
- `GET /api/platform/apps/{slug}/access` => `{ slug, stage, allowed, reason }` (UI uses it; denials audited).

### 3.5 Authorization (deny by default)
- Per-app `policy.yaml` (validated at startup, fail closed):

```yaml
app: kyc-case-reviewer
rules:
  - action: case.view
    effect: allow            # allow | deny ; any matching deny wins
    roles: [KYC Analyst]
    when:                    # optional; allOf / anyOf of conditions
      anyOf:
        - { attr: resource.assignedAnalystId, op: eq, value: $user.id }
        - { attr: resource.assignedAnalystId, op: isNull }
  - action: case.decide
    effect: deny
    roles: ["*"]
    when: { allOf: [ { attr: resource.preparedBy, op: eq, value: $user.id } ] }
    reason: maker-checker    # shown in the 403 and the audit row
```
  ops: `eq`, `neq`, `in`, `notIn`, `isNull`, `notNull`. `value` is a literal, a list, or `$user.id`.
  `roles: ["*"]` = any authenticated user. No rule matched => deny (`no-matching-rule`).
- `IAuthorizer.Authorize(subject, appSlug, action, resource)` is pure/in-memory; the `RequireAction` filter calls it and
  audits denials. Apps may call `Authorize` to filter lists (no audit for filtered rows).
- Platform's own `platform/backend/Platform/policy.yaml` covers `catalog.read` (any user), `audit.read` and
  `audit.verify` (Auditor, Builder).

### 3.6 UnitOfWork, idempotency, outbox, audit (one SQLite transaction)
- `IUnitOfWork.ExecuteAsync(context, work)` opens one transaction (SQLite `BEGIN IMMEDIATE`, plus a process lock), runs
  `work(scope)`, then in the same transaction writes: the business changes, **one audit row** (before/after diff of
  changed entities, masked), the **idempotency record** (if the current HTTP request has an `Idempotency-Key`), and any
  **outbox rows** (`scope.Publish`). Commit, or roll back everything on any exception.
- Apps get **no** DbContext. Reads: `IAppData.Query<T>()` (no-tracking). Writes: only `IWriteScope` inside a UoW.
  `PlatformDbContext` is `internal`.
- Idempotency: mutating app endpoints require header `Idempotency-Key` (`^[A-Za-z0-9-]{8,128}$`) else 400.
  Key scope = (userId, method, path). Request hash = SHA-256 of the body. Same key + same hash => stored status + body,
  with response header `Idempotency-Replayed: true`, and the handler does not run. Same key + different hash => 422
  `{title:"Idempotency key reused with a different request"}`. Concurrent duplicate in flight => 409.
  Handlers return `Results.Ok(dto)` with the UoW result; that body is what gets stored.
- Outbox: `IOutboxRelay.RelayPendingAsync(ct)` publishes unsent rows to `IOutboxPublisher` (default: log sink),
  marks them sent with a conditional update (`WHERE SentAt IS NULL`) so two concurrent relays publish each row once.
  A `BackgroundService` calls it every 2 s.
- Audit row (`AuditEvent`): `id` (sequence), `timestamp` (UTC ISO-8601), `appSlug`, `actorId`, `action`, `resourceType`,
  `resourceId`, `outcome` (`success` | `denied` | `read`), `reason`, `before`, `after` (JSON strings, masked),
  `correlationId`, `prevHash`, `hash`.
  `hash = HMAC-SHA256(key = Platform:AuditChainKey, canonical(fields) + prevHash)` hex; first row `prevHash = 64 x "0"`.
  Computed inside the writing transaction. Denials/reads outside a UoW use the same writer in their own transaction.
  No update/delete API exists. `GET /api/platform/audit/verify` recomputes the chain and returns
  `{ valid, checked, firstBrokenId, reason }`.
- Masking in audit diffs: properties listed in the manifest `piiFields` or marked `[Pii]` are written as `"***"`
  (or `PiiMask` form). Sensitive values never reach logs.

### 3.7 Manifest (`platform.yaml`) — validated at startup, fail closed

```yaml
schemaVersion: 1
name: kyc-case-reviewer            # slug ^[a-z][a-z0-9-]{2,40}$, equals folder name
displayName: KYC Case Reviewer
description: Review and decide KYC cases with maker-checker.
owner: { team: kyc-engineering, email: kyc-eng@example.test }
businessOwner: { name: Head of Compliance Ops, email: compliance-ops@example.test }
dataClassification: restricted     # public | internal | confidential | restricted
access: write                      # read-only | write
stage: builder-only                # initial stage on registration: builder-only | demo | live
registration: intake               # intake (catalog entry created by Builder approval) | bootstrap (registered at startup)
roles: [KYC Analyst, KYC Senior Reviewer, Auditor]
accessList: []                     # user ids; applies in live
auditedFields: [status, recommendation, decision, assignedAnalystId]
piiFields: [customerName, dateOfBirth, ssnLast4]
cardData: none                     # must be "none" (zero-PAN rule)
backend: { assembly: Kyc.CaseReviewer }
```
Validation errors (any => refuse to start): missing required field, bad slug, unknown enum, `cardData` != none,
`piiFields` present but classification below `confidential`, `accessList` contains `"*"` for confidential/restricted,
policy file missing/invalid, policy references roles not in `roles` (pseudo-roles, `Builder`, `Auditor` and `*` excepted).

### 3.8 Catalog
- Table owned by platform. `ICatalogAdmin` (used by the Builder app, inside a UoW scope): `RegisterAsync`,
  `SetStageAsync`, `SetAccessListAsync`. Registration of an app whose code exists checks that the code's manifest has the
  same `access` mode and a classification >= the approved intake classification (else 422).
- `bootstrap` apps (the Builder) are registered at startup with the manifest's stage/accessList.

### 3.9 Secrets
- None in code/Git. Required: `Platform:AuditChainKey` (env `Platform__AuditChainKey` or `dotnet user-secrets`).
  Startup fails if missing, shorter than 32 chars, or a placeholder (`changeme`, `placeholder`, `todo`, `xxx`, `<...>`, `secret`).
- `scripts/dev.sh` generates a random key into the gitignored `.env` if none exists.

### 3.10 Telemetry and health
- OpenTelemetry tracing + logging to console; JSON console logs with redaction of known sensitive keys.
- `GET /health` => `{ "status": "ok" }` (anonymous).

### 3.11 Synthetic data
- Entities annotate properties with `[Synthetic(SyntheticKind.X)]`. `ISyntheticDataGenerator.Generate<T>(count)` builds
  deterministic fake rows (seed = hash of app slug + `Platform:SeedBase`, default 4242). Emails use `example.test`.
  Names come from Bogus. Never real PII. Never card numbers. Restricted apps get only obviously-fake values
  (e.g. last-4 digits from the 9xxx range).
- `ISyntheticSeeder.SeedAppAsync(scope, slug)` calls the app's `SeedAsync` (only if the app has no rows yet).

## 4. HTTP API contracts

All JSON camelCase. Errors are problem JSON. Mutations need `X-CSRF-Token`; app mutations also need `Idempotency-Key`.

### Platform
| Method | Path | Notes |
|---|---|---|
| GET | `/health` | anonymous |
| GET | `/api/session` | `{ authenticated, user: {id, displayName, email, roles[]} \| null, csrfToken, expiresAt }` |
| GET | `/api/auth/dev-users` | `[{id, displayName, roles[]}]` (dev identity provider only) |
| POST | `/api/auth/login` | `{userId}` → session JSON; rate limited; rotates session id |
| POST | `/api/auth/logout` | 204 |
| GET | `/api/platform/catalog` | `[CatalogEntry]` |
| GET | `/api/platform/catalog/{slug}` | `CatalogEntry` (incl. `manifestYaml`) |
| GET | `/api/platform/apps/{slug}/access` | `{slug, stage, allowed, reason}` |
| GET | `/api/platform/audit?app=&limit=50&beforeId=` | `[AuditEvent]` newest first; Auditor/Builder |
| GET | `/api/platform/audit/verify` | `{valid, checked, firstBrokenId, reason}`; Auditor/Builder |
| GET | `/api/platform/capabilities` | `[{id, name, description, howToSee}]` |

`CatalogEntry = { slug, displayName, description, owner:{team,email}, businessOwner:{name,email}, dataClassification, access, stage, accessList[], requesterId, approvedBy, registeredAt, hasCode, manifestYaml }`

### Barebone Builder (`/api/apps/barebone-builder`)
| Method | Path | Action (policy) |
|---|---|---|
| GET | `/intakes` | `intake.list` (Requester sees own; Builder sees all) |
| GET | `/intakes/{id}` | `intake.view` |
| GET | `/roi?hoursSavedPerUserPerWeek=&numberOfUsers=&access=&dataClassification=` | `intake.create` → `Roi` |
| POST | `/intakes` | `intake.create` (Requester, Builder) body `IntakeCreate` → `Intake` |
| POST | `/intakes/{id}/approve` | `intake.approve` (Builder; **deny if `resource.requesterId == $user.id`**) → registers app (stage builder-only), generated `platform.yaml`, seeds synthetic data |
| POST | `/intakes/{id}/reject` | `intake.approve` same rule; body `{reason}` |
| POST | `/apps/{slug}/stage` | `app.stage.set` (Builder) body `{stage}` → `CatalogEntry` |
| PUT | `/apps/{slug}/access-list` | `app.access.set` (Builder) body `{userIds[]}` → `CatalogEntry` |
| GET | `/directory` | `intake.list` → dev users (for the access-list picker) |

`IntakeCreate = { title, appSlug, problem, users, dataTouched, dataClassification, access: "read-only"|"write", hoursSavedPerUserPerWeek, numberOfUsers }`
`Intake = IntakeCreate + { id, status: "submitted"|"approved"|"rejected", requesterId, decidedBy, decidedAt, rejectReason, roi: Roi, generatedManifestYaml }`
`Roi` (constants in the builder app):
- `annualHoursSaved = hoursSavedPerUserPerWeek * numberOfUsers * 46`
- `annualValueUsd = annualHoursSaved * 85`
- `buildHours = (access=="write" ? 40 : 16) + (classification restricted ? 24 : confidential ? 12 : 0)`
- `buildCostUsd = buildHours * 150`
- `annualRunCostUsd = 2400 + (classification restricted ? 3600 : confidential ? 1200 : 0)`
- `paybackMonths = buildCostUsd / ((annualValueUsd - annualRunCostUsd) / 12)` (null if not positive), rounded to 1 dp
- `recommendation = paybackMonths != null && paybackMonths <= 12 ? "build" : "review"`

### KYC Case Reviewer (`/api/apps/kyc-case-reviewer`)
| Method | Path | Action |
|---|---|---|
| GET | `/cases` | `case.list`; rows filtered by `case.view` |
| GET | `/cases/{id}` | `case.view` → masked `CaseDetail` (notes, decisions) |
| POST | `/cases/{id}/reveal` | `case.reveal-pii` → `{customerName, dateOfBirth, ssnLast4}`; audited with outcome `read` |
| POST | `/cases/{id}/assign` | `case.assign` (analyst assigns to self if unassigned) |
| POST | `/cases/{id}/notes` | `case.note.add` body `{text}` (1..2000 chars) |
| POST | `/cases/{id}/recommendation` | `case.recommend` body `{recommendation: approve\|reject\|escalate, rationale}` → status `pending-senior-review`, `preparedBy = user` |
| POST | `/cases/{id}/decision` | `case.decide` (Senior; deny maker-checker `preparedBy == user`; status must be `pending-senior-review`) body `{decision: approve\|reject, rationale}` → `approved`/`rejected` |

Case statuses: `new` → `in-review` → `pending-senior-review` → `approved` | `rejected`.
Masked by default: `customerName` → `"J*** D***"`, `dateOfBirth` → `"****-**-**"` (year shown only after reveal),
`ssnLast4` → `"***-**-1234"` (only last 4 are ever stored). Documents are metadata only (`{name, type, uploadedAt}`).
Policy: Analysts see/act only on cases assigned to them or unassigned; Senior Reviewers see all and decide;
Auditors read-only (list/view masked) + platform audit trail; Requester/others denied; `platform:builder` and
`platform:demo-viewer` may `case.list`/`case.view` (masked only).

## 5. Frontend architecture
- One SPA, entry `platform/web/index.html` + `platform/web/src/main.tsx`, Vite root `platform/web`.
- 5.1 App discovery: `import.meta.glob('../../../apps/*/web/index.tsx', { eager: true })` from `platform/web/src/apps.ts`;
  each app's `web/index.tsx` default-exports an `AppModule` (see `platform/web/src/sdk/types.ts`).
- Shell routes: `/login` (dev user picker), `/` (catalog + app switcher), `/audit` (audit viewer + verify button),
  `/apps/:slug/*` (app gate → app routes). Header: user, roles, app switcher, stage badge, logout.
- `@platform/sdk` (alias of `platform/web/src/sdk`) is the only thing apps import from the platform:
  `api.get/post/put/del` (adds `X-CSRF-Token` and a fresh `Idempotency-Key` per call unless one is passed, sends
  `credentials: 'same-origin'`, throws `ApiError{status, problem}`), `useSession()`, `newIdempotencyKey()`,
  components `Page`, `StageBadge`, `AccessDenied`, `MaskedValue`, `AuditViewer`, `VerifyAuditButton`.
- No tokens or PII in localStorage/sessionStorage. No `dangerouslySetInnerHTML`.

## 6. Task split (sub-sessions; each pushes its own branch off `devin/1792922400-barebone-prototype`)
| Session | Owns | Done when |
|---|---|---|
| platform-backend | `platform/backend/**`, `platform/host/**`, `Directory.Build.props`, `Devin.slnx`, `global.json`, `.editorconfig`, `config/` | host runs, all platform endpoints work, a throwaway local test app proves UoW/idempotency/audit; `dotnet build -warnaserror` + `dotnet format --verify-no-changes` clean |
| platform-web | `platform/web/**`, root `package.json`, `tsconfig.json`, `eslint.config.js`, `vitest.config.ts` | shell works against the API contract; eslint + tsc + vitest clean |
| builder-app | `apps/barebone-builder/**` | builder endpoints + pages per contract |
| kyc-app | `apps/kyc-case-reviewer/**` | KYC endpoints + pages per contract |
| ci-docs | `.github/**`, `.gitleaks.toml`, `README.md`, `scripts/**` | workflow + docs + scripts |
| tests | `tests/**` | tests written from requirements in this file |
The integrator (parent session) reviews each branch, merges into the integration branch and fixes integration issues.
