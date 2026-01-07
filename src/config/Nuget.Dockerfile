# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:10.0.1-alpine3.23@sha256:2273b24ea865e253d5e3b66d0eae23ce53529946089819489410849ba62db12c AS runtimebase

LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

RUN apk --no-cache add postgresql16-client

FROM runtimebase AS setup

ARG VERSION=0.0.0
ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app

RUN echo "Tag Version:" $VERSION

RUN wget -O /app/EdFi.DmsConfigurationService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DmsConfigurationService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DmsConfigurationService.zip -d /app/ && \
    rm -f /app/EdFi.DmsConfigurationService.zip

COPY --chmod=700 run.sh /app/run.sh
EXPOSE ${ASPNETCORE_HTTP_PORTS}
ENTRYPOINT ["/bin/ash","/app/run.sh"]
