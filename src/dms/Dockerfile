# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.401-alpine3.20@sha256:658c93223111638f9bb54746679e554b2cf0453d8fb7b9fed32c3c0726c210fe AS build

WORKDIR /source

# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/*.csproj ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/*.csproj ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/*.csproj ./core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/*.csproj ./backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/*.csproj ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.Mssql/*.csproj ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/*.csproj ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Backend.OpenSearch/*.csproj ./backend/EdFi.DataManagementService.Backend.OpenSearch/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/*.csproj ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj && \
    dotnet restore backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj && \
    dotnet restore clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj & \
    wait

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ ./core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/ ./backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/ ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Backend.OpenSearch/ ./backend/EdFi.DataManagementService.Backend.OpenSearch/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/ ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

RUN dotnet publish /source/frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj \
    -c Release --no-restore --self-contained false -o /app/Frontend && \
    dotnet publish /source/backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj \
    -c Release --no-restore --self-contained false -o /app/Installer && \
    dotnet publish /source/clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj \
    -c Release --no-restore --self-contained false -o /app/ApiSchemaDownloader

FROM mcr.microsoft.com/dotnet/aspnet:8.0.12-alpine3.21@sha256:accc7352721d44ef6246e91704f4efb1954f69912af2d2bd54d117fa09922a53 AS runtimebase

# bash: used in startup script and debugging
# postgresql: used to test for PostgreSQL readiness
# gettext: provides `envsubst`, used to inject env values into appsettings
# jq: used to parse plugins defined in JSON env variable
RUN apk --no-cache add bash=~5 postgresql16-client=~16 gettext=~0 jq=~1.7

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080
ENV START_YEAR=1991
ENV END_YEAR=2037
ENV CURRENT_SCHOOL_YEAR=2025

WORKDIR /app

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Installer/*.* ./Installer/
COPY --from=build /app/Frontend/ApiSchema/*.json ./ApiSchema/
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
COPY --from=build /app/ApiSchemaDownloader/*.* ./ApiSchemaDownloader/
COPY --chmod=600 appsettings.template.json /app/appsettings.template.json
COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
