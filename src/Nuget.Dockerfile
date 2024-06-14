# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.3-alpine3.19-amd64@sha256:a531d9d123928514405b9da9ff28a3aa81bd6f7d7d8cfb6207b66c007e7b3075 AS runtimebase

FROM runtimebase AS setup

ENV LOG_LEVEL=${LOG_LEVEL}

WORKDIR /TBD

RUN umask 0077 && \
    # wget -nv -O /app/AdminApi.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.Suite3.ODS.AdminApi/versions/${VERSION}/content" && \
    wget -nv -O /app/<FILENAME_HERE>.zip "" && \
    unzip /app/FILENAME_HERE.zip AdminApi/* -d /app/ && \
    cp -r /app/FILENAME_HERE/. /app/ && \
    rm -f /app/FILENAME_HERE.zip && \
    rm -r /app/FILENAME_HERE && \
    cp /app/log4net.txt /app/log4net.config && \
    dos2unix /app/*.json && \
    dos2unix /app/*.sh && \
    dos2unix /app/log4net.config && \
    chmod 700 /app/*.sh -- ** && \
    rm -f /app/*.exe && \
    apk del unzip dos2unix curl && \
    chown -R edfi /app

USER edfi

ENTRYPOINT [ "/app/run.sh" ]
