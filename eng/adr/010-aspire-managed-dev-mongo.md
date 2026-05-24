# ADR-010: Aspire-managed MongoDB in local development

**Status:** Accepted 2026-05-19
**Context companion:** [Design-038](../../specs/038-developer-onboarding-snapshot/spec.md) (resolves its Open Question 1)

## Decision

The Aspire-managed local Mongo is **opt-in**, not the default. It activates
only when `useLocalMongo` is true:

```csharp
builder.Environment.IsDevelopment()
&& builder.ExecutionContext.IsRunMode
&& builder.Configuration.GetValue("Parameters:use-local-mongo", false)
```

`use-local-mongo` is a first-class Aspire parameter —
`builder.AddParameter("use-local-mongo", value: "false")`, registered like
`mongo-host` etc. (dashboard-visible, manifest, same resolution order: env
`Parameters__use_local_mongo` > user-secrets > appsettings; `value: "false"`
default so it is never an unresolved prompt in prod/CI, mirroring
`keycloak-admin-secret`). It is also checked into `appsettings.Development.json`
as `Parameters:use-local-mongo = "false"` so the knob is discoverable next to
`mongo-host`, and overridden per-machine via
`dotnet user-secrets set "Parameters:use-local-mongo" "true"`. Because a
`ParameterResource` is a deferred reference that cannot conditionally create
resources, the build-time topology branch reads the value from configuration
(`builder.Configuration["Parameters:use-local-mongo"]`) — the pattern the
Aspire "External parameters" docs explicitly sanction for AppHost-side reads. When true the AppHost runs `mongodb/mongodb-atlas-local`
(`AddContainer("mongodb-local", …)`) with a named volume (`starwars-dev-mongo`)
and `ContainerLifetime.Persistent`; the `mongodb` connection string is built
from that container's endpoint, and the snapshot-restore resource (gated on the
same flag) populates it once.

**Default (`"false"`) is unchanged behaviour:** the connection string is
assembled from the external `mongo-host/-port/-user/-password` parameters
pointing at the self-hosted server. So **production AND every existing
server-based dev workflow are byte-identical to before** — opting in is the
only way to get the container, and it can never enter
`aspire publish`/`prepare`/`deploy` output (Development+RunMode gate).
Crucially, snapshot-restore is gated on the *same* flag, so it can never
`mongorestore --drop` over a shared server's `starwars-dev`.

## Why this overrides the prior "not Aspire-managed" stance

CLAUDE.md previously stated MongoDB is "external self-hosted … not
Aspire-managed". That remains true and correct **for production**. The owner's
explicit requirement for developer UX is: *clone → `aspire run` → working app,
only an OpenAI key to set* — no pasted connection strings, no manual
`docker run`, no LAN dependency. An external-only model cannot deliver that for
a fresh clone with no access to the LAN server. So the decision is scoped:
**prod = external (unchanged); local dev = Aspire-managed.**

- **Atlas Local image, not plain `mongo`** — the app uses Atlas vector/text
  search; plain `mongo` would restore data but break search/RAG.
- **Persistent volume + lifetime** — the ~15 GB snapshot restore (Design-038)
  must run once, not on every `aspire run`.

## Consequences / known limitations

- A raw `AddContainer` has no Mongo health check, so `WaitFor` gates on
  *container running*, not *Mongo accepting connections*. A first-run race is
  possible; mitigated by the persistent volume + the idempotent, resumable
  restore (next `aspire run` self-heals). Acceptable for local dev.
- This was wired from the proven in-file container pattern and is
  **compile-verified only** — it must be exercised by one real `aspire run`
  on a fresh clone before it is trusted (tracked in Design-038 "Validation
  status").

## Revisit when

- Aspire ships a first-class Atlas Local hosting integration with a built-in
  health check → replace the raw container + manual `WaitFor` wiring with it.
- Production ever moves off the external self-hosted server → re-evaluate
  whether the split is still warranted.
