# Kairos in one image: the single page, the API that serves it, and nothing else.
#
# Three stages, because the two halves of this application are built by two
# toolchains and neither of them belongs in what ships. What reaches the runtime
# stage is the publish output and the built page — no SDK, no npm cache, no
# sources.
#
# Every base image is pinned by tag AND digest, the way compose.dev.yaml pins the
# mock provider: the tag records which release a human chose, the digest is what
# actually runs, and it is the half a re-tagged image cannot move underneath us.
#
# Both build stages are pinned to $BUILDPLATFORM and cross-publish to $TARGETARCH
# rather than running under emulation. Without that, a multi-architecture build
# puts npm and the whole .NET SDK through QEMU once per foreign architecture to
# produce byte-for-byte what cross-publishing already produces, since nothing here
# compiles to native code. Only the runtime stage stays emulated, and it has no RUN.

# ---- the single page --------------------------------------------------------
# Node 26.5.0, which is NODE_VERSION in .github/workflows/ci.yml. A different
# Node here would mean the bundle CI lints, tests and weighs is not the bundle
# this image serves.
#
# $BUILDPLATFORM, with nothing to reconcile afterwards: Vite emits JavaScript,
# CSS and fonts, and not one byte of that output depends on the architecture that
# produced it. This stage is built once and both architectures copy from it.
FROM --platform=$BUILDPLATFORM node:26.5.0-bookworm-slim@sha256:2d49d876e96237d76de412761cf05dbfe5aee325cc4406a4d41d5824c5bb8beb AS frontend

WORKDIR /src/frontend

# The manifests on their own first, so the install layer is rebuilt when a
# dependency changes and reused when only source did. `npm ci` rather than
# `npm install`: it installs the lockfile exactly and refuses a package.json
# that has drifted from it.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./

# vite.config.ts writes into ../backend/src/Kairos.Api/wwwroot with
# emptyOutDir: false, because the vendored kairos.css lives there and is not the
# build's to delete. Here that directory does not exist until Vite creates it, so
# what comes out is exactly this build's page, bundles and fonts.
#
# The manifest Vite also writes for scripts/bundle-budget.sh is left in place:
# `dotnet publish` skips dot-prefixed entries under wwwroot, so .vite/ never
# reaches the runtime stage — measured on a built image, not assumed.
RUN npm run build

# ---- the API ----------------------------------------------------------------
# SDK 10.0.101 exactly, the version backend/global.json names. Not the floating
# 10.0 tag: it currently carries 10.0.400, which is a different feature band,
# and `rollForward: latestPatch` does not cross one — that image cannot build
# this repository at all ("A compatible .NET SDK was not found", measured).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.101-noble@sha256:5504edd1267dd4deab3443f960cfab219249c8bd935fbcc358f1c24aeae23fe0 AS backend

WORKDIR /src/backend

ARG TARGETARCH

# Restore before the sources, so editing a .cs file reuses this layer instead of
# reading every package from nuget.org again. Everything copied here is an input
# to restore and nothing else is: global.json picks the SDK, NuGet.config is the
# only feed, and the two Directory.*.props carry the target framework and every
# version pin.
COPY backend/global.json backend/NuGet.config backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/src/Kairos.Api/Kairos.Api.csproj src/Kairos.Api/

# Docker spells the 64-bit Intel architecture amd64 and .NET spells it x64, so the
# two are reconciled once here rather than worked out twice. An architecture
# neither branch knows stops the build, because the alternative is an image that
# runs the wrong binaries and says nothing until it is started.
#
# Restoring FOR a runtime identifier is what lets the publish below pick one set of
# native assets; with no RID in the restore graph, --no-restore fails on the first
# RID-specific reference.
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 > /rid ;; \
      arm64) echo linux-arm64 > /rid ;; \
      *) echo "no runtime identifier known for TARGETARCH=$TARGETARCH" >&2; exit 1 ;; \
    esac \
 && dotnet restore src/Kairos.Api/Kairos.Api.csproj -r "$(cat /rid)"

COPY backend/ ./

# After the sources rather than before, because backend/ brings the committed
# kairos.css into wwwroot and this lays the built page down beside it. Both are
# content of a web project, so publish takes them without being told to.
COPY --from=frontend /src/backend/src/Kairos.Api/wwwroot/ src/Kairos.Api/wwwroot/

# Framework-dependent, and pinned to one runtime identifier. Without the RID the
# publish is architecture-agnostic and copies runtimes/ for every platform any
# dependency was built for — 23 of them, 34.2 MB, of which this image can execute
# exactly one. SQLitePCLRaw alone ships libe_sqlite3 for Windows, macOS,
# Mac Catalyst, browser-wasm, s390x, ppc64le, riscv64 and every musl variant.
# --no-self-contained keeps the .NET runtime itself in the base image where it
# belongs, so what the RID selects is the native assets and nothing more.
RUN dotnet publish src/Kairos.Api/Kairos.Api.csproj \
      -c Release -r "$(cat /rid)" --no-self-contained --no-restore -o /app

# ---- what ships -------------------------------------------------------------
# The chiselled ASP.NET runtime: no shell, no package manager, no ICU, and already
# non-root — its USER is 1654. Nothing here can be exec'd into, so every line below
# has to do its work at build time.
#
# No ICU is affordable because nothing in this application asks for it: there is no
# TimeZoneInfo anywhere in backend/src — every zone comes from NodaTime's bundled
# TZDB via DateTimeZoneProviders.Tzdb — and every CultureInfo and NodaTime pattern
# is the invariant one. backend/Directory.Build.props sets
# InvariantGlobalization=true to match, and this base image sets
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true whether it does or not.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled@sha256:0839314d08bb65da369135389a5d8291f75ace587fbb0488f469eb92c62eef68 AS runtime

# An absolute destination with no WORKDIR before it, deliberately: a directory
# COPY creates belongs to root, and a directory WORKDIR creates belongs to
# whoever USER is — here 1654, the account this process runs as. The application
# has no business being able to rewrite its own assemblies, so /app is root's and
# the process only reads it.
COPY --from=backend /app /app

# The data, outside the image. SQLite in WAL mode writes kairos.db and its -wal and
# -shm siblings, so what has to be writable is the DIRECTORY and not just the file.
# Those three and the data-protection key ring under .aspnet/ are the whole of what
# a redeploy must not lose and a backup has to take.
#
# Created with WORKDIR rather than declared by VOLUME alone: WORKDIR gives the
# directory to 1654, and Docker copies a fresh volume's ownership from the
# directory it is mounted over. Left to VOLUME on a path that is not in the image,
# the volume would come up owned by root and this process could not create its
# database.
WORKDIR /var/lib/kairos
VOLUME ["/var/lib/kairos"]

# /app already exists and belongs to root, so this only moves into it.
WORKDIR /app

# The one value this image supplies, supplied the way every other setting is:
# through the environment, section separators doubled, overridable by anything
# that sets it later. Program.cs falls back to a relative "Data Source=kairos.db"
# with no configuration at all, which would put the database on the container's
# own writable layer and throw it away with the container.
ENV ConnectionStrings__Db="Data Source=/var/lib/kairos/kairos.db"

# The key ring that signs the operator's session cookie lives at
# $HOME/.aspnet/DataProtection-Keys, and the base image sets no HOME of its own, so
# without this it lands on the container's writable layer and every recreated
# container signs the operator out. Pointed at the volume, it survives a redeploy
# and goes into the backup with the database. Overridable the same way.
ENV HOME=/var/lib/kairos

# The port the base image already points Kestrel at through ASPNETCORE_HTTP_PORTS.
# Stated here so `docker run -P` and anyone reading `docker image inspect` sees it.
EXPOSE 8080

# There is deliberately no HEALTHCHECK. This image has no shell and no curl to
# write one with; /healthz and /readyz are HTTP endpoints an orchestrator or a
# proxy probes directly, which is where the reference deployment probes them;
# and a second, weaker liveness mechanism living inside the image is a second
# thing that has to be kept true.

ENTRYPOINT ["dotnet", "Kairos.Api.dll"]
