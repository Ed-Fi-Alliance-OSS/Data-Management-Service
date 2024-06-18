# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.3-alpine3.19-amd64@sha256:a531d9d123928514405b9da9ff28a3aa81bd6f7d7d8cfb6207b66c007e7b3075 AS runtimebase

FROM runtimebase AS setup

ENV LOG_LEVEL=${LOG_LEVEL}
ARG VERSION=0.0.0-alpha.0.102

WORKDIR /app

RUN umask 0077 && \
    #wget -O /app/EdFi.DataManagementService.nupkg "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService.Frontend.AspNetCore/versions/${VERSION}/content" && \
    wget -O /app/EdFi.DataManagementService.nupkg "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService.Frontend.AspNetCore/versions/${VERSION}/content" && \
    unzip /app/EdFi.DataManagementService.nupkg -d /app/ && \
    #nuget install /app/EdFi.DataManagementService.nupkg -Source %cd% -Nuget packages
    rm -f /app/EdFi.DataManagementService.nupkg && \
    #cp /app/log4net.txt /app/log4net.config && \
    dos2unix /app/content/*.json
    #dos2unix /app/*.sh && \
    #dos2unix /app/log4net.config && \
    #chmod 700 /app/*.sh -- ** && \
    #rm -f /app/*.exe && \
    #apk del unzip dos2unix curl && \
    #chown -R edfi /app

    #wget -nv -O /app/EdFi.DataManagementService.zip "https://dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_artifacts/feed/EdFi/NuGet/EdFi.DataManagementService/overview/0.0.0-alpha.0.8" && \
    #wget -nv -O /app/EdFi.DataManagementService.zip "" && \
    #unzip /app/EdFi.DataManagementService.zip EdFi.DataManagementService/* -d /app/ && \
    #cp -r /app/EdFi.DataManagementService/* /app/ && \
    #rm -f /app/EdFi.DataManagementService.zip && \
    #rm -r /app/EdFi.DataManagementService && \
    ##cp /app/log4net.txt /app/log4net.config && \
    #dos2unix /app/*.json && \
    #dos2unix /app/*.sh && \
    ##dos2unix /app/log4net.config && \
    #chmod 700 /app/*.sh -- ** && \
    #rm -f /app/*.exe && \
    #apk del unzip dos2unix curl && \
    #chown -R edfi /app

#EXPOSE ${ASPNETCORE_HTTP_PORTS}
#USER edfi

ENTRYPOINT [ "ls" ]
