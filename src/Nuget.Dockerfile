# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.8-alpine3.20-amd64@sha256:98fa594b91cda6cac28d2aae25567730db6f8857367fab7646bdda91bc784b5f AS runtimebase

LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

RUN apk --no-cache add gettext=~0 postgresql16-client=~16

FROM runtimebase AS setup

ENV LOG_LEVEL=${LOG_LEVEL}
ARG VERSION=0.0.0
ENV ASPNETCORE_HTTP_PORTS=8080
ENV OAUTH_TOKEN_ENDPOINT=${OAUTH_TOKEN_ENDPOINT}
ENV BYPASS_STRING_COERCION=${BYPASS_STRING_COERCION}
ENV CORRELATION_ID_HEADER=${CORRELATION_ID_HEADER}
ENV DATABASE_ISOLATION_LEVEL=${DATABASE_ISOLATION_LEVEL}

ENV FAILURE_RATIO=${FAILURE_RATIO}
ENV SAMPLING_DURATION_SECONDS=${SAMPLING_DURATION_SECONDS}
ENV MINIMUM_THROUGHPUT=${MINIMUM_THROUGHPUT}
ENV BREAK_DURATION_SECONDS=${BREAK_DURATION_SECONDS}

ENV DATABASE_CONNECTION_STRING=${DATABASE_CONNECTION_STRING}

ENV POSTGRES_PORT=${POSTGRES_PORT}
ENV POSTGRES_HOST=${POSTGRES_HOST}

ENV NEED_DATABASE_SETUP=${NEED_DATABASE_SETUP}
ENV POSTGRES_ADMIN_USER=${POSTGRES_ADMIN_USER}
ENV POSTGRES_ADMIN_PASSWORD=${POSTGRES_ADMIN_PASSWORD}

ENV POSTGRES_PORT=${POSTGRES_PORT}
ENV POSTGRES_HOST=${POSTGRES_HOST}
ENV LOG_LEVEL=${LOG_LEVEL}

WORKDIR /app

RUN echo "Tag Version:" $VERSION

RUN wget -O /app/EdFi.DataManagementService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DataManagementService.zip -d /app/ && \
    cp -r /app/DataManagementService/. /app/ && \
    cp -r /app/Installer/. /app/. && \
    rm -f /app/EdFi.DataManagementService.zip && \
    rm -r /app/DataManagementService

COPY --chmod=600 appsettings.template.json /app/appsettings.template.json

COPY --chmod=700 run.sh /app/run.sh
EXPOSE ${ASPNETCORE_HTTP_PORTS}
ENTRYPOINT ["/bin/ash","/app/run.sh"]
