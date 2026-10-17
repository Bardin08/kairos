# Backing up and restoring Kairos

Everything Kairos keeps is in one Docker volume: the SQLite database `kairos.db` and the key ring under `.aspnet/DataProtection-Keys/`. A backup is a consistent copy of both. A restore puts them on an empty volume and starts the image on it. `scripts/restore-check.sh` tells you whether the two match.

The commands below call the volume `$VOLUME`. With the repository's `compose.yaml` it is named after the compose project, so find it first:

```
docker volume ls --filter name=kairos-data
VOLUME=kairos_kairos-data   # whatever the line above printed
```

They also pin one helper image, used only for its shell and the SQLite command line:

```
ALPINE=alpine:3.22@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8
```

## Backup

Run this while Kairos is up. It needs nothing on the host but Docker.

```
mkdir -p backup
docker run --rm -v "$VOLUME:/data" -v "$PWD/backup:/out" "$ALPINE" sh -c '
  apk add --no-cache sqlite=3.49.2-r1 >/dev/null
  mkdir /stage && cp -R /data/.aspnet /stage/
  sqlite3 /data/kairos.db ".backup /stage/kairos.db"
  tar -C /stage -czf "/out/kairos-$(date -u +%Y%m%dT%H%M%SZ).tar.gz" .'
```

`.backup` is SQLite's own online backup. It produces a consistent database even while Kairos is writing, which a plain copy of `kairos.db` does not: the database runs in WAL mode, and recent writes sit in `kairos.db-wal` until a checkpoint moves them. The volume is mounted read-write on purpose. SQLite needs to write the `-shm` file even to read a WAL database, so `:ro` makes the backup fail.

Three things the command does not do for you:

- **Encrypt it.** The archive holds every invitee's name and email and the encrypted calendar credentials. Encrypt it before it leaves the host, with whatever you already use.
- **Keep the secret key.** `Security__SecretKey` is not in the archive and must not be. Keep it somewhere you can reach during a restore. Without it the restore still runs, but every integration has to be reconnected and the operator signs in again.
- **Schedule it.** Run the command from cron, a systemd timer or your backup tool. Kairos schedules nothing.

## Restore

**Pick the image.** Restore with the release the backup was taken on, or a newer one, never an older one. A newer image applies its migrations on start; an older one does not know the archive's. `scripts/restore-check.sh` prints the archive's migration on its first line.

**Unpack onto an empty volume.** The helper runs with no network and refuses a volume that already holds anything:

```
mkdir restore && tar -xzf backup/kairos-<stamp>.tar.gz -C restore
docker run --rm --network none -v "$VOLUME:/data" -v "$PWD/restore:/restore:ro" "$ALPINE" sh -c '
  [ -z "$(ls -A /data)" ] || { echo "volume is not empty" >&2; exit 1; }
  cp /restore/kairos.db /data/ && cp -R /restore/.aspnet /data/
  rm -f /data/kairos.db-wal /data/kairos.db-shm
  chown -R 1654:1654 /data'
```

1654 is the user the image runs as. The `rm` matters when the archive came from somewhere other than the backup command above: a `-wal` file that does not belong to this `kairos.db` corrupts it on open.

**Start it** with the same `Security__SecretKey` as the instance that made the backup. What happens next depends on which of the two restores this is.

### Disaster restore

The instance is coming back on new hardware, with its own `.env`. Create the volume and container without starting them, restore into the volume, then start:

```
docker compose create
VOLUME=$(docker volume ls -q --filter name=kairos-data)
# the unpack command above
docker compose up -d
```

On first start the instance catches up with everything it missed, which is correct and can look alarming: booking events still in the outbox go to `Webhooks__Url`, reminders that fell due go out by mail, and the calendar integration picks up where it stopped.

### Test restore

A test restore proves a backup is good. It is a second copy of a live instance, and the database holds real invitees, queued webhook events, due reminders and the integration's settings. Settings in the database are not switched off by leaving environment variables out, so the copy has to be cut off from the network instead. Kairos goes on a Docker network with no route out, and you prove that before Kairos starts.

```
docker network create --internal kairos-restore-test
docker volume create kairos-restore-test
VOLUME=kairos-restore-test
# the unpack command above
```

Prove the network is closed, and prove the probe can tell. The first command must print `isolated`; the second must print `control reached out`, or the first proved nothing:

```
docker run --rm --network kairos-restore-test "$ALPINE" sh -c '
  if nslookup example.com >/dev/null 2>&1 || nc -z -w 5 1.1.1.1 443; then echo LEAK; exit 1; fi
  echo isolated'
docker run --rm "$ALPINE" sh -c 'nslookup example.com >/dev/null && nc -z -w 5 1.1.1.1 443 && echo "control reached out"'
```

Start Kairos on that network with no `Email__*`, `Webhooks__*` or `Integrations__*` settings and no published port. The three `Authentication__*` values only have to be present; nothing contacts the provider until someone signs in.

```
docker run -d --name kairos-restore-test --network kairos-restore-test --network-alias kairos \
  -v kairos-restore-test:/var/lib/kairos \
  -e Security__SecretKey="<the instance's key>" \
  -e Authentication__Authority=https://id.example.test \
  -e Authentication__ClientId=restore-test \
  -e Authentication__ClientSecret=restore-test \
  ghcr.io/bardin08/kairos:<tag>
```

## Verify

A restore passes three checks. Run them from a second container on the same network, since the restored copy publishes no port:

```
docker run --rm --network kairos-restore-test "$ALPINE" sh -c '
  until wget -q -O /dev/null http://kairos:8080/readyz; do sleep 1; done; echo "readyz 200"
  wget -q -O /dev/null http://kairos:8080/api/v1/links/<an active slug> && echo "link 200"'
```

Then stop Kairos, copy the database out of the volume, and compare it with the archive's:

```
docker stop kairos-restore-test
mkdir after
docker run --rm -v kairos-restore-test:/data:ro -v "$PWD/after:/out" "$ALPINE" sh -c 'cp /data/kairos.db* /out/'
scripts/restore-check.sh restore/kairos.db > before.txt
scripts/restore-check.sh after/kairos.db > after.txt
diff before.txt after.txt && echo "every table matches"
```

Identical output means the same migration and the same number of rows in every table: every booking, participant, override and setting came back. If the image was newer than the backup, the migration line differs and the counts are what to compare.

Signing in to the admin is a fourth check you can add when the restored copy can reach your identity provider. On an isolated network it cannot, and the three above do not need it.

Clean up:

```
docker rm kairos-restore-test
docker volume rm kairos-restore-test
docker network rm kairos-restore-test
rm -rf restore after before.txt after.txt
```
