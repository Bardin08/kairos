# Running Kairos locally

Four commands, from the repository root. The order matters: the API serves the single page out of its own `wwwroot`, so nothing is on screen until the frontend has been built into it.

```
docker compose -f compose.dev.yaml up -d
cd frontend && npm ci && npm run build && cd ..
cd backend && ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Kairos.Api --no-launch-profile
```

Then open <http://localhost:5000/admin>, which answers 401 and redirects nowhere until you sign in. Go to <http://localhost:5000/admin/login>, type any name into the mock provider's form — `vlad`, or anything else — and paste `{"groups": ["kairos-users"]}` into the **Optional claims JSON** box beside it. Press Sign-in and you land back on `/admin/links` with a session, and the workbench is yours.

The claims box is not optional here, whatever the label says. A session is not an operator: the admin policy admits accounts carrying the group `kairos-users`, so a sign-in with only a username lands fine and the workbench then replaces itself with a screen saying the account is not an operator of this Kairos. That is the boundary working rather than a broken local setup — an OIDC provider is shared across applications, so its user list is not this application's operator list. Nothing about it is relaxed for Development, because a boundary that is off on a laptop is a boundary nobody tests. A deployment names its own group with `Authorization__AdminGroup` and, where the provider hands that group out more widely than Kairos should trust, narrows it further with `Authorization__AdminSubjects__0`. Whichever group is configured, the API logs it once at startup — that line is the answer to "which group does this deployment admit?".

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

---

# Running Kairos as a container

Everything above is the development loop: two toolchains on the host and a mock identity provider beside them. This section is the other thing in the repository — one image that carries both halves of the application and takes every setting from its environment. It is what a deployment runs, and it needs nothing on the machine but Docker.

```
docker build -t kairos:local .
docker run --rm -p 8080:8080 \
  -e Security__SecretKey="$(openssl rand -base64 32)" \
  -e Authentication__Authority=https://id.example.test \
  -e Authentication__ClientId=kairos \
  -e Authentication__ClientSecret=placeholder \
  -v kairos-data:/var/lib/kairos \
  kairos:local
```

Then `curl http://localhost:8080/healthz` and `curl http://localhost:8080/readyz` — the first says the process is alive, the second that it can reach its database. Both answer 200 on a healthy container and nothing else needs to be true for them to.

The `Dockerfile` builds the SPA itself, in its own Node stage, from `frontend/` and the committed lockfile — a `npm run build` you ran on this machine is deliberately kept out of the build context, so what the image serves is always what the image built. The three base images are pinned by tag and digest, as `compose.dev.yaml` pins the mock provider.

## What it expects

Four settings have no default and the process refuses to start without them: `Security__SecretKey` (a base64 32-byte AES-GCM key — `openssl rand -base64 32`), and `Authentication__Authority`, `Authentication__ClientId` and `Authentication__ClientSecret` for the OIDC provider the operator signs in against. Every other setting has one. The double underscore is how a `:` in a configuration path is written in an environment variable, so `Proxy:KnownProxies:0` is `Proxy__KnownProxies__0` and so on for all of them; nothing is baked into the image that an environment variable cannot replace.

`/var/lib/kairos` is where the SQLite database lives, and it is the one thing to keep. Give it a volume: the connection string already points there, the database runs in WAL mode so `kairos.db` has `-wal` and `-shm` siblings, and a backup of that directory is a backup of the whole application. Run without a volume and the data goes with the container.

The container listens on 8080 and holds no certificate. That is by design — a deployment puts a TLS-terminating reverse proxy in front of it, and the proxy is what redirects plaintext to HTTPS. Tell Kairos which address that proxy has with `Proxy__KnownProxies__0`, because until you do it ignores the forwarded headers and sees every request as plaintext arriving from the proxy's own address, which is both a wrong rate-limit bucket and a sign-in that redirects to `http://`.

## Checking that the proxy leg works

`compose.proxy.yaml` and `Caddyfile` are a verification harness, not a deployment. They run the image behind Caddy holding a certificate from its own local CA, which is the only shape in which the two decisions that depend on a forwarded request are observable at all.

```
docker compose -f compose.proxy.yaml up -d --build
curl -sD - -o /dev/null -k --resolve kairos.test:443:127.0.0.1 https://kairos.test/healthz
curl -sD - -o /dev/null -H 'Host: kairos.test' http://localhost:8080/healthz
docker compose -f compose.proxy.yaml down -v
```

The first goes through the proxy and comes back carrying `Strict-Transport-Security`. The second reaches the same container directly, with no proxy hop and therefore no `X-Forwarded-Proto`, and comes back with no HSTS header at all. That difference is the only thing being measured, and it is why both requests ask for the same path: they differ in one variable, so the header appearing in one and not the other has one available explanation. A container that sent HSTS either way would be emitting it unconditionally instead of reading the forwarded scheme, and neither request on its own could tell those apart.

Pick the path for that property, not for convenience. `/api/v1/links/whatever` looks like a reasonable choice and is a bad one: it is a `DomainException` route, and a refusal is exactly the kind of response a header can go missing from without anybody noticing — this repository shipped that bug once and the harness pointed at that path could not see it. `/healthz` and `/` take the ordinary path in both directions.

`kairos.test` rather than `localhost`, for the same reason one level up: ASP.NET Core's HSTS middleware excludes `localhost`, `127.0.0.1` and `[::1]` by default, so the same check run against `https://localhost` reports no HSTS header no matter what the application does — it would pass while proving nothing. The Caddyfile serves both names so the difference can be seen rather than remembered.

**The session cookie is a separate property and it is not what this pair measures.** `curl -k --resolve kairos.test:443:127.0.0.1 https://kairos.test/admin/logout` comes back with `Set-Cookie: .AspNetCore.Cookies=; ...; secure; samesite=lax; httponly`, and so does the same request sent to `http://localhost:8080` over plaintext with no proxy in front of it. That is correct and deliberate: `CookieSecurePolicy.Always` in `Program.cs` makes `Secure` unconditional rather than derived from the scheme, precisely so that a proxy misconfiguration cannot quietly downgrade a session. So the attribute proves the policy, not the proxy. (`/admin/logout` writes a cookie *deletion* — the session's own `CookieBuilder`, so the attributes on it are the real ones, but no session is created by asking.)
