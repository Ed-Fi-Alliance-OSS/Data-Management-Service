// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal static class DocumentCacheAdminConfiguration
{
    public static IConfigurationRoot Build(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        string? environmentName = GetEnvironmentName(parseResult);
        string? settingsPath = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.SettingsOptionName
        );

        var builder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory());

        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
        }

        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            builder.AddJsonFile(Path.GetFullPath(settingsPath), optional: false, reloadOnChange: false);
        }

        builder.AddEnvironmentVariables();
        builder.AddInMemoryCollection(
            DocumentCacheAdminCommandSurface.CreateConfigurationOverrides(parseResult)
        );

        return builder.Build();
    }

    private static string? GetEnvironmentName(ParseResult parseResult)
    {
        string? explicitEnvironment = parseResult.GetValue<string?>(
            DocumentCacheAdminCommandSurface.EnvironmentOptionName
        );
        if (!string.IsNullOrWhiteSpace(explicitEnvironment))
        {
            return explicitEnvironment;
        }

        string? dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(dotnetEnvironment))
        {
            return dotnetEnvironment;
        }

        string? aspnetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.IsNullOrWhiteSpace(aspnetCoreEnvironment) ? null : aspnetCoreEnvironment;
    }
}
