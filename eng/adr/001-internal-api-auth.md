# ADR-001: Internal API Authentication via X-User-Id Header

**Status:** Accepted
**Date:** 2026-03-29
**Decision maker:** Patrick Magee

## Context

Star Wars Data Explorer consists of two runtime services:

- **Frontend** — Blazor Web App (Interactive Server mode) with Keycloak OIDC authentication
- **ApiService** — ASP.NET Core API providing data endpoints, AI agent, and background jobs

The Frontend authenticates users via Keycloak (`auth.magaoidh.pro`) using OpenID Connect with the Authorization Code flow (PKCE). The API is a separate process that the Frontend calls server-to-server.

### The Problem

We need the API to know which user is making a request so it can:

- Scope chat sessions and settings to the correct user
- Apply per-user rate limiting
- Look up BYOK OpenAI keys

### Options Considered

#### Option A: JWT Bearer Token Forwarding

Forward the Keycloak access token from the Frontend to the API as a Bearer token. The API validates the JWT and extracts the user identity from claims.

**Attempted and failed.** Blazor Interactive Server mode uses SignalR for all interactive rendering. `HttpContext` (and therefore `GetTokenAsync("access_token")`) is only available during the initial static SSR/prerender HTTP request. Once the SignalR circuit is established:

- `IHttpContextAccessor.HttpContext` returns the stale `HttpContext` from the initial connection via `AsyncLocal<T>`, but `GetTokenAsync` does not reliably return tokens in this context
- `DelegatingHandler` instances are created in a separate DI scope by `IHttpClientFactory`, so scoped services (like a `CircuitTokenProvider`) are different instances than those in the Blazor circuit scope
- Middleware runs in the request pipeline DI scope, which is again different from the Blazor circuit DI scope

Microsoft's own documentation states:

> "The token handler only executes during **static server-side rendering (static SSR)**, so using HttpContext is safe in this scenario."

Our app uses `@rendermode="InteractiveServer"` globally, so all component renders happen over SignalR, not SSR. The MS-recommended workaround for interactive mode is the Backend-for-Frontend (BFF) pattern with YARP reverse proxy, which is a significant architectural change.

Multiple implementation attempts were made:

1. `DelegatingHandler` with `IHttpContextAccessor` — handler not invoked (stripped by `RemoveAllResilienceHandlers`)
2. After fixing handler registration — `GetTokenAsync` returns null during SignalR renders
3. Scoped `CircuitTokenProvider` populated by middleware — different DI scope than Blazor circuit
4. Singleton `TokenStore` bridge — timing issues, token not available when components render
5. `ApiClient` wrapper with lazy token capture — same scope isolation problem

All approaches failed because the fundamental architecture (Interactive Server + separate API) does not support token forwarding without YARP/BFF.

#### Option B: X-User-Id Header (Selected)

The Frontend extracts the user ID from the authenticated `ClaimsPrincipal` (Keycloak `sub` claim) and sends it as an `X-User-Id` header on every API request. The API trusts this header because the API is not exposed to the internet.

#### Option C: BFF with YARP Reverse Proxy

Use YARP to proxy API requests through the Frontend, attaching tokens at the proxy layer. This is Microsoft's recommended approach but requires significant architectural changes (adding YARP, restructuring the API as a backend behind the proxy).

## Decision

**Option B: X-User-Id Header** with network-level security.

## Rationale

1. **The API is internal-only.** In production, only the Blazor Server frontend can reach the API. The frontend runs behind Cloudflare Tunnel; the API port is not exposed. In Docker Compose, both services are on an isolated bridge network.

2. **Authentication happens at the Frontend.** Keycloak OIDC authentication is enforced by the Blazor Server app. Users cannot reach the API without going through the authenticated frontend. The `X-User-Id` header is set by a `DelegatingHandler` that reads from the validated `ClaimsPrincipal` — no user input can influence it.

3. **YARP/BFF is disproportionate.** Adding a reverse proxy layer for a single-person fan project with an internal API adds complexity without meaningful security benefit given the network isolation.

4. **Pragmatic and working.** This pattern is simple, testable, and works reliably across all Blazor render modes.

## Security Considerations

- **Network isolation is the security boundary.** The API trusts `X-User-Id` because only the Frontend can reach it. If the API were ever exposed publicly, this pattern would need to be replaced with proper token validation.
- **No bearer tokens cross service boundaries.** Tokens stay in the Frontend's cookie. The API never sees or validates JWTs.
- **Rate limiting uses `X-User-Id` for authenticated users** and `X-Forwarded-For` IP for anonymous users.
- **BYOK keys are encrypted** at rest via ASP.NET Core Data Protection regardless of the auth pattern.
- **Admin endpoints** are not protected at the API level. Admin access is controlled by the Frontend's `[Authorize]` attribute and Keycloak role assignment. The Aspire dashboard HTTP commands for admin operations are accessible from the Aspire dashboard only.

## Consequences

### Positive

- Works reliably with Blazor Interactive Server mode
- Simple to understand and maintain
- No additional infrastructure (YARP, token refresh logic)
- All existing features (chat history, BYOK, data export) work correctly

### Negative

- API trusts the Frontend — no defence-in-depth at the API layer
- If API is accidentally exposed publicly, `X-User-Id` can be spoofed
- Admin endpoints have no API-level protection
- Cannot support third-party API consumers without adding auth

### Mitigations

- Docker Compose network isolation prevents external access
- Cloudflare Tunnel only exposes the Frontend port
- Periodic review: if the API needs to be exposed, implement JWT Bearer or YARP/BFF
- Consider adding API-level admin protection via a shared secret header if Aspire dashboard commands become insufficient

## References

- [ASP.NET Core Blazor additional security scenarios — Token handler for web API calls](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/additional-scenarios?view=aspnetcore-10.0#use-a-token-handler-for-web-api-calls)
- [Secure a Blazor Web App with OIDC — With YARP and Aspire](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-oidc?view=aspnetcore-10.0&pivots=with-yarp-and-aspire)
- [IHttpContextAccessor/HttpContext in Blazor apps](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/httpcontext?view=aspnetcore-10.0)
- [Problem providing Access Token to HttpClient in Interactive Server mode (dotnet/aspnetcore #52390)](https://github.com/dotnet/aspnetcore/issues/52390)
