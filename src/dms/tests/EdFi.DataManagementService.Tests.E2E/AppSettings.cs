// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.E2E;

public static class AppSettings
{
    public const string LegacyDmsInstanceDatabaseName = "edfi_datamanagementservice";

    private const string DefaultDmsPort = "8080";
    private const string DefaultConfigServicePort = "8081";
    private const string DefaultAuthenticationService =
        "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token";

    private static readonly AppSettingsValues _settings = LoadFromDefaultSources();

    public static string DmsPort => _settings.DmsPort;
    public static string ConfigServicePort => _settings.ConfigServicePort;
    public static string AuthenticationService => _settings.AuthenticationService;
    public static bool UseRelationalBackend => _settings.UseRelationalBackend;
    public static string DmsInstanceDatabaseName => _settings.DmsInstanceDatabaseName;

    internal static AppSettingsValues Create(IConfiguration configuration)
    {
        return new AppSettingsValues(
            GetString(configuration, nameof(DmsPort), DefaultDmsPort),
            GetString(configuration, nameof(ConfigServicePort), DefaultConfigServicePort),
            GetString(configuration, nameof(AuthenticationService), DefaultAuthenticationService),
            GetBoolean(configuration, nameof(UseRelationalBackend), defaultValue: false),
            GetString(configuration, nameof(DmsInstanceDatabaseName), LegacyDmsInstanceDatabaseName)
        );
    }

    private static AppSettingsValues LoadFromDefaultSources()
    {
        return Create(
            new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build()
        );
    }

    private static string GetString(IConfiguration configuration, string key, string defaultValue)
    {
        string? value = configuration[$"AppSettings:{key}"] ?? configuration[key];
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static bool GetBoolean(IConfiguration configuration, string key, bool defaultValue)
    {
        string? value = configuration[$"AppSettings:{key}"] ?? configuration[key];
        return bool.TryParse(value, out bool parsedValue) ? parsedValue : defaultValue;
    }
}

internal sealed record AppSettingsValues(
    string DmsPort,
    string ConfigServicePort,
    string AuthenticationService,
    bool UseRelationalBackend,
    string DmsInstanceDatabaseName
);
