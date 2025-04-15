# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.12-alpine3.21@sha256:accc7352721d44ef6246e91704f4efb1954f69912af2d2bd54d117fa09922a53 AS runtimebase

LABEL maintainer="Ed-Fi Alliance, LLC and Contributors <techsupport@ed-fi.org>"

RUN apk --no-cache add gettext=~0 postgresql16-client=~16

FROM runtimebase AS setup

ARG VERSION=0.0.0
ENV ASPNETCORE_HTTP_PORTS=8080
ENV CORE_PACKAGE=EdFi.DataStandard52.ApiSchema
ENV CORE_PACKAGE_VERSION=1.0.171
ENV TPDM_PACKAGE=EdFi.TPDM.ApiSchema
ENV TPDM_PACKAGE_VERSION=1.0.171
ENV Sample_PACKAGE=EdFi.Sample.ApiSchema
ENV Sample_PACKAGE_VERSION=1.0.171
ENV Homograph_PACKAGE=EdFi.Homograph.ApiSchema
ENV Homograph_PACKAGE_VERSION=1.0.171

WORKDIR /app

RUN echo "Tag Version:" $VERSION

RUN wget -O /app/EdFi.DataManagementService.zip "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_apis/packaging/feeds/EdFi/nuget/packages/EdFi.DataManagementService/versions/${VERSION}/content" && \
    unzip /app/EdFi.DataManagementService.zip -d /app/ && \
    cp -r /app/DataManagementService/. /app/ && \
    cp -r /app/Installer/. /app/. && \
    cp -r /app/ApiSchemaDownloader/. /app/. && \
    rm -f /app/EdFi.DataManagementService.zip && \
    rm -r /app/DataManagementService

COPY --chmod=600 appsettings.template.json /app/appsettings.template.json

COPY --chmod=700 run.sh /app/run.sh
EXPOSE ${ASPNETCORE_HTTP_PORTS}
ENTRYPOINT ["/bin/ash","/app/run.sh"]
