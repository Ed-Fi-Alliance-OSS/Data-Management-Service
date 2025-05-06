# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.401-alpine3.20@sha256:658c93223111638f9bb54746679e554b2cf0453d8fb7b9fed32c3c0726c210fe AS build

WORKDIR /source

COPY frontend/EdFi.DataManagementService.Frontend.AspNetCore/ ./frontend/EdFi.DataManagementService.Frontend.AspNetCore/
COPY frontend/EdFi.DataManagementService.Frontend.SchoolYearLoader/ ./frontend/EdFi.DataManagementService.Frontend.SchoolYearLoader/
COPY core/EdFi.DataManagementService.Core/ ./core/EdFi.DataManagementService.Core/
COPY core/EdFi.DataManagementService.Core.External/ core/EdFi.DataManagementService.Core.External/
COPY backend/EdFi.DataManagementService.Backend.Installer/ backend/EdFi.DataManagementService.Backend.Installer/
COPY backend/EdFi.DataManagementService.Backend/ backend/EdFi.DataManagementService.Backend/
COPY backend/EdFi.DataManagementService.Backend.Mssql/ backend/EdFi.DataManagementService.Backend.Mssql/
COPY backend/EdFi.DataManagementService.Backend.Postgresql/ backend/EdFi.DataManagementService.Backend.Postgresql/
COPY backend/EdFi.DataManagementService.Backend.OpenSearch/ backend/EdFi.DataManagementService.Backend.OpenSearch/
COPY clis/EdFi.DataManagementService.ApiSchemaDownloader/ clis/EdFi.DataManagementService.ApiSchemaDownloader/
# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir .editorconfig .
# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir Directory.Packages.props ./
# Named context support https://github.com/hadolint/hadolint/issues/830
# hadolint ignore=DL3022
COPY --from=parentdir nuget.config .

RUN dotnet restore frontend/EdFi.DataManagementService.Frontend.SchoolYearLoader/EdFi.DataManagementService.Frontend.SchoolYearLoader.csproj && \
    dotnet restore frontend/EdFi.DataManagementService.Frontend.AspNetCore/EdFi.DataManagementService.Frontend.AspNetCore.csproj && \
    dotnet restore backend/EdFi.DataManagementService.Backend.Installer/EdFi.DataManagementService.Backend.Installer.csproj && \
    dotnet restore clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj

WORKDIR /source/frontend/EdFi.DataManagementService.Frontend.SchoolYearLoader
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release --no-build --no-restore -o /app/SchoolYearLoader

WORKDIR /source/frontend/EdFi.DataManagementService.Frontend.AspNetCore
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release --no-build --no-restore -o /app/Frontend

WORKDIR /source/backend/EdFi.DataManagementService.Backend.Installer
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release -o /app/Installer --no-restore --no-build

WORKDIR /source/clis/EdFi.DataManagementService.ApiSchemaDownloader
RUN dotnet build -c Release --no-restore && \
    dotnet publish -c Release -o /app/ApiSchemaDownloader --no-restore --no-build

FROM mcr.microsoft.com/dotnet/aspnet:8.0.12-alpine3.21@sha256:accc7352721d44ef6246e91704f4efb1954f69912af2d2bd54d117fa09922a53 AS runtimebase

# bash: used in startup script and debugging
# postgresql: used to test for PostgreSQL readiness
# gettext: provides `envsubst`, used to inject env values into appsettings
RUN apk --no-cache add bash=~5 postgresql16-client=~16 gettext=~0

FROM runtimebase AS setup

ENV ASPNETCORE_HTTP_PORTS=8080
ENV CORE_PACKAGE=EdFi.DataStandard52.ApiSchema
ENV CORE_PACKAGE_VERSION=1.0.192
ENV TPDM_PACKAGE=EdFi.TPDM.ApiSchema
ENV TPDM_PACKAGE_VERSION=1.0.192
ENV SAMPLE_PACKAGE=EdFi.Sample.ApiSchema
ENV SAMPLE_PACKAGE_VERSION=1.0.192
ENV HOMOGRAPH_PACKAGE=EdFi.Homograph.ApiSchema
ENV HOMOGRAPH_PACKAGE_VERSION=1.0.192
ENV START_YEAR=1991
ENV END_YEAR=2037
ENV CURRENT_SCHOOL_YEAR=2025

WORKDIR /app

COPY --chmod=600 appsettings.template.json /app/appsettings.template.json

COPY --from=build /app/Frontend/*.dll ./
COPY --from=build /app/Installer/*.* ./Installer/
COPY --from=build /app/Frontend/ApiSchema/*.json ./ApiSchema/
COPY --from=build /app/Frontend/*.pdb ./
COPY --from=build /app/Frontend/appsettings.json ./
COPY --from=build /app/Frontend/*runtimeconfig.json ./
COPY --from=build /app/Frontend/*.deps.json ./
COPY --from=build /app/ApiSchemaDownloader/*.* ./ApiSchemaDownloader/
COPY --from=build /app/SchoolYearLoader/*.dll ./SchoolYearLoader/
COPY --from=build /app/SchoolYearLoader/ApiSchema/*.json ./SchoolYearLoader/ApiSchema/
COPY --from=build /app/SchoolYearLoader/*.pdb ./SchoolYearLoader/
COPY --from=build /app/SchoolYearLoader/appsettings.json ./SchoolYearLoader/
COPY --from=build /app/SchoolYearLoader/*runtimeconfig.json ./SchoolYearLoader/
COPY --from=build /app/SchoolYearLoader/*.deps.json ./SchoolYearLoader/

COPY --chmod=700 run.sh /app/run.sh

EXPOSE ${ASPNETCORE_HTTP_PORTS}

ENTRYPOINT ["/app/run.sh"]
