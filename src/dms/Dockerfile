# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

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
# Sources are copied file by file rather than as whole directories. The default build context is
# governed by src/dms/.dockerignore, which excludes bin/ and obj/; the parentdir named context has
# no such file, so a directory copy would drop a developer's host build output on top of the
# Linux restore performed below.
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

# Both plugin projects are restored even though nothing references them yet. Staging their lock
# files without restoring them would leave the failure this change is most likely to produce - a
# committed lock file that --locked-mode rejects inside the image - undetected until the story
# that adds the frontend's project reference.
RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj --locked-mode && \
    dotnet restore clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj --locked-mode && \
    dotnet restore ../plugins/EdFi.Api.Plugins/EdFi.Api.Plugins.csproj --locked-mode && \
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

# EdFi.Api.Plugins.Hosting is deliberately empty today, so it has no source line here; its csproj
# is already staged above and the restore above proves its lock file. The story that gives it
# code adds its source copy.
# hadolint ignore=DL3022
COPY --from=parentdir plugins/EdFi.Api.Plugins/*.cs plugins/EdFi.Api.Plugins/PLUGINS.md /source/plugins/EdFi.Api.Plugins/

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
