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

# Invoked through the runtime rather than the published apphost so the image does not depend on
# the build stage having produced a musl-matched native host.
ENTRYPOINT ["dotnet", "/app/dms-document-cache.dll"]
