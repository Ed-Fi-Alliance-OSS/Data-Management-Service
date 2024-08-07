# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.203-alpine3.18@sha256:2a8dca3af111071172b1629c12eefaeca0d6c2954887c4489195771c9e90833c AS build

WORKDIR /source

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/ backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/ backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ backend/EdFi.DataManagementService.Backend.Postgresql/
COPY .editorconfig .

COPY Directory.Packages.props ./
COPY nuget.config .
RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj && \
    dotnet restore backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj

WORKDIR /source/frontend/EdFi.DataManagementService.Frontend.AspNetCore
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release --no-build --no-restore -o /app/Frontend

WORKDIR /source/backend/EdFi.DataManagementService.Backend.Installer
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release -o /app/Installer --no-restore --no-build

FROM mcr.microsoft.com/dotnet/aspnet:8.0.3-alpine3.19-amd64@sha256:a531d9d123928514405b9da9ff28a3aa81bd6f7d7d8cfb6207b66c007e7b3075 AS runtimebase

RUN apk --no-cache add bash=~5 postgresql16-client=~16 gettext=~0

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080
ENV OAUTH_TOKEN_ENDPOINT=${OAUTH_TOKEN_ENDPOINT}
ENV NEED_DATABASE_SETUP=${NEED_DATABASE_SETUP}
ENV BYPASS_STRING_COERCION=${BYPASS_STRING_COERCION}
ENV CORRELATION_ID_HEADER=${CORRELATION_ID_HEADER}
ENV DATABASE_ISOLATION_LEVEL=${DATABASE_ISOLATION_LEVEL}
ENV POSTGRES_ADMIN_USER=${POSTGRES_ADMIN_USER}
ENV POSTGRES_ADMIN_PASSWORD=${POSTGRES_ADMIN_PASSWORD}

ENV DATABASE_CONNECTION_STRING=${DATABASE_CONNECTION_STRING}

ENV POSTGRES_PORT=${POSTGRES_PORT}
ENV POSTGRES_HOST=${POSTGRES_HOST}
ENV LOG_LEVEL=${LOG_LEVEL}

WORKDIR /app

COPY --chmod=600 appsettings.template.json /app/appsettings.template.json

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Installer/*.* ./Installer/
COPY --from=build /app/Frontend/ApiSchema/*.json ./ApiSchema/
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./

COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]

