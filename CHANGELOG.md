# Changelog

Releases are tagged `vX.Y.Z` and published as `ghcr.io/bardin08/kairos:vX.Y.Z` for `linux/amd64` and `linux/arm64`.

## v0.2.0

- First public release: the repository is open and the image pulls without signing in.
- Release images are signed: `gh attestation verify oci://ghcr.io/bardin08/kairos:<tag> --owner Bardin08`, with an SBOM and build provenance attached.
- Build the image from source with `docker build`.

## v0.1.1

- The landing page at `/` and the privacy policy and terms at `/privacy` and `/terms` are served publicly. The operator's details for them are entered under Settings → Legal pages.
- Crawlers are kept to the landing and legal pages; booking links, the admin and the API are disallowed in `robots.txt`.
- A redeploy no longer signs the operator out. The sign-in key ring now lives on the data volume, encrypted under `Security__SecretKey`; changing that key signs the operator out.
- Two schema clean-up migrations run on first start: a blocked date is stored as a date with no windows, and the integration settings column is renamed.
- Integration credentials are proven on demand and on a 15-minute timer, so a dead grant shows up before a booking page needs it.
- Meetings a calendar refused are placed automatically once the grant works again, and from the workbench. The invitee receives a join link after a resync.
- Backup and restore are documented in `docs/backup-and-restore.md`, with `scripts/restore-check.sh` to confirm a restore brought every booking back.
- A new brand mark replaces the clock glyph.

## v0.1.0

First release. One operator per instance, OIDC sign-in, booking links with availability patterns and overrides, Google Calendar busy time and events with Meet links, SMTP mail with calendar invitations, signed webhooks, and a 24-hour reminder.
