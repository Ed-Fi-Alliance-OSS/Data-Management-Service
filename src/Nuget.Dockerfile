# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.3-alpine3.19-amd64@sha256:a531d9d123928514405b9da9ff28a3aa81bd6f7d7d8cfb6207b66c007e7b3075 AS runtimebase

FROM runtimebase AS setup

ENV LOG_LEVEL=${LOG_LEVEL}
ARG VERSION=0.0.0-alpha.0.8

WORKDIR /app

RUN umask 0077 && \
    wget -O /app/EdFi.DataManagementService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DataManagementService.zip -d /app/ && \
    rm -f /app/EdFi.DataManagementService.zip && \
    chmod 700 /app/* -- **

EXPOSE ${ASPNETCORE_HTTP_PORTS}
ENTRYPOINT ["dotnet", "/app/EdFi.DataManagementService.Api.dll"]
