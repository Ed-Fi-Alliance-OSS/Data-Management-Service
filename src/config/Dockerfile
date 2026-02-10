# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:10.0.101-alpine3.23@sha256:b6e895da9d359739e8cff0c296c28eaac24697d44af63f3a7099a6fd0dfe3be7 AS build

WORKDIR /source
# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/*.csproj ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/*.csproj ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/*.csproj ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/*.csproj ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/*.csproj ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/*.csproj ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/*.csproj ./datamodel/EdFi.DmsConfigurationService.DataModel/

RUN dotnet restore frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj

COPY frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/ ./frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/
COPY backend/EdFi.DmsConfigurationService.Backend/ ./backend/EdFi.DmsConfigurationService.Backend/
COPY backend/EdFi.DmsConfigurationService.Backend.Keycloak/ ./backend/EdFi.DmsConfigurationService.Backend.Keycloak/
COPY backend/EdFi.DmsConfigurationService.Backend.OpenIddict/ ./backend/EdFi.DmsConfigurationService.Backend.OpenIddict/
COPY backend/EdFi.DmsConfigurationService.Backend.Mssql/ ./backend/EdFi.DmsConfigurationService.Backend.Mssql/
COPY backend/EdFi.DmsConfigurationService.Backend.Postgresql/ ./backend/EdFi.DmsConfigurationService.Backend.Postgresql/
COPY datamodel/EdFi.DmsConfigurationService.DataModel/ ./datamodel/EdFi.DmsConfigurationService.DataModel/

RUN dotnet publish frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj \
    -c Release --no-restore --self-contained false -o /app/Frontend

FROM mcr.microsoft.com/dotnet/aspnet:10.0.1-alpine3.23@sha256:2273b24ea865e253d5e3b66d0eae23ce53529946089819489410849ba62db12c AS runtimebase

# Upgrade OpenSSL to fix CVE-2025-15467 and install runtime helpers
RUN apk --no-cache upgrade openssl=3.5.5-r0 && \
    apk --no-cache add bash=~5 postgresql16-client=~16

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
COPY --from=build /app/Frontend/Schema/ ./Schema/

COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
