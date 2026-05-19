# ADR-010: Aspire-managed MongoDB in local development

**Status:** Accepted 2026-05-19
**Context companion:** [Design-038](../design/038-developer-onboarding-snapshot.md) (resolves its Open Question 1)

## Decision

In **Development + run mode only**, the AppHost runs MongoDB itself as an
Aspire-managed `mongodb/mongodb-atlas-local` container
(`AddContainer("mongodb-local", …)`), with a named data volume
(`starwars-dev-mongo`) and `ContainerLifetime.Persistent`. The `mongodb`
connection string is built from that container's endpoint.

In **Production (and any publish/prepare/deploy)** nothing changes: the
connection string is still assembled from the external `mongo-host/-port/-user/
-password` parameters pointing at the self-hosted server. The container is
gated by `builder.Environment.IsDevelopment() && builder.ExecutionContext.IsRunMode`,
so it can never enter `aspire publish`/`prepare`/`deploy` output.

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
