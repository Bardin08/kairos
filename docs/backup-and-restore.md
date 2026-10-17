# Backing up and restoring Kairos

Everything Kairos keeps is in one Docker volume: the SQLite database `kairos.db` and the key ring under `.aspnet/DataProtection-Keys/`. A backup is a consistent copy of both. A restore puts them on an empty volume and starts the image on it. `scripts/restore-check.sh` tells you whether the two match.

Run these commands from a working directory outside any git checkout: the unpacked key ring is not covered by the repository's `.gitignore`.

The commands below call the volume `$VOLUME`. Compose names the volume after its project, and a filter on the name can match several volumes, so ask Compose instead. Run this from the directory that holds the deployment's `compose.yaml`:

```
VOLUME=$(docker compose volumes -q)
```

They also pin one helper image, used only for its shell and the SQLite command line:

```
ALPINE=alpine:3.22@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8
```

Verifying a restore uses `scripts/restore-check.sh` from a checkout of this repository. Point at it once:

```
CHECK=/path/to/kairos/scripts/restore-check.sh
```

## Backup

Run this while Kairos is up. It needs nothing on the host but Docker.

```
mkdir -p backup
docker run --rm -v "$VOLUME:/data" -v "$PWD/backup:/out" "$ALPINE" sh -ec '
  [ -f /data/kairos.db ] || { echo "no kairos.db on this volume: check VOLUME" >&2; exit 1; }
  apk add --no-cache sqlite=3.49.2-r1 >/dev/null
  mkdir /stage && cp -R /data/.aspnet /stage/
  sqlite3 /data/kairos.db ".backup /stage/kairos.db"
  tar -C /stage -czf "/out/kairos-$(date -u +%Y%m%dT%H%M%SZ).tar.gz" .'
```

`.backup` is SQLite's own online backup. It produces a consistent database even while Kairos is writing, which a plain copy of `kairos.db` does not: the database runs in WAL mode, and recent writes sit in `kairos.db-wal` until a checkpoint moves them. The volume is mounted read-write on purpose. SQLite needs to write the `-shm` file even to read a WAL database, so `:ro` makes the backup fail.

The pin is exact because Alpine's mirror keeps only the newest revision of a package. When `apk` reports `sqlite=3.49.2-r1` missing, change the version here and in `scripts/restore-check.sh` to the one `apk` offers.

The command stops at the first step that fails and writes no archive, so an incomplete backup cannot pass for a good one. Its first line stops it when `$VOLUME` holds no `kairos.db`, which means the variable names the wrong volume (Docker would otherwise create an empty one under that name). If that line passes and the command still stops, the usual cause is an image released before the key ring moved onto the volume, v0.1.0 among them: there `.aspnet` is on the container's own layer, the volume holds only the database, and the backup stops at the first `cp`. Upgrade first: a newer image writes the key ring to the volume as it starts.

Three things the command does not do for you:

- **Encrypt it.** The archive holds every invitee's name and email and the encrypted calendar credentials. Encrypt it before it leaves the host, with whatever you already use.
- **Keep the secret key.** `Security__SecretKey` is not in the archive and must not be. Keep it somewhere you can reach during a restore. Without it the restore still runs, but every integration has to be reconnected and the operator signs in again.
- **Schedule it.** Run the command from cron, a systemd timer or your backup tool. Kairos schedules nothing.

## Restore

**Pick the image.** Restore with the release the backup was taken on, or a newer one, never an older one. A newer image applies its migrations on start; an older one does not know the archive's. `scripts/restore-check.sh` prints the archive's migration on its first line.

**Unpack onto an empty volume.** The helper runs with no network and refuses a volume that already holds anything:

```
mkdir restore && tar -xzf backup/kairos-<stamp>.tar.gz -C restore
docker run --rm --network none -v "$VOLUME:/data" -v "$PWD/restore:/restore:ro" "$ALPINE" sh -ec '
  [ -z "$(ls -A /data)" ] || { echo "volume is not empty" >&2; exit 1; }
  cp /restore/kairos.db /data/ && cp -R /restore/.aspnet /data/
  chown -R 1654:1654 /data'
```

1654 is the user the image runs as. Only `kairos.db` is copied, so a `-wal` file from the archive never reaches the volume and a foreign WAL cannot corrupt the database. The catch is an archive made by a raw file copy: it can hold committed rows only in its `-wal`, which this step drops. Take backups with the command above.

**Start it** with the same `Security__SecretKey` as the instance that made the backup. What happens next depends on which of the two restores this is.

### Disaster restore

The instance is coming back on new hardware, with its own `.env`. Create the volume and container without starting them, restore into the volume, then start:

```
docker compose create
VOLUME=$(docker compose volumes -q)
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

Start Kairos on that network with no `Email__*`, `Webhooks__*` or `Integrations__*` settings and no published port. The three `Authentication__*` values only have to be present; nothing contacts the provider until someone signs in. Put the settings in a file and fill in the key there, so it stays out of shell history and `ps`:

```
cat > restore-test.env <<'EOF'
Security__SecretKey=
Authentication__Authority=https://id.example.test
Authentication__ClientId=restore-test
Authentication__ClientSecret=restore-test
EOF
```

Paste the instance's key after `Security__SecretKey=`, then start it:

```
docker run -d --name kairos-restore-test --network kairos-restore-test --network-alias kairos \
  -v kairos-restore-test:/var/lib/kairos \
  --env-file restore-test.env \
  ghcr.io/bardin08/kairos:<tag>
```

## Verify

A restore passes three checks. Run them from a second container on the same network, since the restored copy publishes no port:

```
docker run --rm --network kairos-restore-test "$ALPINE" sh -c '
  i=0
  until wget -q -O /dev/null http://kairos:8080/readyz; do
    i=$((i+1)); [ "$i" -lt 60 ] || { echo "not ready after 60s: docker logs kairos-restore-test" >&2; exit 1; }
    sleep 1
  done; echo "readyz 200"
  wget -q -O /dev/null http://kairos:8080/api/v1/links/<an active slug> && echo "link 200"'
```

Then stop Kairos, copy the database out of the volume, and compare it with the archive's:

```
docker stop kairos-restore-test
mkdir after
docker run --rm -v kairos-restore-test:/data:ro -v "$PWD/after:/out" "$ALPINE" sh -c 'cp /data/kairos.db* /out/'
"$CHECK" restore/kairos.db > before.txt
"$CHECK" after/kairos.db > after.txt
diff before.txt after.txt && echo "every table matches"
```

Identical output means the same migration and the same number of rows in every table: every booking, participant, override and setting came back. If the image was newer than the backup, the migration line differs and it can add whole new table lines; the counts of the tables both sides have are what to compare.

One line may differ on a real instance: `OutboxMessages` can be higher in the restored copy, by the number of meetings that ended between the backup and the restore. At start the restored instance queues a meeting-ended event for each of them, whether or not webhooks are configured. That is the instance catching up, not a defect. Every other line must match exactly.

Signing in to the admin is a fourth check you can add when the restored copy can reach your identity provider. On an isolated network it cannot, and the three above do not need it.

Clean up:

```
docker rm kairos-restore-test
docker volume rm kairos-restore-test
docker network rm kairos-restore-test
rm -rf restore after before.txt after.txt restore-test.env
```
