// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

/// <summary>
/// The data store derivative arrangement a scenario asks for through its tags. Attachment is opt-in,
/// so an untagged scenario's data store stays derivative-free and its reads keep exercising the
/// primary path.
/// </summary>
public enum DataStoreDerivativeArrangement
{
    /// <summary>
    /// No derivatives. A snapshot-requesting read has no snapshot to select, which is the
    /// arrangement the "no snapshot is configured" failure response is asserted against.
    /// </summary>
    None,

    /// <summary>
    /// Both derivatives, arranged so routing is observable; see <see
    /// cref="AuthorizationDataProvider.AttachDataStoreDerivatives" />.
    /// </summary>
    Routing,

    /// <summary>
    /// A Snapshot derivative alone, naming a database that does not exist. A snapshot-requesting
    /// read then fails inside connection acquisition against a snapshot that is genuinely
    /// configured, which is the arrangement the unreachable-snapshot failure response is asserted
    /// against. No read replica is attached, so the same read without the header is served by the
    /// primary.
    /// </summary>
    UnreachableSnapshot,
}

/// <summary>
/// Provides authorization functionality for E2E tests by managing vendor registration,
/// application creation, and OAuth token generation for the Ed-Fi Data Management Service.
/// This class handles the complete authorization workflow needed for test scenarios.
/// </summary>
public static class AuthorizationDataProvider
{
    /// <summary>
    /// The client credentials obtained from the most recent call to CreateClientCredentials().
    /// These credentials are used for OAuth token generation.
    /// </summary>
    private static ClientCredentials _clientCredentials = null!;

    /// <summary>
    /// HTTP client configured to communicate with the Configuration Management Service (CMS).
    /// Used for vendor and application registration operations.
    /// </summary>
    private static readonly HttpClient _configurationServiceClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    /// <summary>
    /// HTTP client configured to communicate with the Data Management Service (DMS).
    /// Used for OAuth token generation and authentication operations.
    /// </summary>
    private static readonly HttpClient _dmsClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.DmsPort}/"),
    };

    /// <summary>
    /// The scenario tag that selects <see cref="DataStoreDerivativeArrangement.Routing" />.
    /// </summary>
    internal const string RoutingTag = "derivative-routing";

    /// <summary>
    /// The scenario tag that selects <see cref="DataStoreDerivativeArrangement.UnreachableSnapshot" />.
    /// </summary>
    internal const string UnreachableSnapshotTag = "derivative-routing-unreachable-snapshot";

    /// <summary>
    /// The suffix appended to the snapshot database name to name one that does not exist.
    /// </summary>
    private const string AbsentDatabaseSuffix = "_absent";

    /// <summary>
    /// The connection string keyword both engines' E2E registration strings carry the catalog under.
    /// Lookups through <see cref="DbConnectionStringBuilder" /> are case insensitive, so the one
    /// keyword covers the PostgreSQL <c>database=</c> and the SQL Server <c>Database=</c> forms.
    /// </summary>
    private const string DatabaseKeyword = "database";

    /// <summary>
    /// Selects the derivative arrangement a scenario's combined tags ask for. The
    /// unreachable-snapshot tag is checked first, so a scenario carrying both tags gets the
    /// unreachable snapshot rather than the routing arrangement.
    /// </summary>
    internal static DataStoreDerivativeArrangement ArrangementFromTags(IEnumerable<string> combinedTags)
    {
        string[] tags = combinedTags.ToArray();

        if (tags.Contains(UnreachableSnapshotTag))
        {
            return DataStoreDerivativeArrangement.UnreachableSnapshot;
        }

        return tags.Contains(RoutingTag)
            ? DataStoreDerivativeArrangement.Routing
            : DataStoreDerivativeArrangement.None;
    }

    /// <summary>
    /// Derives a well-formed connection string that names a database which does not exist, from the
    /// snapshot connection string the build orchestration resolved for the running engine. Rewriting
    /// only the catalog of a real connection string keeps every other setting - host, port,
    /// credentials, TLS - exactly as the engine needs it, so one derivation serves both the
    /// PostgreSQL (<c>host=...;database=...</c>) and the SQL Server
    /// (<c>Server=...;Database=...</c>) E2E runs without either form being written out here.
    /// </summary>
    internal static string AbsentDatabaseConnectionString(string snapshotConnectionString)
    {
        // Both failure messages name the missing setting only. The connection string carries
        // credentials, so it is never repeated back.
        if (string.IsNullOrWhiteSpace(snapshotConnectionString))
        {
            throw new InvalidOperationException(
                "The unreachable-snapshot arrangement needs a configured "
                    + $"{nameof(AppSettings.DataStoreSnapshotConnectionString)}, and it is blank."
            );
        }

        DbConnectionStringBuilder builder = new() { ConnectionString = snapshotConnectionString };

        if (
            !builder.TryGetValue(DatabaseKeyword, out object? database)
            || database is not string databaseName
            || string.IsNullOrWhiteSpace(databaseName)
        )
        {
            throw new InvalidOperationException(
                $"The snapshot connection string carries no '{DatabaseKeyword}' setting, so an absent "
                    + "database cannot be named from it."
            );
        }

        builder[DatabaseKeyword] = databaseName + AbsentDatabaseSuffix;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Attaches the requested derivative arrangement to the data store this setup just created.
    /// Requested only by scenarios carrying <see cref="RoutingTag" /> or <see
    /// cref="UnreachableSnapshotTag" />; every other setup's data store stays derivative-free, so its
    /// plain reads keep exercising the primary path.
    /// </summary>
    /// <remarks>
    /// Under <see cref="DataStoreDerivativeArrangement.Routing" />, the read replica deliberately
    /// points at the data store's own database. Once a replica is configured, a routing scenario's
    /// plain reads are served by it, and a replica pointing anywhere else would serve them from a
    /// database the scenario never wrote to. Pointing it at the primary keeps those reads seeing the
    /// scenario's own writes while a read replica is genuinely configured, which is what snapshot
    /// precedence is asserted against.
    ///
    /// The snapshot points at a separately provisioned database that is left empty. DMS never writes
    /// to a derivative, so "empty" is what makes a snapshot-routed read distinguishable from a plain
    /// one without seeding a database the API cannot reach.
    ///
    /// Under <see cref="DataStoreDerivativeArrangement.UnreachableSnapshot" />, the snapshot names a
    /// database that does not exist and no read replica is attached: a snapshot-requesting read then
    /// fails inside connection acquisition while the same read without the header is served by the
    /// primary.
    /// </remarks>
    private static async Task AttachDataStoreDerivatives(
        int dataStoreId,
        DataStoreDerivativeArrangement arrangement
    )
    {
        (string DerivativeType, string ConnectionString)[] derivatives = arrangement switch
        {
            DataStoreDerivativeArrangement.Routing =>
            [
                ("ReadReplica", DataStoreConnectionStringProvider.Create()),
                ("Snapshot", AppSettings.DataStoreSnapshotConnectionString),
            ],
            DataStoreDerivativeArrangement.UnreachableSnapshot =>
            [
                ("Snapshot", AbsentDatabaseConnectionString(AppSettings.DataStoreSnapshotConnectionString)),
            ],
            _ => [],
        };

        foreach ((string derivativeType, string connectionString) in derivatives)
        {
            using StringContent content = new(
                JsonSerializer.Serialize(
                    new
                    {
                        dataStoreId,
                        derivativeType,
                        connectionString,
                    }
                ),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response = await _configurationServiceClient.PostAsync(
                "v3/dataStoreDerivatives",
                content
            );

            if (!response.IsSuccessStatusCode)
            {
                // Fail loudly rather than leaving the routing scenarios to report a confusing
                // "Snapshot not found". The body can carry the connection string, so it is not logged.
                throw new InvalidOperationException(
                    $"Failed to register the {derivativeType} derivative for data store {dataStoreId}: "
                        + $"{(int)response.StatusCode} {response.StatusCode}."
                );
            }
        }
    }

    /// <summary>
    /// Creates a new vendor and application in the Configuration Management Service with specified
    /// authorization parameters. This establishes the foundation for OAuth authentication in tests.
    /// </summary>
    /// <param name="company">The name of the vendor company to register</param>
    /// <param name="contactName">The name of the primary contact for the vendor</param>
    /// <param name="contactEmailAddress">The email address of the primary contact</param>
    /// <param name="namespacePrefixes">Comma-separated list of namespace prefixes the vendor is authorized to use</param>
    /// <param name="edOrgIds">Comma-separated list of education organization IDs the vendor has access to</param>
    /// <param name="systemAdministratorToken">Bearer token with system administrator privileges for CMS API access</param>
    /// <param name="claimSetName">The name of the claim set to assign to the application (default: "SISVendor")</param>
    /// <param name="derivativeArrangement">Which derivative arrangement to attach to the created data store; see <see cref="AttachDataStoreDerivatives" /></param>
    /// <returns>A task representing the asynchronous operation</returns>
    public static async Task CreateClientCredentials(
        string company,
        string contactName,
        string contactEmailAddress,
        string namespacePrefixes,
        string edOrgIds,
        string systemAdministratorToken,
        string claimSetName = AuthorizationClaimSetNames.SisVendor,
        DataStoreDerivativeArrangement derivativeArrangement = DataStoreDerivativeArrangement.None
    )
    {
        _configurationServiceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            systemAdministratorToken
        );

        // Create DataStore first
        using StringContent dataStoreContent = new(
            JsonSerializer.Serialize(
                new
                {
                    dataStoreType = "Test",
                    name = "E2E Test DMS Instance",
                    connectionString = DataStoreConnectionStringProvider.Create(),
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage dataStorePostResponse = await _configurationServiceClient.PostAsync(
            "v3/dataStores",
            dataStoreContent
        );

        var dataStoreLocation = dataStorePostResponse.Headers.Location?.AbsoluteUri ?? "";

        using HttpResponseMessage dataStoreGetResponse = await _configurationServiceClient.GetAsync(
            dataStoreLocation
        );
        string dataStoreBody = await dataStoreGetResponse.Content.ReadAsStringAsync();

        int dataStoreId = JsonDocument.Parse(dataStoreBody).RootElement.GetProperty("id").GetInt32();

        await AttachDataStoreDerivatives(dataStoreId, derivativeArrangement);

        // Create vendor
        using StringContent vendorContent = new(
            JsonSerializer.Serialize(
                new
                {
                    company,
                    contactName,
                    contactEmailAddress,
                    namespacePrefixes,
                }
            ),
            Encoding.UTF8,
            "application/json"
        );

        using HttpResponseMessage vendorPostResponse = await _configurationServiceClient.PostAsync(
            "v3/vendors",
            vendorContent
        );

        var vendorLocation = vendorPostResponse.Headers.Location?.AbsoluteUri ?? "";

        using HttpResponseMessage vendorGetResponse = await _configurationServiceClient.GetAsync(
            vendorLocation
        );
        string vendorBody = await vendorGetResponse.Content.ReadAsStringAsync();

        int vendorId = JsonDocument.Parse(vendorBody).RootElement.GetProperty("id").GetInt32();

        long[] educationOrganizationIds = [];
        if (!string.IsNullOrEmpty(edOrgIds))
        {
            educationOrganizationIds = edOrgIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s.Trim()))
                .ToArray();
        }

        // Create application with DataStore
        string requestJson = CreateApplicationRequestJson(
            vendorId,
            claimSetName,
            educationOrganizationIds,
            dataStoreId
        );

        using StringContent applicationContent = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage applicationPostResponse = await _configurationServiceClient.PostAsync(
            "v3/applications",
            applicationContent
        );

        string applicationBody = await applicationPostResponse.Content.ReadAsStringAsync();

        if (!applicationPostResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to create application. Status: {applicationPostResponse.StatusCode}, "
                    + $"Response: {applicationBody}"
            );
        }

        var applicationJson = JsonDocument.Parse(applicationBody);

        if (
            !applicationJson.RootElement.TryGetProperty("key", out var keyProperty)
            || !applicationJson.RootElement.TryGetProperty("secret", out var secretProperty)
        )
        {
            throw new InvalidOperationException(
                $"Application response missing key or secret. Response: {applicationBody}"
            );
        }

        _clientCredentials = new ClientCredentials(
            keyProperty.GetString() ?? "",
            secretProperty.GetString() ?? ""
        );
    }

    internal static string CreateApplicationRequestJson(
        int vendorId,
        string claimSetName,
        long[] educationOrganizationIds,
        int dataStoreId
    )
    {
        return JsonSerializer.Serialize(
            new
            {
                vendorId,
                applicationName = "E2E",
                claimSetName,
                educationOrganizationIds,
                dataStoreIds = new[] { dataStoreId },
            }
        );
    }

    /// <summary>
    /// Generates an OAuth access token using the stored client credentials.
    /// This token is required for authenticating API requests to the Data Management Service.
    /// </summary>
    /// <returns>A valid OAuth access token as a string</returns>
    public static Task<string> GetToken() => GetToken("oauth/token");

    public static async Task<string> GetToken(string tokenUrl)
    {
        var formData = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }
        );

        string basicB64 = OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(
            _clientCredentials.key,
            _clientCredentials.secret
        );

        _dmsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue($"Basic", basicB64);
        var tokenResponse = await _dmsClient.PostAsync(tokenUrl, formData);
        var jsonString = await tokenResponse.Content.ReadAsStringAsync();

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to get token. Status: {tokenResponse.StatusCode}, " + $"Response: {jsonString}"
            );
        }

        var tokenJson = JsonDocument.Parse(jsonString);

        if (!tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenProperty))
        {
            throw new InvalidOperationException(
                $"Token response missing access_token. Response: {jsonString}"
            );
        }

        return accessTokenProperty.GetString() ?? "";
    }
}
