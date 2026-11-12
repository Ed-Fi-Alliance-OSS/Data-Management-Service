# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24@sha256:819bd3649d8bf53f35527c68d62e17b348ef6f24ae73073f497f00a878348062 AS runtimebase

LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

# ICU because Microsoft.Data.SqlClient (the MSSQL backend) does not support
# globalization-invariant mode, which the Alpine base image enables by default.
RUN apk --no-cache add postgresql18-client icu-libs=~78 icu-data-full=~78

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

FROM runtimebase AS setup

ARG VERSION=0.0.0
ARG ASSEMBLY_VERSION=0.0.0.0
ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app

RUN echo "Tag Version:" $VERSION
RUN echo "Assembly Version:" $ASSEMBLY_VERSION

RUN wget -O /app/EdFi.DmsConfigurationService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.Api.ConfigurationService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DmsConfigurationService.zip -d /app/ && \
    rm -f /app/EdFi.DmsConfigurationService.zip

COPY --chmod=700 run.sh /app/run.sh
EXPOSE ${ASPNETCORE_HTTP_PORTS}
ENTRYPOINT ["/bin/ash","/app/run.sh"]
