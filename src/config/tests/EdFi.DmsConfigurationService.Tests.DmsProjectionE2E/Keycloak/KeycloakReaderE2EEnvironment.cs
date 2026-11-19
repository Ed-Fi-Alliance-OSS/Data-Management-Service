// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Keycloak;

/// <summary>
/// The single-tenant DMS E2E stack in Keycloak mode this namespace's fixture runs against, read from the
/// <c>KEYCLOAK_READER_E2E_*</c> variables. A missing or malformed value fails the fixture with a setup message: it never
/// skips.
/// </summary>
public sealed record KeycloakReaderE2EEnvironment(
    Uri DmsBaseUrl,
    Uri ConfigServiceUrl,
    string AdminClientId,
    string AdminClientSecret,
    string ExpectedIssuer,
    string DatabaseEngine,
    string DataStoreConnectionString
)
{
    private const string SetupFailure = "DMS projection reader Keycloak E2E setup failed";

    public static KeycloakReaderE2EEnvironment Read()
    {
        string databaseEngine = Required("KEYCLOAK_READER_E2E_DATABASE_ENGINE");
        if (databaseEngine is not ("postgresql" or "mssql"))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: KEYCLOAK_READER_E2E_DATABASE_ENGINE must be 'postgresql' or 'mssql'."
            );
        }

        return new(
            RequiredUrl("KEYCLOAK_READER_E2E_DMS_BASE_URL"),
            RequiredUrl("KEYCLOAK_READER_E2E_CONFIG_SERVICE_URL"),
            Required("KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_ID"),
            Required("KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_SECRET"),
            Required("KEYCLOAK_READER_E2E_EXPECTED_ISSUER"),
            databaseEngine,
            Required("KEYCLOAK_READER_E2E_DATA_STORE_CONNECTION_STRING")
        );
    }

    /// <summary>
    /// The addresses, admin credential and engine as <see cref="LiveServices"/> takes them. The Instance-only members
    /// are empty, so the calls that need them (route databases, tenant vendors) are not used on this stack.
    /// </summary>
    public ProjectionE2EEnvironment ForLiveServices() =>
        new(
            DmsBaseUrl,
            ConfigServiceUrl,
            AdminClientId,
            AdminClientSecret,
            DatabaseEngine,
            routeTwoConnectionString: "",
            routeThreeConnectionString: "",
            routes: [],
            vendorIdsByTenant: new Dictionary<string, int>()
        );

    /// <summary>Never includes a value: a secret and a connection string are among them.</summary>
    public override string ToString() => nameof(KeycloakReaderE2EEnvironment);

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: required environment variable '{name}' is not set. Run this fixture through "
                    + "'./build-dms.ps1 E2ETest -IdentityProvider keycloak -RunDmsProjectionReaderKeycloakE2E', which "
                    + "starts the single-tenant DMS E2E stack in Keycloak mode and exports it to the test process."
            );
        }

        return value;
    }

    private static Uri RequiredUrl(string name) =>
        Uri.TryCreate(Required(name).TrimEnd('/') + "/", UriKind.Absolute, out Uri? url)
            ? url
            : throw new InvalidOperationException($"{SetupFailure}: '{name}' is not an absolute URL.");
}
