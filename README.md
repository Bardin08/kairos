# Kairos

Self-hosted booking links. You share a link, the person you sent it to picks a time that fits your calendar, and the meeting lands in both calendars. One operator per instance; invitees need no account.

Kairos runs as one container with SQLite inside. It idles at about 76 MiB in that container. The image is built for `linux/arm64` with a Raspberry Pi in mind, though it has not been measured on one yet.

## What it does and does not do

Supported:

- One operator per instance, signing in through any OIDC provider that puts a `groups` claim in the token (Pocket ID, Authentik, Keycloak and others).
- `linux/amd64` and `linux/arm64` images.
- Google Calendar for busy time and events, with a Google Meet link on each booking.
- Mail to the invitee over SMTP on booking, reschedule and cancellation, with an invitation their calendar can accept.
- Signed webhooks on booking events.

Not supported: several operators or teams, Zoom, recurring bookings.

## Quick start

You need Docker with Compose, and an OIDC provider you can register a client with. Kairos does not bring its own sign-in.

1. At your provider, register a client with redirect URI `http://localhost:8080/signin-oidc` and the `groups` scope. Put your account in a group named `kairos-users`.
2. Get the two files and fill in the four required settings:

   ```bash
   mkdir kairos && cd kairos
   curl -fsSLO https://raw.githubusercontent.com/Bardin08/kairos/v0.1.0/compose.yaml
   curl -fsSL https://raw.githubusercontent.com/Bardin08/kairos/v0.1.0/.env.example -o .env
   openssl rand -base64 32   # paste as Security__SecretKey in .env
   ```

   Then set `Authentication__Authority`, `Authentication__ClientId` and `Authentication__ClientSecret` in `.env` from step 1.
3. Start it, and check it can reach its database:

   ```bash
   docker compose up -d
   curl -sS --retry 15 --retry-delay 1 --retry-all-errors localhost:8080/readyz
   ```

   It prints `Healthy` once the container has started. A connection error or two before that, while it starts, is expected.

4. Open <http://localhost:8080/admin/login>, sign in, and create a link under Links.
5. Open the link's booking page in a private window and book a slot. The booking appears under Meetings.

Nothing else is required. Without Google, a booking is recorded and no calendar event is made; without mail settings, no mail is sent.

## Configuration

Every setting is an environment variable in `.env`. A configuration path's `:` is written `__`, so `Authentication:Authority` is `Authentication__Authority`, and the first item of a list is `…__0`.

| Setting | Default | What it does |
|---|---|---|
| `Security__SecretKey` | required | Base64 32-byte key encrypting stored integration credentials. A new key cannot read what an old one encrypted |
| `Authentication__Authority` | required | The OIDC issuer URL |
| `Authentication__ClientId` | required | The client registered for Kairos |
| `Authentication__ClientSecret` | required | Its secret |
| `Authorization__AdminGroup` | `kairos-users` | The group in the `groups` claim whose members administer Kairos |
| `Authorization__AdminSubjects__0` | none | Admits only these subject ids from that group |
| `Proxy__KnownProxies__0` | none | The reverse proxy's IP address, whose forwarded headers Kairos trusts |
| `Proxy__KnownNetworks__0` | none | The same, as a CIDR network |
| `App__PublicBaseUrl` | the browser's origin | The public origin for links the admin hands out, and for links in mail |
| `Email__Host` | none | SMTP host. Mail is off unless `Email__Host`, `Email__From` and a public origin are set |
| `Email__Port` | `587` | SMTP port, STARTTLS |
| `Email__Username`, `Email__Password` | none | SMTP credentials |
| `Email__From` | none | Sender address |
| `Email__PublicBaseUrl` | `App__PublicBaseUrl` | The public origin written into links in mail, when it differs from the admin's |
| `Integrations__Google__ClientId`, `…__ClientSecret` | none | A Google OAuth client; Google Calendar and Meet are off without it |
| `Integrations__Google__RedirectUri` | none | `https://<your host>/api/v1/integrations/google/callback`, exactly as registered with Google |
| `Webhooks__Url` | none | Receives a POST for each booking event |
| `Webhooks__Secret` | none | HMAC-SHA256 key; the signature is in `X-Kairos-Signature` |

## Behind a reverse proxy

Kairos listens on plain HTTP on 8080 and expects a proxy in front of it to terminate TLS. The session cookie is always `Secure`, so anything other than `localhost` needs HTTPS.

- Set `Proxy__KnownProxies__0` to the address the proxy connects from. Until you do, Kairos ignores `X-Forwarded-Proto` and `X-Forwarded-For`, sign-in redirects to `http://`, and every visitor shares one rate-limit bucket.
- Register `https://<your host>/signin-oidc` with your OIDC provider.
- `compose.yaml` binds 8080 to loopback. If the proxy runs on another host, change the port line to `"8080:8080"`.
- `/healthz` answers 200 while the process is up; `/readyz` answers 200 while it can reach its database.

## Data

Everything Kairos keeps is in the `kairos-data` volume, at `/var/lib/kairos`: one SQLite database with its `-wal` and `-shm` files. A documented backup and restore procedure is not written yet.

## Upgrading

Pin a release in production instead of `latest`: set `image: ghcr.io/bardin08/kairos:v0.1.0` in `compose.yaml`, then change the tag and run `docker compose pull && docker compose up -d` to upgrade.

Known limitation: each new container signs the operator out, because the keys that protect the session cookie are not kept on the volume yet. Signing in again is the whole fix.

## Development

Running from source, the test harnesses and the build: [`docs/running-locally.md`](docs/running-locally.md). Contributor rules: [`AGENTS.md`](AGENTS.md).

## License

[AGPL-3.0](LICENSE).
