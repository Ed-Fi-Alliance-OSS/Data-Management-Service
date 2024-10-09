# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.401-alpine3.20@sha256:658c93223111638f9bb54746679e554b2cf0453d8fb7b9fed32c3c0726c210fe AS build

WORKDIR /source

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/ backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/ backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Backend.OpenSearch/ backend/EdFi.DataManagementService.Backend.OpenSearch/
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

FROM mcr.microsoft.com/dotnet/aspnet:8.0.8-alpine3.20-amd64@sha256:98fa594b91cda6cac28d2aae25567730db6f8857367fab7646bdda91bc784b5f AS runtimebase

# bash: used in startup script and debugging
# postgresql: used to test for PostgreSQL readiness
# gettext: provides `envsubst`, used to inject env values into appsettings
RUN apk --no-cache add bash=~5 postgresql16-client=~16 gettext=~0

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080

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

