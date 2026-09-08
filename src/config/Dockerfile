# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS build

WORKDIR /source
# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./
COPY Directory.Build.props ./

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/*.csproj frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/packages.lock.json ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/*.csproj backend/EdFi.DmsConfigurationService.Backend/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/*.csproj backend/EdFi.DmsConfigurationService.Backend.Keycloak/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/*.csproj backend/EdFi.DmsConfigurationService.Backend.OpenIddict/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/*.csproj backend/EdFi.DmsConfigurationService.Backend.Mssql/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/*.csproj backend/EdFi.DmsConfigurationService.Backend.Postgresql/packages.lock.json ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/*.csproj datamodel/EdFi.DmsConfigurationService.DataModel/packages.lock.json ./datamodel/EdFi.DmsConfigurationService.DataModel/

RUN dotnet restore frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj --locked-mode

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/ ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/ ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/ ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/ ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/ ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/ ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/ ./datamodel/EdFi.DmsConfigurationService.DataModel/

ARG VERSION=""
ARG ASSEMBLY_VERSION=""

RUN version_args=""; \
    if [ -n "$VERSION" ]; then \
        version_args="$version_args /p:Version=$VERSION /p:InformationalVersion=$VERSION"; \
    fi; \
    if [ -n "$ASSEMBLY_VERSION" ]; then \
        version_args="$version_args /p:AssemblyVersion=$ASSEMBLY_VERSION /p:FileVersion=$ASSEMBLY_VERSION"; \
    fi; \
    dotnet publish frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj \
        -c Release --no-restore --self-contained false -o /app/Frontend \
        $version_args

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23@sha256:258b939d6d684ff05ad7ea16782b4bee55260de4acc6f99bec897fd11de7640c AS runtimebase

# Install OpenSSL on the 3.5 line and runtime helpers; ICU because
# Microsoft.Data.SqlClient does not support globalization-invariant mode
RUN apk add --no-cache openssl=~3.5 bash=~5 postgresql16-client=~16 icu-libs=~76 icu-data-full=~76

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
