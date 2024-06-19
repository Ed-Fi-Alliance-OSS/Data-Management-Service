# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.3-alpine3.19-amd64@sha256:a531d9d123928514405b9da9ff28a3aa81bd6f7d7d8cfb6207b66c007e7b3075 AS runtimebase

RUN apk --no-cache add curl=~8 unzip=~6 dos2unix=~7 bash=~5 gettext=~0

FROM runtimebase AS setup
LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

ENV LOG_LEVEL=${LOG_LEVEL}
ARG VERSION=0.0.0-alpha.0.108
ENV ASPNETCORE_HTTP_PORTS=8080

ENV POSTGRES_USER=${POSTGRES_USER}
ENV POSTGRES_PASSWORD=${POSTGRES_PASSWORD}

ENV POSTGRES_PORT=${POSTGRES_PORT}
ENV POSTGRES_HOST=${POSTGRES_HOST}

WORKDIR /app

RUN umask 0077 && \
    wget -O /app/EdFi.DataManagementService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DataManagementService.zip -d /app/ && \
    rm -f /app/EdFi.DataManagementService.zip && \
    chmod 700 /app/* -- **

EXPOSE ${ASPNETCORE_HTTP_PORTS}
CMD ["dotnet", "/app/DataManagementService/EdFi.DataManagementService.Frontend.AspNetCore.dll"]
