# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Local image for the dms-document-cache tool, used by the bootstrap CDC phase. The phase runs
# the tool as a one-shot container on the dms network because the work it does is only reachable
# from inside it: the instance database is registered in CMS under its container alias, and the
# broker advertises PLAINTEXT://dms-kafka1:9092, so a host-side process is redirected to a name
# it cannot resolve. This image is a development artifact; the published tool package is built by
# build-dms.ps1 -Command Package -PackageTarget DocumentCacheAdmin.

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS build

WORKDIR /source

# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./
COPY Directory.Build.props Directory.Build.targets ./

# The tool's reference graph spans core/ and most of backend/, and .dockerignore already drops
# bin/, obj/, and every test project. Copying those two trees whole is deliberately simpler than
# the frontend image's per-project csproj/lock layering: this image is rebuilt only by the CDC
# opt-in, so build-cache granularity is worth less here than a source list that cannot drift out
# of step with the tool's project references.
COPY core/ ./core/
COPY backend/ ./backend/
COPY clis/EdFi.DataManagementService.DocumentCacheAdmin/ ./clis/EdFi.DataManagementService.DocumentCacheAdmin/

RUN dotnet publish clis/EdFi.DataManagementService.DocumentCacheAdmin/EdFi.DataManagementService.DocumentCacheAdmin.csproj \
    -c Release --self-contained false -o /app/DocumentCacheAdmin

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.23@sha256:f03685b2735e0d3d25d6c60672e74b21bb6334f1402f71bae2d2cf02307163cd AS runtime

# Microsoft.Data.SqlClient (the MSSQL backend) does not support Globalization Invariant Mode,
# which the Alpine base image enables by default, so ICU is installed and the switch turned off.
RUN apk add --no-cache icu-libs=~76
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app

COPY --from=build /app/DocumentCacheAdmin/ ./

# The durable CDC binding state store is a bind mount, and Docker Desktop presents a bind-mounted
# directory as world-writable (0777) whatever the host's own permissions are - including one the
# host created itself. LocalCdcBindingStateStore refuses a group- or world-writable store root, so
# without this every cdc verb fails at its first binding read with `LocalStateUnavailable`. The
# mount point is the one directory in the store's tree that the store never creates for itself, so
# the image tightens it here, immediately before the tool runs.
#
# Only the two bits the store rejects are cleared, so an already-compliant root keeps the mode it
# has: a native Linux bind mount is typically 0755, which `chmod g-w,o-w` leaves untouched. Owner
# bits and ownership are never changed either, so the host user keeps the read access the
# retirement's host-side binding discovery needs.
#
# DMS_CDC_STATE_ROOT names the mount, and cdc-setup.yml sets it to the same path it mounts and
# passes as --cdc-binding-state-path. Unset - which is every non-CDC invocation of this image -
# tightens nothing.
#
# The tool is still invoked through the runtime rather than the published apphost, so the image
# does not depend on the build stage having produced a musl-matched native host. Nothing here is
# expanded at build time: a JSON-array ENTRYPOINT is not shell-processed by Docker, so
# $DMS_CDC_STATE_ROOT and "$@" reach /bin/sh literally. The trailing "--" becomes $0, which is
# what leaves "$@" holding the cdc verb and its arguments.
ENTRYPOINT ["/bin/sh", "-c", "if [ -n \"$DMS_CDC_STATE_ROOT\" ] && [ -d \"$DMS_CDC_STATE_ROOT\" ]; then chmod g-w,o-w \"$DMS_CDC_STATE_ROOT\"; fi; exec dotnet /app/dms-document-cache.dll \"$@\"", "--"]
