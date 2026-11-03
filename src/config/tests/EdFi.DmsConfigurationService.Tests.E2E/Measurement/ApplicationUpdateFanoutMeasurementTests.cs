// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.Tests.E2E.Hooks;
using EdFi.DmsConfigurationService.Tests.E2E.Keycloak;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MssqlOpenIddict = EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories;
using PostgresqlOpenIddict = EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;

namespace EdFi.DmsConfigurationService.Tests.E2E.Measurement;

/// <summary>
/// Opt-in wiring for the DMS-1642 measurement of claim set changes applied to every API client of
/// one Application. It runs only when CONFIGURATION_SERVICE_FANOUT_MEASUREMENT is true, and only
/// against an isolated deployment (Invoke-IsolatedConfigE2E.ps1), since it creates hundreds of
/// clients and changes their identity provider state directly.
/// </summary>
public static class FanoutMeasurementEnvironment
{
    public const string Category = "ApplicationUpdateFanoutMeasurement";

    private const string EnabledVariable = "CONFIGURATION_SERVICE_FANOUT_MEASUREMENT";

    public static int ClientCount =>
        int.Parse(EnvOrDefault("CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_CLIENTS", "200"));

    public static string OutputDirectory =>
        EnvOrDefault(
            "CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_OUTPUT",
            TestContext.CurrentContext.WorkDirectory
        );

    /// <summary>
    /// The provider the running Configuration Service container was given, as build-config.ps1
    /// reads it back from the container, rather than what an environment file intended.
    /// </summary>
    public static string IdentityProvider =>
        Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_E2E_EXPECTED_IDENTITY_PROVIDER") ?? "";

    public static string ApiUrl => new PlaywrightContext().ApiUrl;

    public static string KeycloakTokenEndpoint =>
        EnvOrDefault(
            "KEYCLOAK_OAUTH_TOKEN_ENDPOINT",
            $"{KeycloakCharacterizationEnvironment.KeycloakUrl}/realms/{KeycloakCharacterizationEnvironment.Realm}/protocol/openid-connect/token"
        );

    public static string RoleClaimType =>
        EnvOrDefault(
            "CONFIGURATION_SERVICE_E2E_EXPECTED_ROLE_CLAIM_TYPE",
            "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
        );

    public static void RequireIsolatedOptIn()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable(EnabledVariable),
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            Assert.Ignore($"The fan-out measurement runs only when {EnabledVariable}=true.");
        }

        // The stock stack and other workspaces' stacks may be running; this run must never touch them.
        if (
            Environment.GetEnvironmentVariable("CONFIGURATION_SERVICE_E2E_API_URL")
                is not { Length: > 0 } apiUrl
            || apiUrl.Contains(":8081", StringComparison.Ordinal)
        )
        {
            Assert.Fail(
                "The fan-out measurement requires an isolated deployment (CONFIGURATION_SERVICE_E2E_API_URL set "
                    + "to a port other than the stock stack's 8081); refusing to run."
            );
        }

        if (IdentityProvider is not ("keycloak" or "self-contained"))
        {
            Assert.Fail(
                "CONFIGURATION_SERVICE_E2E_EXPECTED_IDENTITY_PROVIDER does not name keycloak or self-contained; refusing to run."
            );
        }
    }

    private static string EnvOrDefault(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>One API client the measurement created, with what its provider state must keep.</summary>
public sealed record FanoutClient(
    int Ordinal,
    int ApiClientId,
    Guid ClientUuid,
    string Key,
    string Secret,
    string Name,
    bool IsApproved,
    long[] EducationOrganizationIds,
    int[] DataStoreIds,
    bool Inherits
)
{
    public string EducationOrganizationClaim => string.Join(",", EducationOrganizationIds.Order());

    public string DataStoreClaim => string.Join(",", DataStoreIds.Order());
}

/// <summary>A client's identity provider state, reduced to what the verification compares.</summary>
public sealed record ProviderClientState(
    bool Exists,
    bool? Enabled,
    string[] Scopes,
    string? EducationOrganizationClaim,
    string? DataStoreClaim,
    string? CredentialFingerprint
);

/// <summary>The real repository for the provider under test and how to read its stored client state.</summary>
public abstract class FanoutProvider
{
    public abstract string Description { get; }

    /// <summary>A repository instance per measured run, as each Application PUT request resolves its own.</summary>
    public abstract IIdentityProviderRepository CreateRepository();

    public abstract string TokenEndpoint { get; }

    /// <summary>
    /// The status and OAuth error code with which the token endpoint refuses an unapproved client
    /// presenting its valid secret. Any other refusal, such as a server error, is not taken as one.
    /// </summary>
    public abstract string UnapprovedClientRefusal { get; }

    public abstract Task<ProviderClientState> ReadStateAsync(Guid clientUuid);

    public static FanoutProvider Create() =>
        FanoutMeasurementEnvironment.IdentityProvider == "keycloak"
            ? new KeycloakFanoutProvider()
            : new OpenIddictFanoutProvider();
}

public sealed class KeycloakFanoutProvider : FanoutProvider
{
    private readonly KeycloakContext _context = new(
        KeycloakCharacterizationEnvironment.KeycloakUrl,
        KeycloakCharacterizationEnvironment.Realm,
        StepDefinitions.StepDefinitions.SystemClientId,
        StepDefinitions.StepDefinitions.SystemClientSecret,
        FanoutMeasurementEnvironment.RoleClaimType
    );

    public override string Description => $"keycloak at {KeycloakCharacterizationEnvironment.KeycloakUrl}";

    public override string TokenEndpoint => FanoutMeasurementEnvironment.KeycloakTokenEndpoint;

    // An unapproved client is disabled in Keycloak, which refuses a disabled client's grant this way.
    public override string UnapprovedClientRefusal => "401 invalid_client";

    public override IIdentityProviderRepository CreateRepository() =>
        new KeycloakClientRepository(
            _context,
            new KeycloakClientFacade(_context),
            NullLogger<KeycloakClientRepository>.Instance,
            Options.Create(new ClientSecretValidationOptions())
        );

    public override async Task<ProviderClientState> ReadStateAsync(Guid clientUuid)
    {
        KeycloakClientFacade facade = new(_context);
        var client = await facade.GetClientAsync(_context.Realm, clientUuid.ToString());
        if (client is null)
        {
            return new(false, null, [], null, null, null);
        }

        var scopes = await facade.GetDefaultClientScopesAsync(_context.Realm, clientUuid.ToString());
        return new(
            true,
            client.Enabled,
            [.. scopes.Select(scope => scope.Name)],
            MapperValue("educationOrganizationIds"),
            MapperValue("dataStoreIds"),
            // Keycloak does not expose a credential digest; approved clients prove their credential
            // is unchanged by obtaining a token with it.
            null
        );

        string? MapperValue(string claimName) =>
            client
                .ProtocolMappers?.FirstOrDefault(mapper =>
                    mapper.Config is not null
                    && mapper.Config.Contains(new KeyValuePair<string, string>("claim.name", claimName))
                )
                ?.Config["claim.value"];
    }
}

public sealed class OpenIddictFanoutProvider : FanoutProvider
{
    private readonly IOpenIddictDataRepository _dataRepository = CreateDataRepository();

    public override string Description =>
        $"self-contained (OpenIddict) on {(SetupHooks.UseMssql ? "mssql" : "postgresql")}";

    public override string TokenEndpoint => $"{FanoutMeasurementEnvironment.ApiUrl}/connect/token";

    // OpenIddictTokenManager answers a valid secret of an unapproved client with invalid_client, and a
    // wrong secret with unauthorized_client, so this refusal is the approval gate's own.
    public override string UnapprovedClientRefusal => "401 invalid_client";

    private static IOpenIddictDataRepository CreateDataRepository()
    {
        var options = Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = SetupHooks.DatabaseConnectionString,
                EncryptionKey = "",
            }
        );
        return SetupHooks.UseMssql
            ? new MssqlOpenIddict.OpenIddictDataRepository(
                options,
                NullLogger<MssqlOpenIddict.OpenIddictDataRepository>.Instance
            )
            : new PostgresqlOpenIddict.OpenIddictDataRepository(
                options,
                NullLogger<PostgresqlOpenIddict.OpenIddictDataRepository>.Instance
            );
    }

    public override IIdentityProviderRepository CreateRepository() =>
        // The hasher is used only when a client is created or its secret is reset, which this
        // measurement never does, so a throwing stand-in proves the update path never reaches it.
        new OpenIddictClientRepository(
            NullLogger<OpenIddictClientRepository>.Instance,
            new UnusedClientSecretHasher(),
            _dataRepository,
            Options.Create(new ClientSecretValidationOptions())
        );

    public override async Task<ProviderClientState> ReadStateAsync(Guid clientUuid)
    {
        using var connection = await _dataRepository.CreateConnectionAsync();
        var application = await _dataRepository.GetApplicationByIdAsync(clientUuid, connection);
        if (application is null)
        {
            return new(false, null, [], null, null, null);
        }

        var mappers =
            JsonSerializer.Deserialize<List<Dictionary<string, string>>>(application.ProtocolMappers) ?? [];
        return new(
            true,
            // Approval is held by the Configuration Service's ApiClient row, not the provider record.
            null,
            application.Permissions,
            MapperValue("educationOrganizationIds"),
            MapperValue("dataStoreIds"),
            application.ClientSecret
        );

        string? MapperValue(string claimName) =>
            mappers
                .Find(mapper => mapper.Contains(new KeyValuePair<string, string>("claim.name", claimName)))
                ?.GetValueOrDefault("claim.value");
    }

    private sealed class UnusedClientSecretHasher : Secrets.IClientSecretHasher
    {
        public Task<string> HashSecretAsync(string plainTextSecret) =>
            throw new InvalidOperationException("A claim set update must not hash a client secret.");

        public Task<bool> VerifySecretAsync(string plainTextSecret, string hashedSecret) =>
            throw new InvalidOperationException("A claim set update must not verify a client secret.");

        public bool IsSecretHashed(string secret) =>
            throw new InvalidOperationException("A claim set update must not inspect a client secret.");
    }
}

/// <summary>
/// Timings and outcomes of one change applied to every client. A provider-mode run times each
/// client's update; an endpoint-mode run has only the request's total, so its per-client list is empty.
/// </summary>
public sealed record MeasuredRun(
    string Label,
    string FromClaimSet,
    string ToClaimSet,
    double TotalMilliseconds,
    double[] PerClientMilliseconds,
    int Attempted,
    int Succeeded,
    string[] Failures
)
{
    public List<string> VerificationMismatches { get; } = [];

    public double Percentile(double percentile)
    {
        double[] sorted = [.. PerClientMilliseconds.Order()];
        int index = (int)Math.Ceiling(percentile / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

/// <summary>
/// DMS-1642 measurement: how long a claim set change takes when it is applied to every API client of
/// one Application, and whether every client's newly issued token carries it. Each fixture times the
/// change its own way (<see cref="MeasureAsync"/>). Setup, token requests and verification are outside
/// the timed interval, and every run is verified for every client with newly issued tokens and the
/// provider's stored state, so an early return or a skipped client cannot pass as a fast run. No
/// elapsed-time assertion is made; the timings are evidence, written to the measurement report.
/// </summary>
public abstract class ApplicationUpdateFanoutMeasurement
{
    private const string NamespacePrefixes = "uri://ed-fi-fanout.org";
    private const int VerificationConcurrency = 8;
    private const long FirstEducationOrganizationId = 255901;

    private readonly SensitiveValueRegistry _sensitive = new();
    protected readonly HttpClient _http = new();
    protected readonly List<FanoutClient> _clients = [];
    private readonly List<MeasuredRun> _runs = [];
    private readonly List<string> _baselineMismatches = [];
    private readonly Dictionary<Guid, string?> _credentialFingerprints = [];
    private readonly Dictionary<Guid, string[]> _otherTokenScopes = [];
    protected FanoutProvider _provider = null!;
    private string _runTag = "";

    protected int ApplicationId { get; private set; }
    protected int VendorId { get; private set; }
    protected long[] ApplicationEducationOrganizationIds { get; private set; } = [];
    protected string ApplicationName => $"Fanout Application {_runTag}";

    /// <summary>The mode named in the report and its file name.</summary>
    protected abstract string Mode { get; }
    private TimeSpan _setupDuration;

    [OneTimeSetUp]
    public async Task Setup()
    {
        FanoutMeasurementEnvironment.RequireIsolatedOptIn();
        _provider = FanoutProvider.Create();
        _runTag = Guid.NewGuid().ToString("N")[..8];

        string claimSetA = $"FanoutA{_runTag}";
        string claimSetB = $"FanoutB{_runTag}";
        string claimSetC = $"FanoutC{_runTag}";

        Stopwatch setup = Stopwatch.StartNew();
        await CreateClientsAsync(claimSetA);
        _setupDuration = setup.Elapsed;

        _baselineMismatches.AddRange(
            await VerifyAsync(claimSetA, previousClaimSet: null, captureBaseline: true)
        );

        // Cold: the first change, to a claim set no provider scope exists for yet. The repeats change
        // to another new claim set and then back to the original, whose scope already exists.
        (string Label, string From, string To)[] changes =
        [
            ("cold, new claim set scope", claimSetA, claimSetB),
            ("repeat, new claim set scope", claimSetB, claimSetC),
            ("repeat, existing claim set scope", claimSetC, claimSetA),
        ];
        foreach (var (label, from, to) in changes)
        {
            MeasuredRun run = await MeasureAsync(label, from, to);
            run.VerificationMismatches.AddRange(await VerifyAsync(to, from, captureBaseline: false));
            _runs.Add(run);
        }

        await WriteReportAsync();
    }

    [OneTimeTearDown]
    public void TearDown() => _http.Dispose();

    [Test]
    public void It_verifies_every_client_before_any_change() => _baselineMismatches.Should().BeEmpty();

    [Test]
    public void It_measures_every_change() => _runs.Should().HaveCount(3);

    [Test]
    public void It_updates_every_client_in_every_run()
    {
        foreach (MeasuredRun run in _runs)
        {
            run.Failures.Should().BeEmpty(run.Label);
            run.Attempted.Should().Be(_clients.Count, run.Label);
            run.Succeeded.Should().Be(_clients.Count, run.Label);
        }
    }

    [Test]
    public void It_verifies_every_client_after_every_run()
    {
        foreach (MeasuredRun run in _runs)
        {
            run.VerificationMismatches.Should().BeEmpty(run.Label);
        }
    }

    /// <summary>
    /// One Application, as an SEA keeps per vendor product, whose clients are mostly narrowed to one
    /// education organization each; every twentieth inherits the whole list, the second client is
    /// narrowed to the whole list, a few are unapproved, and a third hold a second data store.
    /// </summary>
    private async Task CreateClientsAsync(string claimSet)
    {
        int count = FanoutMeasurementEnvironment.ClientCount;
        long[] educationOrganizationIds =
        [
            .. Enumerable.Range(0, count).Select(index => FirstEducationOrganizationId + index),
        ];

        await AuthenticateAdminAsync();
        VendorId = await PostForLocationIdAsync(
            "/v3/vendors",
            new
            {
                company = $"Fanout Vendor {_runTag}",
                contactName = "Fanout",
                contactEmailAddress = "fanout@example.com",
                namespacePrefixes = NamespacePrefixes,
            }
        );
        int dataStore1 = await PostForLocationIdAsync(
            "/v3/dataStores",
            new
            {
                dataStoreType = "Test",
                name = $"Fanout Data Store 1 {_runTag}",
                connectionString = "Server=test;Database=Fanout1;",
            }
        );
        int dataStore2 = await PostForLocationIdAsync(
            "/v3/dataStores",
            new
            {
                dataStoreType = "Test",
                name = $"Fanout Data Store 2 {_runTag}",
                connectionString = "Server=test;Database=Fanout2;",
            }
        );

        JsonNode application = await PostForBodyAsync(
            "/v3/applications",
            new
            {
                vendorId = VendorId,
                applicationName = ApplicationName,
                claimSetName = claimSet,
                educationOrganizationIds,
                dataStoreIds = new[] { dataStore1 },
            }
        );
        int applicationId = application["id"]!.GetValue<int>();
        ApplicationId = applicationId;
        ApplicationEducationOrganizationIds = educationOrganizationIds;
        string firstSecret = application["secret"]!.GetValue<string>();
        _sensitive.Register(firstSecret);

        List<(int ApiClientId, string Secret)> created = [];
        JsonNode firstClient = (
            await GetJsonAsync($"/v3/apiClients?applicationid={applicationId}")
        )!.AsArray()[0]!;
        created.Add((firstClient["id"]!.GetValue<int>(), firstSecret));

        for (int ordinal = 1; ordinal < count; ordinal++)
        {
            bool inherits = ordinal % 20 == 0;
            // The second client is narrowed to the whole list, so it must not follow later changes
            bool narrowedToWholeList = ordinal == 1;
            JsonNode credentials = await PostForBodyAsync(
                "/v3/apiClients",
                new Dictionary<string, object>
                {
                    ["applicationId"] = applicationId,
                    ["name"] = $"Fanout Client {ordinal:D3} {_runTag}",
                    ["isApproved"] = ordinal % 50 != 25,
                    ["dataStoreIds"] =
                        ordinal % 3 == 0 ? new[] { dataStore1, dataStore2 } : new[] { dataStore1 },
                    ["educationOrganizationIds"] =
                        inherits || narrowedToWholeList
                            ? educationOrganizationIds
                            : new[] { educationOrganizationIds[ordinal] },
                    ["inheritsApplicationEducationOrganizations"] = inherits,
                }
            );
            string secret = credentials["secret"]!.GetValue<string>();
            _sensitive.Register(secret);
            created.Add((credentials["id"]!.GetValue<int>(), secret));
        }

        // The Configuration Service's own view of each client is the expectation the provider must keep.
        for (int ordinal = 0; ordinal < created.Count; ordinal++)
        {
            JsonNode stored = (await GetJsonAsync($"/v3/apiClients/{created[ordinal].ApiClientId}"))!;
            _clients.Add(
                new FanoutClient(
                    ordinal,
                    created[ordinal].ApiClientId,
                    stored["clientUuid"]!.GetValue<Guid>(),
                    stored["clientId"]!.GetValue<string>(),
                    created[ordinal].Secret,
                    stored["name"]!.GetValue<string>(),
                    stored["isApproved"]!.GetValue<bool>(),
                    [.. stored["educationOrganizationIds"]!.AsArray().Select(id => id!.GetValue<long>())],
                    [.. stored["dataStoreIds"]!.AsArray().Select(id => id!.GetValue<int>())],
                    stored["inheritsApplicationEducationOrganizations"]!.GetValue<bool>()
                )
            );
        }
    }

    /// <summary>Applies the change to every client, timing only the change itself.</summary>
    protected abstract Task<MeasuredRun> MeasureAsync(string label, string fromClaimSet, string toClaimSet);

    /// <summary>
    /// Every client, after every run: the Configuration Service still holds what it was created
    /// with, the provider stores the expected scope and claims, an approved client obtains a new
    /// token carrying exactly the expected claim set and claims with its original secret, and an
    /// unapproved client is refused one.
    /// </summary>
    private async Task<List<string>> VerifyAsync(
        string claimSet,
        string? previousClaimSet,
        bool captureBaseline
    )
    {
        await AuthenticateAdminAsync();
        List<string> mismatches = [];
        Lock sync = new();

        await Parallel.ForEachAsync(
            _clients,
            new ParallelOptions { MaxDegreeOfParallelism = VerificationConcurrency },
            async (client, _) =>
            {
                List<string> found = await VerifyClientAsync(
                    client,
                    claimSet,
                    previousClaimSet,
                    captureBaseline
                );
                lock (sync)
                {
                    mismatches.AddRange(found.Select(mismatch => $"client {client.Ordinal}: {mismatch}"));
                }
            }
        );

        return mismatches;
    }

    private async Task<List<string>> VerifyClientAsync(
        FanoutClient client,
        string claimSet,
        string? previousClaimSet,
        bool captureBaseline
    )
    {
        List<string> mismatches = [];

        JsonNode stored = (await GetJsonAsync($"/v3/apiClients/{client.ApiClientId}"))!;
        if (
            stored["clientUuid"]!.GetValue<Guid>() != client.ClientUuid
            || stored["clientId"]!.GetValue<string>() != client.Key
            || stored["isApproved"]!.GetValue<bool>() != client.IsApproved
            || !stored["educationOrganizationIds"]!
                .AsArray()
                .Select(id => id!.GetValue<long>())
                .SequenceEqual(client.EducationOrganizationIds)
            || !stored["dataStoreIds"]!
                .AsArray()
                .Select(id => id!.GetValue<int>())
                .SequenceEqual(client.DataStoreIds)
        )
        {
            mismatches.Add("the Configuration Service's stored client changed");
        }

        ProviderClientState state = await _provider.ReadStateAsync(client.ClientUuid);
        if (!state.Exists)
        {
            mismatches.Add("the provider no longer holds the client");
            return mismatches;
        }

        if (
            !state.Scopes.Contains(claimSet)
            || (previousClaimSet is not null && state.Scopes.Contains(previousClaimSet))
        )
        {
            mismatches.Add("the provider's stored scopes are not the expected claim set");
        }

        if (state.Enabled is { } enabled && enabled != client.IsApproved)
        {
            mismatches.Add("the provider's enabled state does not match approval");
        }

        if (state.EducationOrganizationClaim != client.EducationOrganizationClaim)
        {
            mismatches.Add("the provider's stored educationOrganizationIds claim changed");
        }

        if (state.DataStoreClaim != client.DataStoreClaim)
        {
            mismatches.Add("the provider's stored dataStoreIds claim changed");
        }

        lock (_credentialFingerprints)
        {
            if (captureBaseline)
            {
                _credentialFingerprints[client.ClientUuid] = state.CredentialFingerprint;
            }
            else if (_credentialFingerprints[client.ClientUuid] != state.CredentialFingerprint)
            {
                mismatches.Add("the provider's stored credential changed");
            }
        }

        TokenResponse tokenResponse = await RequestClientTokenAsync(client);
        if (!client.IsApproved)
        {
            if (
                tokenResponse is not TokenResponse.Refused refused
                || refused.Classification != _provider.UnapprovedClientRefusal
            )
            {
                mismatches.Add(
                    $"an unapproved client was not refused as unapproved ({tokenResponse.Description})"
                );
            }

            return mismatches;
        }

        if (tokenResponse is not TokenResponse.Issued issued)
        {
            mismatches.Add($"an approved client was refused a token ({tokenResponse.Description})");
            return mismatches;
        }

        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);
        string? Claim(string type) => token.Claims.FirstOrDefault(claim => claim.Type == type)?.Value;

        string[] tokenScopes =
        [
            .. token
                .Claims.Where(claim => claim.Type == "scope")
                .SelectMany(claim => claim.Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)),
        ];
        string[] otherScopes = [.. tokenScopes.Where(scope => scope != claimSet).Order()];
        lock (_otherTokenScopes)
        {
            if (captureBaseline)
            {
                _otherTokenScopes[client.ClientUuid] = otherScopes;
            }
            else if (!_otherTokenScopes[client.ClientUuid].SequenceEqual(otherScopes))
            {
                mismatches.Add("the token's scope holds something other than the expected claim set");
            }
        }

        if (!tokenScopes.Contains(claimSet))
        {
            mismatches.Add("the token's scope lacks the expected claim set");
        }

        if ((Claim("client_id") ?? Claim("azp")) != client.Key)
        {
            mismatches.Add("the token names another client");
        }

        if (Claim("educationOrganizationIds") != client.EducationOrganizationClaim)
        {
            mismatches.Add("the token's educationOrganizationIds claim is not the client's effective list");
        }

        if (Claim("dataStoreIds") != client.DataStoreClaim)
        {
            mismatches.Add("the token's dataStoreIds claim is not the client's data stores");
        }

        if (Claim("namespacePrefixes") != NamespacePrefixes)
        {
            mismatches.Add("the token's namespacePrefixes claim changed");
        }

        return mismatches;
    }

    /// <summary>What a token endpoint answered a client's grant.</summary>
    private abstract record TokenResponse
    {
        public abstract string Description { get; }

        public sealed record Issued(string AccessToken) : TokenResponse
        {
            public override string Description => "a token was issued";
        }

        /// <summary>
        /// A refusal, classified by its status and OAuth error code only, so nothing else the
        /// response carried can reach a mismatch message.
        /// </summary>
        public sealed record Refused(string Classification) : TokenResponse
        {
            public override string Description => Classification;
        }
    }

    private async Task<TokenResponse> RequestClientTokenAsync(FanoutClient client)
    {
        using var response = await _http.PostAsync(
            _provider.TokenEndpoint,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = client.Key,
                    ["client_secret"] = client.Secret,
                }
            )
        );
        if (ParseJson(await response.Content.ReadAsStringAsync()) is not JsonNode body)
        {
            return new TokenResponse.Refused($"{(int)response.StatusCode} without an OAuth error");
        }

        if (
            response.StatusCode == HttpStatusCode.OK
            && StringValue(body["access_token"]) is string accessToken
        )
        {
            _sensitive.Register(accessToken);
            return new TokenResponse.Issued(accessToken);
        }

        return new TokenResponse.Refused(
            OAuthError(body) is string error
                ? $"{(int)response.StatusCode} {error}"
                : $"{(int)response.StatusCode} without an OAuth error"
        );
    }

    /// <summary>
    /// The OAuth error code of a refusal: Keycloak's RFC 6749 <c>error</c> member, or the code that
    /// leads the Configuration Service's first problem-details error ("invalid_client. ...").
    /// </summary>
    private static string? OAuthError(JsonNode body) =>
        StringValue(body["error"])
        ?? (body["errors"] is JsonArray { Count: > 0 } errors ? StringValue(errors[0])?.Split('.')[0] : null);

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static JsonNode? ParseJson(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A new administrator token for each phase, so no phase outlives one.</summary>
    protected async Task AuthenticateAdminAsync()
    {
        using var response = await _http.PostAsync(
            $"{FanoutMeasurementEnvironment.ApiUrl}/connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = StepDefinitions.StepDefinitions.SystemClientId,
                    ["client_secret"] = StepDefinitions.StepDefinitions.SystemClientSecret,
                    ["scope"] = "edfi_admin_api/full_access",
                }
            )
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the administrator token is required for setup");
        string accessToken = JsonNode.Parse(await response.Content.ReadAsStringAsync())![
            "access_token"
        ]!.GetValue<string>();
        _sensitive.Register(accessToken);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private async Task<int> PostForLocationIdAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(
            $"{FanoutMeasurementEnvironment.ApiUrl}{path}",
            body
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {path} is part of setup");
        return int.Parse(response.Headers.Location!.ToString().TrimEnd('/').Split('/')[^1]);
    }

    private async Task<JsonNode> PostForBodyAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(
            $"{FanoutMeasurementEnvironment.ApiUrl}{path}",
            body
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, $"POST {path} is part of setup");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task<JsonNode?> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync($"{FanoutMeasurementEnvironment.ApiUrl}{path}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path} reads the expectation");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A JSON record of the run and a summary on the test output. Both carry only identifiers,
    /// timings and outcomes, and are refused if any secret or token of this run would appear in them.
    /// </summary>
    private async Task WriteReportAsync()
    {
        var report = new
        {
            ticket = "DMS-1642",
            mode = Mode,
            recordedAt = DateTimeOffset.UtcNow,
            provider = _provider.Description,
            configurationService = FanoutMeasurementEnvironment.ApiUrl,
            clients = _clients.Count,
            approvedClients = _clients.Count(client => client.IsApproved),
            inheritingClients = _clients.Count(client => client.Inherits),
            narrowedToWholeListClients = _clients.Count(client =>
                !client.Inherits
                && client.EducationOrganizationIds.Length == ApplicationEducationOrganizationIds.Length
            ),
            setupSeconds = Math.Round(_setupDuration.TotalSeconds, 1),
            baselineMismatches = _baselineMismatches,
            runs = _runs.Select(run => new
            {
                run.Label,
                run.FromClaimSet,
                run.ToClaimSet,
                totalMilliseconds = Math.Round(run.TotalMilliseconds, 1),
                // Only a provider-mode run times each client; an endpoint-mode run has no per-client timing
                perClient = run.PerClientMilliseconds.Length == 0
                    ? null
                    : new
                    {
                        firstClientMilliseconds = Math.Round(run.PerClientMilliseconds[0], 1),
                        minMilliseconds = Math.Round(run.PerClientMilliseconds.Min(), 1),
                        meanMilliseconds = Math.Round(run.PerClientMilliseconds.Average(), 1),
                        p50Milliseconds = Math.Round(run.Percentile(50), 1),
                        p90Milliseconds = Math.Round(run.Percentile(90), 1),
                        p95Milliseconds = Math.Round(run.Percentile(95), 1),
                        p99Milliseconds = Math.Round(run.Percentile(99), 1),
                        maxMilliseconds = Math.Round(run.PerClientMilliseconds.Max(), 1),
                        milliseconds = run.PerClientMilliseconds.Select(ms => Math.Round(ms, 2)),
                    },
                run.Attempted,
                run.Succeeded,
                failed = run.Attempted - run.Succeeded,
                run.Failures,
                run.VerificationMismatches,
            }),
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (_sensitive.ContainsAny(json))
        {
            throw new InvalidOperationException(
                "The measurement report would contain a secret or token; it was not written."
            );
        }

        Directory.CreateDirectory(FanoutMeasurementEnvironment.OutputDirectory);
        string path = Path.Combine(
            FanoutMeasurementEnvironment.OutputDirectory,
            $"dms-1642-fanout-{Mode}-{FanoutMeasurementEnvironment.IdentityProvider}-{(SetupHooks.UseMssql ? "mssql" : "postgresql")}-{_runTag}.json"
        );
        await File.WriteAllTextAsync(path, json);

        await TestContext.Progress.WriteLineAsync($"[fanout] report: {path}");
        await TestContext.Progress.WriteLineAsync(
            $"[fanout] {Mode}; {_provider.Description}; {_clients.Count} clients; setup {_setupDuration.TotalSeconds:F1}s"
        );
        foreach (MeasuredRun run in _runs)
        {
            string perClient =
                run.PerClientMilliseconds.Length == 0
                    ? ""
                    : $"first {run.PerClientMilliseconds[0]:F1}ms, p50 {run.Percentile(50):F1}ms, "
                        + $"p95 {run.Percentile(95):F1}ms, max {run.PerClientMilliseconds.Max():F1}ms, ";
            await TestContext.Progress.WriteLineAsync(
                $"[fanout] {run.Label}: total {run.TotalMilliseconds / 1000:F2}s, {perClient}"
                    + $"succeeded {run.Succeeded}/{run.Attempted}, verification mismatches {run.VerificationMismatches.Count}"
            );
        }
    }
}

/// <summary>
/// Task 1's baseline (Jira scope "First, measure"): the real IIdentityProviderRepository.UpdateClientAsync
/// of the provider the isolated stack runs, called for every client in turn, each call timed.
/// </summary>
[TestFixture]
[Category(FanoutMeasurementEnvironment.Category)]
[NonParallelizable]
public class Given_claim_set_changes_applied_to_every_client_of_one_application
    : ApplicationUpdateFanoutMeasurement
{
    protected override string Mode => "provider";

    /// <summary>
    /// The timed interval: one repository, as one Application PUT would resolve, updating each
    /// client in turn with its own name, effective education organizations, data stores and approval.
    /// </summary>
    protected override async Task<MeasuredRun> MeasureAsync(
        string label,
        string fromClaimSet,
        string toClaimSet
    )
    {
        IIdentityProviderRepository repository = _provider.CreateRepository();
        double[] perClient = new double[_clients.Count];
        List<string> failures = [];
        int succeeded = 0;

        Stopwatch total = Stopwatch.StartNew();
        foreach (FanoutClient client in _clients)
        {
            long started = Stopwatch.GetTimestamp();
            ClientUpdateResult result = await repository.UpdateClientAsync(
                client.ClientUuid.ToString(),
                client.Name,
                toClaimSet,
                client.EducationOrganizationClaim,
                client.DataStoreIds,
                client.IsApproved,
                "dms-client"
            );
            perClient[client.Ordinal] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (result is ClientUpdateResult.Success success && success.ClientUuid == client.ClientUuid)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"client {client.Ordinal}: {result.GetType().Name}");
            }
        }
        total.Stop();

        return new MeasuredRun(
            label,
            fromClaimSet,
            toClaimSet,
            total.Elapsed.TotalMilliseconds,
            perClient,
            _clients.Count,
            succeeded,
            [.. failures]
        );
    }
}

/// <summary>
/// The finished feature: the claim set change sent as one PUT /v3/applications/{id} to the running
/// Configuration Service, which saves it, marks every client, updates each client in its identity
/// provider and clears each marker before responding. The timed interval is that request alone. A run
/// counts a client as updated only when the request answered 204 and the client's marker is cleared in
/// the Configuration Service database afterwards.
/// </summary>
[TestFixture]
[Category(FanoutMeasurementEnvironment.Category)]
[NonParallelizable]
public class Given_claim_set_changes_saved_through_the_application_endpoint
    : ApplicationUpdateFanoutMeasurement
{
    protected override string Mode => "endpoint";

    protected override async Task<MeasuredRun> MeasureAsync(
        string label,
        string fromClaimSet,
        string toClaimSet
    )
    {
        await AuthenticateAdminAsync();
        using HttpRequestMessage request = new(
            HttpMethod.Put,
            $"{FanoutMeasurementEnvironment.ApiUrl}/v3/applications/{ApplicationId}"
        )
        {
            Content = JsonContent.Create(
                new
                {
                    id = ApplicationId,
                    applicationName = ApplicationName,
                    vendorId = VendorId,
                    claimSetName = toClaimSet,
                    educationOrganizationIds = ApplicationEducationOrganizationIds,
                    profileIds = Array.Empty<int>(),
                }
            ),
        };

        Stopwatch total = Stopwatch.StartNew();
        using HttpResponseMessage response = await _http.SendAsync(request);
        total.Stop();

        List<string> failures = [];
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            failures.Add($"the request answered {(int)response.StatusCode}");
        }

        int[] pending = await PendingClientIdsAsync();
        failures.AddRange(pending.Select(apiClientId => $"API client {apiClientId} is still pending"));

        return new MeasuredRun(
            label,
            fromClaimSet,
            toClaimSet,
            total.Elapsed.TotalMilliseconds,
            [],
            _clients.Count,
            _clients.Count - pending.Length,
            [.. failures]
        );
    }

    /// <summary>The Application's clients whose pending marker is set, read from the database.</summary>
    private async Task<int[]> PendingClientIdsAsync()
    {
        const string Sql =
            "SELECT \"Id\" FROM \"dmscs\".\"ApiClient\" WHERE \"ApplicationId\" = @ApplicationId AND \"IdentityProviderSyncPendingSince\" IS NOT NULL";
        const string MssqlSql =
            "SELECT Id FROM dmscs.ApiClient WHERE ApplicationId = @ApplicationId AND IdentityProviderSyncPendingSince IS NOT NULL";

        await using System.Data.Common.DbConnection connection = SetupHooks.UseMssql
            ? new Microsoft.Data.SqlClient.SqlConnection(SetupHooks.DatabaseConnectionString)
            : new Npgsql.NpgsqlConnection(SetupHooks.DatabaseConnectionString);
        await connection.OpenAsync();
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = SetupHooks.UseMssql ? MssqlSql : Sql;
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "ApplicationId";
        parameter.Value = ApplicationId;
        command.Parameters.Add(parameter);

        List<int> ids = [];
        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return [.. ids];
    }
}
