# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM mcr.microsoft.com/dotnet/sdk:8.0.201-jammy@sha256:9aacef90e2773ff7d13a2f2687739778ed39312678b3669de06ec5fbeb0e86af AS build

ENV ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app
COPY services/EdFi.DataManagementService.Api/*.csproj ./services/EdFi.DataManagementService.Api/
COPY EdFi.DataManagementService-Docker.sln ./
COPY nuget.config .
RUN dotnet restore

COPY services/EdFi.DataManagementService.Api ./services/EdFi.DataManagementService.Api
COPY .editorconfig .

RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0.2-jammy-chiseled@sha256:cb35170c5c4687e42749a4f3538e6667cd682493466cc1535d8579befc23e077 AS runtime
WORKDIR /app
COPY --from=build /app/out/*.dll ./
COPY --from=build /app/out/*.pdb ./
COPY --from=build /app/out/appsettings.json ./
COPY --from=build /app/out/*runtimeconfig.json ./
COPY --from=build /app/out/*.deps.json ./

EXPOSE $ASPNETCORE_HTTP_PORTS
USER app
ENTRYPOINT ["dotnet", "EdFi.DataManagementService.Api.dll"]
