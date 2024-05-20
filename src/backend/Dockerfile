# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.203-alpine3.18@sha256:2a8dca3af111071172b1629c12eefaeca0d6c2954887c4489195771c9e90833c AS build
WORKDIR /source

COPY EdFi.DataManagementService.Backend.Installer/*.csproj EdFi.DataManagementService.Backend.Installer/
COPY EdFi.DataManagementService.Backend/*.csproj EdFi.DataManagementService.Backend/
COPY EdFi.DataManagementService.Backend.Mssql/*.csproj EdFi.DataManagementService.Backend.Mssql/
COPY EdFi.DataManagementService.Backend.Postgresql/*.csproj EdFi.DataManagementService.Backend.Postgresql/

# Fail fast by restoring before taking any other action
RUN dotnet restore EdFi.DataManagementService.Backend.Installer

COPY EdFi.DataManagementService.Backend.Installer/ EdFi.DataManagementService.Backend.Installer/
COPY EdFi.DataManagementService.Backend/ EdFi.DataManagementService.Backend/
COPY EdFi.DataManagementService.Backend.Mssql/ EdFi.DataManagementService.Backend.Mssql/
COPY EdFi.DataManagementService.Backend.Postgresql/ EdFi.DataManagementService.Backend.Postgresql/
COPY .editorconfig .

RUN dotnet publish -c Release -o /app -r linux-x64 -p:PublishSingleFile=true \
    --no-restore EdFi.DataManagementService.Backend.Installer

FROM postgres:16.3-bookworm@sha256:1bf73ccae25238fa555100080042f0b2f9be08eb757e200fe6afc1fc413a1b3c

WORKDIR /app

ENV POSTGRES_USER=postgres
ENV POSTGRES_PORT=5432
ENV POSTGRES_HOST=localhost

COPY --from=build /app .
# chmod=700 is for owner to have full permissions
COPY --chown=postgres --chmod=700 run.sh .

USER postgres
RUN ./run.sh

USER root
# Delete the .net application, do not need to carry it into the final image
RUN rm -rf /app
USER postgres

EXPOSE 5432

# For local debugging, comment out CMD and uncomment ENTRYPOINT
#ENTRYPOINT ["tail", "-f", "/dev/null"]
CMD ["postgres"]
