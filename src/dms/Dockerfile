# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The plugin tree, with host build output filtered out of it.
#
# The build stage below needs every source file under src/plugins, recursively, so that a file
# added to either plugin project reaches the image without anyone editing this Dockerfile. A
# directory copy is the only way to get that, and copying that tree straight from the parentdir
# named context is unsafe: src/dms/.dockerignore filters the default build context, but a named
# context is filtered by a .dockerignore of its own and src/ has none. Measured, after a local
# solution build: a direct directory copy carried the host's bin/, obj/ and results.sarif into the
# image, and obj/ arriving after the restore would land on top of the Linux-generated
# project.assets.json.
#
# So the tree is staged here and the generated directories are removed before the build stage
# sees it. This is deliberately not solved with a src/.dockerignore, which would also change the
# Configuration Service image's context, nor with COPY --exclude, which needs a labs Dockerfile
# frontend. The stage reuses the SDK image the build stage already pulls, so it costs no extra
# download.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS pluginsource

WORKDIR /plugins

# hadolint ignore=DL3022
COPY --from=parentdir plugins/ ./

RUN find . \( -type d \( -name bin -o -name obj \) \) -prune -exec rm -rf {} + && \
    find . -type f -name results.sarif -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS build

# /source is the common parent, holding only the files both trees share, and the DMS and plugin
# trees sit beside each other under it exactly as src/dms sits beside src/plugins in the
# repository. That layout is what lets MSBuild resolve each tree's own Directory.Build.props while
# both find the same Directory.Packages.props and nuget.config one level up, and it is why the
# working directory moves down to /source/dms rather than every DMS path moving up.
WORKDIR /source

# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./

# The plugin contract tree. Nothing in the image references these assemblies yet: they are staged
# by the story that creates the tree so that no merge order can leave the frontend pointing at a
# tree this build stage cannot reach, once the plugin startup guard adds that project reference.
#
# Only the project files and lock files are copied here, so that editing a source file does not
# invalidate the restore layer below. The sources themselves arrive after the restore, from the
# pluginsource stage.
# hadolint ignore=DL3022
COPY --from=parentdir plugins/Directory.Build.props ./plugins/
# hadolint ignore=DL3022
COPY --from=parentdir plugins/EdFi.Api.Plugins/*.csproj plugins/EdFi.Api.Plugins/packages.lock.json ./plugins/EdFi.Api.Plugins/
# hadolint ignore=DL3022
COPY --from=parentdir plugins/EdFi.Api.Plugins.Hosting/*.csproj plugins/EdFi.Api.Plugins.Hosting/packages.lock.json ./plugins/EdFi.Api.Plugins.Hosting/

WORKDIR /source/dms

COPY Directory.Build.props Directory.Build.targets ./

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/*.csproj frontend/EdFi.DataManagementService.Frontend.AspNetCore/packages.lock.json ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/*.csproj core/EdFi.DataManagementService.Core/packages.lock.json ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/*.csproj core/EdFi.DataManagementService.Core.External/packages.lock.json ./core/EdFi.DataManagementService.Core.External/
COPY core/EdFi.DataManagementService.CustomValidation/*.csproj core/EdFi.DataManagementService.CustomValidation/packages.lock.json ./core/EdFi.DataManagementService.CustomValidation/
COPY backend/EdFi.DataManagementService.Backend/*.csproj backend/EdFi.DataManagementService.Backend/packages.lock.json ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.External/*.csproj backend/EdFi.DataManagementService.Backend.External/packages.lock.json ./backend/EdFi.DataManagementService.Backend.External/
COPY backend/EdFi.DataManagementService.Backend.Ddl/*.csproj backend/EdFi.DataManagementService.Backend.Ddl/packages.lock.json ./backend/EdFi.DataManagementService.Backend.Ddl/
COPY backend/EdFi.DataManagementService.Backend.Mssql/*.csproj backend/EdFi.DataManagementService.Backend.Mssql/packages.lock.json ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Plans/*.csproj backend/EdFi.DataManagementService.Backend.Plans/packages.lock.json ./backend/EdFi.DataManagementService.Backend.Plans/
COPY backend/EdFi.DataManagementService.Backend.RelationalModel/*.csproj backend/EdFi.DataManagementService.Backend.RelationalModel/packages.lock.json ./backend/EdFi.DataManagementService.Backend.RelationalModel/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/*.csproj backend/EdFi.DataManagementService.Backend.Postgresql/packages.lock.json ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/*.csproj clis/EdFi.DataManagementService.ApiSchemaDownloader/packages.lock.json ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

# The plugin tree is restored even though nothing in the image references it yet. Staging its lock
# files without restoring them would leave the failure this change is most likely to produce - a
# committed lock file that --locked-mode rejects inside the image - undetected until the story
# that adds the frontend's project reference.
#
# Restoring Hosting alone covers both projects. It carries a ProjectReference to the contract, and
# restore walks that closure validating each project's own lock file, so a contract lock file
# inconsistent with the contract's csproj fails here as an NU1004 naming the contract. Invoking
# restore on the contract as well repeats that work and detects nothing further.
RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj --locked-mode && \
    dotnet restore clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj --locked-mode && \
    dotnet restore ../plugins/EdFi.Api.Plugins.Hosting/EdFi.Api.Plugins.Hosting.csproj --locked-mode

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ ./core/EdFi.DataManagementService.Core.External/
COPY core/EdFi.DataManagementService.CustomValidation/ ./core/EdFi.DataManagementService.CustomValidation/
COPY backend/EdFi.DataManagementService.Backend/ ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.External/ ./backend/EdFi.DataManagementService.Backend.External/
COPY backend/EdFi.DataManagementService.Backend.Ddl/ ./backend/EdFi.DataManagementService.Backend.Ddl/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Plans/ ./backend/EdFi.DataManagementService.Backend.Plans/
COPY backend/EdFi.DataManagementService.Backend.RelationalModel/ ./backend/EdFi.DataManagementService.Backend.RelationalModel/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/ ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

# Both plugin source trees, recursively, from the filtered stage above rather than from the
# named context. A whole-tree copy is what keeps a newly added file - nested or not, in either
# project - reaching the image without a change here, and it is why the empty Hosting project
# needs no special case: a directory copy of an empty tree is not a missing glob.
#
# This lands after the restore, exactly as the DMS source copies above do. It cannot disturb that
# restore's output because the staged tree has had its obj/ removed.
COPY --from=pluginsource /plugins/ /source/plugins/

ARG VERSION=""
ARG ASSEMBLY_VERSION=""

# Version-stamp arguments are accumulated as positional parameters so the expansion below
# stays quoted ("$@" preserves one argument per parameter; ShellCheck SC2086).
RUN set -- ; \
    if [ -n "$VERSION" ]; then \
        set -- "$@" "/p:Version=$VERSION" "/p:InformationalVersion=$VERSION"; \
    fi; \
    if [ -n "$ASSEMBLY_VERSION" ]; then \
        set -- "$@" "/p:AssemblyVersion=$ASSEMBLY_VERSION" "/p:FileVersion=$ASSEMBLY_VERSION"; \
    fi; \
    dotnet publish frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj \
    -c Release --no-restore --self-contained false -o /app/Frontend "$@" && \
    dotnet publish clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj \
    -c Release --no-restore --self-contained false -o /app/ApiSchemaDownloader "$@"

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.23@sha256:f03685b2735e0d3d25d6c60672e74b21bb6334f1402f71bae2d2cf02307163cd AS runtimebase

# Install OpenSSL on the 3.5 line and runtime helpers. icu-libs is required by
# Microsoft.Data.SqlClient (the MSSQL backend), which does not support Globalization
# Invariant Mode; the Alpine base image otherwise ships without ICU.
RUN apk add --no-cache openssl=~3.5 bash=~5 postgresql16-client=~16 jq=~1.8 icu-libs=~76

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080
# Microsoft.Data.SqlClient (MSSQL backend) does not support Globalization Invariant Mode,
# which the Alpine base image enables by default. ICU is installed in runtimebase above.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app

COPY --from=build /app/Frontend/*.dll ./
# runtimes/ exists in the publish output because Microsoft.Data.SqlClient (via Backend.Mssql)
# ships RID-specific assets; its Linux implementation loads from runtimes/unix/. If the Mssql
# backend reference is ever removed from the Frontend project, this COPY will fail the build.
COPY --from=build /app/Frontend/runtimes/ ./runtimes/
COPY --from=build /app/Frontend/ApiSchema/ ./ApiSchema/
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
COPY --from=build /app/ApiSchemaDownloader/*.* ./ApiSchemaDownloader/
COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
