// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.E2E;

public static class AppSettings
{
    public const string DefaultDataStoreDatabaseName = "edfi_datamanagementservice_e2e";
    public const string DefaultDatabaseEngine = "postgresql";
    public const int BytesPerMegabyte = 1024 * 1024;
    public const int DefaultMaxRequestBodySizeMegabytes = 10;

    private const string DefaultDmsPort = "8080";
    private const string DefaultConfigServicePort = "8081";
    private const string DefaultAuthenticationService =
        "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token";

    private static readonly AppSettingsValues _settings = LoadFromDefaultSources();

    public static string DmsPort => _settings.DmsPort;
    public static string ConfigServicePort => _settings.ConfigServicePort;
    public static string AuthenticationService => _settings.AuthenticationService;
    public static string DataStoreDatabaseName => _settings.DataStoreDatabaseName;

    // Database engine backing the E2E stack ("postgresql" default or "mssql"), and the two opaque
    // connection strings the standard E2E orchestration sets for the test process: the host-side
    // admin/reset connection string and the Docker-network Configuration Service registration string.
    // The two connection strings are empty unless set by build-dms.ps1's E2E process context.
    public static string DatabaseEngine => _settings.DatabaseEngine;
    public static string DataStoreAdminConnectionString => _settings.DataStoreAdminConnectionString;
    public static string DataStoreConnectionString => _settings.DataStoreConnectionString;

    public static int MaxRequestBodySizeMegabytes => _settings.MaxRequestBodySizeMegabytes;

    internal static AppSettingsValues Create(IConfiguration configuration)
    {
        return new AppSettingsValues(
            GetString(configuration, nameof(DmsPort), DefaultDmsPort),
            GetString(configuration, nameof(ConfigServicePort), DefaultConfigServicePort),
            GetString(configuration, nameof(AuthenticationService), DefaultAuthenticationService),
            GetString(configuration, nameof(DataStoreDatabaseName), DefaultDataStoreDatabaseName),
            GetString(configuration, nameof(DatabaseEngine), DefaultDatabaseEngine),
            GetString(configuration, nameof(DataStoreAdminConnectionString), string.Empty),
            GetString(configuration, nameof(DataStoreConnectionString), string.Empty),
            GetInt(configuration, nameof(MaxRequestBodySizeMegabytes), DefaultMaxRequestBodySizeMegabytes)
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

    private static int GetInt(IConfiguration configuration, string key, int defaultValue)
    {
        string? value = configuration[$"AppSettings:{key}"] ?? configuration[key];
        return int.TryParse(value, out int parsedValue) ? parsedValue : defaultValue;
    }
}

internal sealed record AppSettingsValues(
    string DmsPort,
    string ConfigServicePort,
    string AuthenticationService,
    string DataStoreDatabaseName,
    string DatabaseEngine,
    string DataStoreAdminConnectionString,
    string DataStoreConnectionString,
    int MaxRequestBodySizeMegabytes
);
