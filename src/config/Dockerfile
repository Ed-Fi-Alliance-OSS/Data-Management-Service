# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The plugin tree, with host build output filtered out of it.
#
# The build stage below needs every source file under src/plugins, recursively, so that a file
# added to either plugin project reaches the image without anyone editing this Dockerfile. A
# directory copy is the only way to get that, and copying that tree straight from the parentdir
# named context is unsafe: src/config/.dockerignore filters the default build context, but a named
# context is filtered by a .dockerignore of its own and src/ has none. A direct directory copy
# after a local solution build would carry the host's bin/, obj/ and results.sarif into the image,
# and obj/ arriving after the restore would land on top of the Linux-generated
# project.assets.json.
#
# So the tree is staged here and the generated directories are removed before the build stage
# sees it. This is deliberately not solved with a src/.dockerignore, which would also change the
# Data Management Service image's context, nor with COPY --exclude, which needs a labs Dockerfile
# frontend. The stage reuses the SDK image the build stage already pulls, so it costs no extra
# download.
FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS pluginsource

WORKDIR /plugins

# hadolint ignore=DL3022
COPY --from=parentdir plugins/ ./

RUN find . \( -type d \( -name bin -o -name obj \) \) -prune -exec rm -rf {} + && \
    find . -type f -name results.sarif -delete

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS build

# /source is the common parent, holding only the files both trees share, and the Configuration
# Service and plugin trees sit beside each other under it exactly as src/config sits beside
# src/plugins in the repository. That layout is what lets MSBuild resolve each tree's own
# Directory.Build.props while both find the same Directory.Packages.props and nuget.config one
# level up, and it is why the working directory moves down to /source/config rather than every
# Configuration Service path moving up.
WORKDIR /source

# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./

# The plugin tree the frontend references. Only the project files and lock files are copied here,
# so that editing a source file does not invalidate the restore layer below. The sources themselves
# arrive after the restore, from the pluginsource stage.
# hadolint ignore=DL3022
COPY --from=parentdir plugins/Directory.Build.props ./plugins/
# hadolint ignore=DL3022
COPY --from=parentdir plugins/EdFi.Api.Plugins/*.csproj plugins/EdFi.Api.Plugins/packages.lock.json ./plugins/EdFi.Api.Plugins/
# hadolint ignore=DL3022
COPY --from=parentdir plugins/EdFi.Api.Plugins.Hosting/*.csproj plugins/EdFi.Api.Plugins.Hosting/packages.lock.json ./plugins/EdFi.Api.Plugins.Hosting/

WORKDIR /source/config

COPY Directory.Build.props ./

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/*.csproj frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/packages.lock.json ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/*.csproj backend/EdFi.DmsConfigurationService.Backend/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/*.csproj backend/EdFi.DmsConfigurationService.Backend.Keycloak/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/*.csproj backend/EdFi.DmsConfigurationService.Backend.OpenIddict/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/*.csproj backend/EdFi.DmsConfigurationService.Backend.Mssql/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/*.csproj backend/EdFi.DmsConfigurationService.Backend.Postgresql/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/*.csproj datamodel/EdFi.DmsConfigurationService.DataModel/packages.lock.json ./datamodel/EdFi.DmsConfigurationService.DataModel/
COPY contracts/EdFi.DmsConfigurationService.Secrets/*.csproj contracts/EdFi.DmsConfigurationService.Secrets/packages.lock.json ./contracts/EdFi.DmsConfigurationService.Secrets/

# The frontend's restore already walks its ProjectReference closure into the plugin tree. Hosting
# is restored as well, mirroring the Data Management Service image, so the plugin tree's own lock
# files are validated in --locked-mode on their own terms rather than only as the frontend
# happens to reach them. Restoring Hosting covers the contract too, through its ProjectReference.
RUN dotnet restore frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj --locked-mode && \
    dotnet restore ../plugins/EdFi.Api.Plugins.Hosting/EdFi.Api.Plugins.Hosting.csproj --locked-mode

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/ ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/ ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/ ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/ ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/ ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/ ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/ ./datamodel/EdFi.DmsConfigurationService.DataModel/
COPY contracts/EdFi.DmsConfigurationService.Secrets/ ./contracts/EdFi.DmsConfigurationService.Secrets/

# Both plugin source trees, recursively, from the filtered stage above rather than from the named
# context. A whole-tree copy is what keeps a newly added file - nested or not, in either project -
# reaching the image without a change here.
#
# This lands after the restore, exactly as the Configuration Service source copies above do. It
# cannot disturb that restore's output because the staged tree has had its obj/ removed.
COPY --from=pluginsource /plugins/ /source/plugins/

ARG VERSION=""

# Version and InformationalVersion only. AssemblyVersion and FileVersion are deliberately not
# passed, and nothing replaces them: a locally built image's Configuration Service assemblies carry
# whatever Directory.Build.props declares, which nothing reads out of a development image, while
# the release lane still stamps them through build-config.ps1 BuildAndPublish. The secrets contract
# keeps its own declared version either way (see its csproj), and so does the plugin tree: its
# AssemblyVersion comes from src/plugins/Directory.Build.props, which the loader's
# newer-plugin-on-older-host preflight compares, and no global property here overrides it.
RUN version_args=""; \
    if [ -n "$VERSION" ]; then \
        version_args="$version_args /p:Version=$VERSION /p:InformationalVersion=$VERSION"; \
    fi; \
    dotnet publish frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj \
        -c Release --no-restore --self-contained false -o /app/Frontend \
        $version_args

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23@sha256:258b939d6d684ff05ad7ea16782b4bee55260de4acc6f99bec897fd11de7640c AS runtimebase

# Install OpenSSL on the 3.5 line and runtime helpers; ICU because
# Microsoft.Data.SqlClient does not support globalization-invariant mode
RUN apk add --no-cache openssl=~3.5 bash=~5 postgresql18-client=~18 icu-libs=~76 icu-data-full=~76

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
# Platform-specific assemblies (e.g. Microsoft.Data.SqlClient's unix implementation)
# resolve from runtimes/ per deps.json; without it the MSSQL datastore cannot load.
COPY --from=build /app/Frontend/runtimes/ ./runtimes/
COPY --from=build /app/Frontend/Schema/ ./Schema/

COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
