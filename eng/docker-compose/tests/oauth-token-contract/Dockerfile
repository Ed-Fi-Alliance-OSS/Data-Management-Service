# SPDX-License-Identifier: Apache-2.0
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TokenContractProxy.csproj ./
COPY Program.cs ./
RUN dotnet publish TokenContractProxy.csproj -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "TokenContractProxy.dll"]
