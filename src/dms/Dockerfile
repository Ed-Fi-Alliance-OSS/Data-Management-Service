# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23@sha256:9b4b31da5246f575086b1901e9871b189ae2a80eb42fe9234e9d000b51febd4b AS build

WORKDIR /source

# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig Directory.Packages.props nuget.config ./

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/*.csproj ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/*.csproj ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/*.csproj ./core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/*.csproj ./backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/*.csproj ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.External/*.csproj ./backend/EdFi.DataManagementService.Backend.External/
COPY backend/EdFi.DataManagementService.Backend.Ddl/*.csproj ./backend/EdFi.DataManagementService.Backend.Ddl/
COPY backend/EdFi.DataManagementService.Backend.Mssql/*.csproj ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Plans/*.csproj ./backend/EdFi.DataManagementService.Backend.Plans/
COPY backend/EdFi.DataManagementService.Backend.RelationalModel/*.csproj ./backend/EdFi.DataManagementService.Backend.RelationalModel/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/*.csproj ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Old.Postgresql/*.csproj ./backend/EdFi.DataManagementService.Old.Postgresql/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/*.csproj ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj && \
    dotnet restore backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj && \
    dotnet restore clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ ./core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/ ./backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/ ./backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.External/ ./backend/EdFi.DataManagementService.Backend.External/
COPY backend/EdFi.DataManagementService.Backend.Ddl/ ./backend/EdFi.DataManagementService.Backend.Ddl/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ ./backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Plans/ ./backend/EdFi.DataManagementService.Backend.Plans/
COPY backend/EdFi.DataManagementService.Backend.RelationalModel/ ./backend/EdFi.DataManagementService.Backend.RelationalModel/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ ./backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Old.Postgresql/ ./backend/EdFi.DataManagementService.Old.Postgresql/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/ ./clis/EdFi.DataManagementService.ApiSchemaDownloader/

RUN dotnet publish frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj \
    -c Release --no-restore --self-contained false -o /app/Frontend && \
    dotnet publish backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj \
    -c Release --no-restore --self-contained false -o /app/Installer && \
    dotnet publish clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj \
    -c Release --no-restore --self-contained false -o /app/ApiSchemaDownloader

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-alpine3.23@sha256:258b939d6d684ff05ad7ea16782b4bee55260de4acc6f99bec897fd11de7640c AS runtimebase

# Upgrade OpenSSL and install runtime helpers
RUN apk add --no-cache --upgrade openssl bash=~5 postgresql16-client=~16 jq=~1.8

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Installer/*.* ./Installer/
COPY --from=build /app/Frontend/ApiSchema/*.json ./ApiSchema/
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
COPY --from=build /app/ApiSchemaDownloader/*.* ./ApiSchemaDownloader/
COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
