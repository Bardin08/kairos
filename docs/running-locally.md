# Running Kairos locally

Four commands, from the repository root. The order matters: the API serves the single page out of its own `wwwroot`, so nothing is on screen until the frontend has been built into it.

```
docker compose -f compose.dev.yaml up -d
cd frontend && npm ci && npm run build && cd ..
cd backend && ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Kairos.Api --no-launch-profile
```

Then open <http://localhost:5000/admin>, which answers 401 and redirects nowhere until you sign in. Go to <http://localhost:5000/admin/login>, type any name into the mock provider's form — `vlad`, or anything else — and paste `{"groups": ["kairos-users"]}` into the **Optional claims JSON** box beside it. Press Sign-in and you land back on `/admin/links` with a session, and the workbench is yours.

The claims box is not optional here, whatever the label says. A session is not an operator: the admin policy admits accounts carrying the group `kairos-users`, so a sign-in with only a username lands fine and then every admin route answers 403. That is the boundary working rather than a broken local setup — an OIDC provider is shared across applications, so its user list is not this application's operator list. Nothing about it is relaxed for Development, because a boundary that is off on a laptop is a boundary nobody tests. A deployment names its own group with `Authorization__AdminGroup` and, where the provider hands that group out more widely than Kairos should trust, narrows it further with `Authorization__AdminSubjects__0`.

No `Security__SecretKey` on the command line: `appsettings.Development.json` carries a committed, deliberately public one so a local run is reproducible and so rows encrypted before a restart can still be read after it. It is labelled in that file and must never be set anywhere but Development. Every other environment still fails fast with no key, as it always did.

## What the compose file is

`compose.dev.yaml` runs one container: `ghcr.io/navikt/mock-oauth2-server`, pinned by tag and digest, bound to `127.0.0.1:5400`. It serves real OIDC discovery at `http://localhost:5400/default/.well-known/openid-configuration` and issues real signed tokens, but it accepts any client id, any client secret, any redirect URI and any username. `appsettings.Development.json` points `Authentication:Authority` at it.

That is the whole point: signing in locally exercises the real `AddOpenIdConnect` handler — challenge, PKCE, the provider's form, `form_post` back to `/signin-oidc`, correlation and nonce checks, the code exchange, the cookie — rather than substituting a scheme or minting a cookie. Nothing in production reads this file, and no bypass was added to the application to make local sign-in work.

The one production-shaped concession is `Authentication:RequireHttpsMetadata`, which `Program.cs` turns off behind `builder.Environment.IsDevelopment()` because this issuer speaks plain HTTP on loopback. It relaxes the transport metadata is fetched over; token signatures, issuer and audience are still checked.

## Two things worth knowing

**The mock sends no `name` claim**, so `signedInAs` comes back null and the rail draws no identity block — the same as a real provider configured not to release a name. To see the block, put a name in the same claims box as the group: `{"name": "Vlad Bardin", "groups": ["kairos-users"]}`.

**Port 5000 is contested on macOS.** AirPlay Receiver listens there. If Kestrel cannot bind, either turn it off in System Settings → General → AirDrop & Handoff, or run with `ASPNETCORE_URLS=http://localhost:5147` and use that port throughout.

## Stopping

```
docker compose -f compose.dev.yaml down
```

The provider keeps no state worth preserving; its signing keys are regenerated on each start, which invalidates any session issued before the restart. Signing in again is the whole recovery.
