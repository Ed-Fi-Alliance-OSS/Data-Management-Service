// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Tests.E2E.SecretResolution;

/// <summary>
/// The secret resolution proofs, run by eng/docker-compose/tests/secret-resolution/Invoke-SecretResolutionE2E.ps1
/// against the isolated deployment it builds: a Configuration Service with the Acme.FileSecretResolver
/// fixture plugin mounted through plugins-config.yml, and a DMS beside it. The harness supplies every
/// setting through SECRETS_E2E_* variables; without them, as in an ordinary run of this suite, the
/// fixtures are ignored. Secret values never reach the test output: comparisons are reported as
/// matched or not, never with the values.
/// </summary>
public abstract class SecretResolutionPluginTestBase
{
    private const string CategoryName = "SecretResolutionPlugin";

    protected static string Setting(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required by the secret resolution proofs.");

    protected HttpClient Cms { get; private set; } = null!;
    protected ConnectionStringEncryptionService Encryption { get; private set; } = null!;
    protected string SecretsFile { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task ConnectToTheHarnessDeployment()
    {
        if (Environment.GetEnvironmentVariable("SECRETS_E2E_CMS_URL") is null)
        {
            Assert.Ignore(
                $"The {CategoryName} proofs run only under Invoke-SecretResolutionE2E.ps1, which deploys the fixture plugin."
            );
        }

        SecretsFile = Setting("SECRETS_E2E_SECRETS_FILE");
        Encryption = new ConnectionStringEncryptionService(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = string.Empty,
                    EncryptionKey = Setting("SECRETS_E2E_ENCRYPTION_KEY"),
                }
            )
        );

        Cms = new HttpClient { BaseAddress = new Uri(Setting("SECRETS_E2E_CMS_URL")) };
        using HttpResponseMessage token = await Cms.PostAsync(
            "connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = Setting("SECRETS_E2E_CMS_CLIENT_ID"),
                    ["client_secret"] = Setting("SECRETS_E2E_CMS_CLIENT_SECRET"),
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "edfi_admin_api/full_access",
                }
            )
        );
        token.StatusCode.Should().Be(HttpStatusCode.OK);
        string accessToken = JsonNode.Parse(await token.Content.ReadAsStringAsync())![
            "access_token"
        ]!.GetValue<string>();
        Cms.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    [OneTimeTearDown]
    public void Disconnect() => Cms?.Dispose();

    /// <summary>
    /// Writes one entry of the store the fixture reads. The file is replaced by a rename, so the
    /// resolver never reads a half-written file.
    /// </summary>
    protected void WriteSecret(string name, string value)
    {
        Dictionary<string, string> secrets =
            JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsFile)) ?? [];
        secrets[name] = value;

        string staged = $"{SecretsFile}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(secrets));
        File.Move(staged, SecretsFile, overwrite: true);
    }

    protected static string ConnectionStringWithPassword(string password) =>
        $"host=dms-postgresql;port=5432;username=postgres;password={password};database={Setting("SECRETS_E2E_DATASTORE_DATABASE")}";

    protected async Task<int> CreateDataStore(string connectionString)
    {
        using HttpResponseMessage response = await Cms.PostAsJsonAsync(
            "v3/dataStores",
            new
            {
                dataStoreType = "Test",
                name = $"Secret resolution {Guid.NewGuid():N}",
                connectionString,
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
    }

    /// <summary>The password a read of the data store through the Configuration Service carries.</summary>
    protected async Task<string> PasswordReadFromCms(int dataStoreId)
    {
        using HttpResponseMessage response = await Cms.GetAsync($"v3/dataStores/{dataStoreId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string base64 = JsonNode.Parse(await response.Content.ReadAsStringAsync())![
            "connectionString"
        ]!.GetValue<string>();
        string plainText = Encryption.Decrypt(Convert.FromBase64String(base64))!;
        return new NpgsqlConnectionStringBuilder(plainText).Password!;
    }
}

/// <summary>
/// A data store created through the Configuration Service with only a reference to its database
/// password, by the harness before DMS started: the Configuration Service resolves it through the
/// plugin, and DMS, which receives the resolved connection string exactly as it would any other, opens
/// the database with it and serves a resource out of it. Without resolution
/// DMS would be handed the literal reference as a password and every write would fail to connect.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("SecretResolutionPlugin")]
public class Given_a_data_store_whose_password_is_a_secret_reference : SecretResolutionPluginTestBase
{
    private const string NoFurtherAuthRequiredClaimSet = "E2E-NoFurtherAuthRequiredClaimSet";

    private int _dataStoreId;
    private HttpClient _dms = null!;

    [OneTimeSetUp]
    public async Task CreateTheDataStoreAndAClient()
    {
        // Created by the harness before DMS started, because DMS will not start without one.
        _dataStoreId = int.Parse(Setting("SECRETS_E2E_DATASTORE_ID"));

        using HttpResponseMessage vendor = await Cms.PostAsJsonAsync(
            "v3/vendors",
            new
            {
                company = $"Secret resolution vendor {Guid.NewGuid():N}",
                contactName = "Secret Resolution",
                contactEmailAddress = "secret.resolution@example.com",
                namespacePrefixes = "uri://ed-fi.org",
            }
        );
        vendor.StatusCode.Should().Be(HttpStatusCode.Created);
        using HttpResponseMessage createdVendor = await Cms.GetAsync(vendor.Headers.Location);
        int vendorId = JsonNode.Parse(await createdVendor.Content.ReadAsStringAsync())![
            "id"
        ]!.GetValue<int>();

        using HttpResponseMessage application = await Cms.PostAsJsonAsync(
            "v3/applications",
            new
            {
                vendorId,
                applicationName = "Secret resolution",
                claimSetName = NoFurtherAuthRequiredClaimSet,
                educationOrganizationIds = Array.Empty<long>(),
                dataStoreIds = new[] { _dataStoreId },
            }
        );
        application.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonNode credentials = JsonNode.Parse(await application.Content.ReadAsStringAsync())!;

        _dms = new HttpClient { BaseAddress = new Uri(Setting("SECRETS_E2E_DMS_URL").TrimEnd('/') + "/") };
        string basic = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                $"{credentials["key"]!.GetValue<string>()}:{credentials["secret"]!.GetValue<string>()}"
            )
        );
        using HttpRequestMessage tokenRequest = new(HttpMethod.Post, "oauth/token")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["grant_type"] = "client_credentials" }
            ),
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using HttpResponseMessage token = await _dms.SendAsync(tokenRequest);
        token.StatusCode.Should().Be(HttpStatusCode.OK);
        _dms.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JsonNode.Parse(await token.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>()
        );
    }

    [OneTimeTearDown]
    public void DisconnectFromDms() => _dms?.Dispose();

    [Test]
    public async Task It_returns_the_resolved_password_from_the_configuration_service()
    {
        string password = await PasswordReadFromCms(_dataStoreId);
        Dictionary<string, string> store = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(SecretsFile)
        )!;

        (password == store[Setting("SECRETS_E2E_DATASTORE_SECRET")])
            .Should()
            .BeTrue("the password must be the value the plugin's store holds");
        password.Should().NotContain("${secret:");
    }

    [Test]
    public async Task It_lets_dms_write_and_read_a_resource_through_the_resolved_credential()
    {
        string codeValue = $"SecretResolution{Guid.NewGuid():N}"[..40];

        using HttpResponseMessage created = await _dms.PostAsJsonAsync(
            "data/ed-fi/absenceEventCategoryDescriptors",
            new
            {
                codeValue,
                shortDescription = "Written through a resolved secret",
                @namespace = "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
            }
        );
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        using HttpResponseMessage read = await _dms.GetAsync(created.Headers.Location);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await read.Content.ReadAsStringAsync())!["codeValue"]!
            .GetValue<string>()
            .Should()
            .Be(codeValue);
    }
}

/// <summary>
/// A rotation in the store reaches the Configuration Service's responses when the host's absolute
/// cache window has elapsed, and not before. The fixture reads its file on every call, so the window
/// measured here is the host's alone. The value is first resolved no earlier than the data store's
/// creation, so the window cannot close before creation plus the expiration; it closes no later than
/// the first read plus the expiration.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("SecretResolutionPlugin")]
public class Given_a_secret_rotated_in_the_store : SecretResolutionPluginTestBase
{
    /// <summary>Allowance for the clock granularity between the host's stamp and this test's.</summary>
    private static readonly TimeSpan _margin = TimeSpan.FromSeconds(1);

    /// <summary>How late the new value may be observed after the window, polling once a second.</summary>
    private static readonly TimeSpan _pollSlack = TimeSpan.FromSeconds(5);

    private TimeSpan _window;
    private TimeSpan _rotatedAfterCreation;
    private TimeSpan _firstReadAfterCreation;
    private TimeSpan _insideReadAfterCreation;
    private TimeSpan _lastPreviousAfterCreation;
    private TimeSpan _firstRotatedAfterCreation;
    private bool _firstReadIsPrevious;
    private bool _insideReadIsPrevious;
    private bool _everyLaterReadIsRotated = true;
    private bool _onlyKnownValuesReturned = true;
    private string _timings = string.Empty;

    [OneTimeSetUp]
    public async Task RotateAndObserve()
    {
        _window = TimeSpan.FromSeconds(int.Parse(Setting("SECRETS_E2E_CACHE_EXPIRATION_SECONDS")));
        string name = $"rotation/{Guid.NewGuid():N}";
        string previous = $"previous-{Guid.NewGuid():N}";
        string rotated = $"rotated-{Guid.NewGuid():N}";
        WriteSecret(name, previous);

        int dataStoreId = await CreateDataStore(ConnectionStringWithPassword($"${{secret:{name}}}"));
        Stopwatch sinceCreation = Stopwatch.StartNew();

        _firstReadIsPrevious = await PasswordReadFromCms(dataStoreId) == previous;
        _firstReadAfterCreation = sinceCreation.Elapsed;

        WriteSecret(name, rotated);
        _rotatedAfterCreation = sinceCreation.Elapsed;

        _insideReadIsPrevious = await PasswordReadFromCms(dataStoreId) == previous;
        _insideReadAfterCreation = sinceCreation.Elapsed;

        TimeSpan deadline = _firstReadAfterCreation + _window + _pollSlack;
        _firstRotatedAfterCreation = TimeSpan.MaxValue;
        while (sinceCreation.Elapsed < deadline)
        {
            TimeSpan before = sinceCreation.Elapsed;
            string password = await PasswordReadFromCms(dataStoreId);

            if (_firstRotatedAfterCreation != TimeSpan.MaxValue)
            {
                // Once the rotated value has been seen, nothing else may be returned.
                _everyLaterReadIsRotated &= password == rotated;
            }
            else if (password == rotated)
            {
                _firstRotatedAfterCreation = sinceCreation.Elapsed;
            }
            else if (password == previous)
            {
                _lastPreviousAfterCreation = before;
            }
            else
            {
                _onlyKnownValuesReturned = false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        _timings = (
            $"window {_window.TotalSeconds:F0}s; after creation: first read {_firstReadAfterCreation.TotalSeconds:F2}s, "
            + $"rotated {_rotatedAfterCreation.TotalSeconds:F2}s, inside read {_insideReadAfterCreation.TotalSeconds:F2}s, "
            + $"last previous {_lastPreviousAfterCreation.TotalSeconds:F2}s, first rotated {(_firstRotatedAfterCreation == TimeSpan.MaxValue ? "never" : $"{_firstRotatedAfterCreation.TotalSeconds:F2}s")}"
        );
    }

    [Test]
    public void It_resolves_the_value_the_store_held_first() => _firstReadIsPrevious.Should().BeTrue();

    /// <summary>
    /// The inside read proves something only if it finished before the earliest moment the window can
    /// have closed, the same bound the early-rotation test uses.
    /// </summary>
    [Test]
    public void It_observed_the_inside_read_inside_the_window() =>
        _insideReadAfterCreation.Should().BeLessThan(_window - _margin);

    [Test]
    public void It_keeps_the_previous_value_inside_the_window() => _insideReadIsPrevious.Should().BeTrue();

    [Test]
    public void It_does_not_return_the_rotated_value_before_the_window_can_have_closed() =>
        _firstRotatedAfterCreation.Should().BeGreaterThanOrEqualTo(_window - _margin);

    [Test]
    public void It_returns_the_rotated_value_once_the_window_has_closed()
    {
        // Recorded with the result, durations only, so the measured window survives in the TRX.
        TestContext.Out.WriteLine(_timings);
        _firstRotatedAfterCreation
            .Should()
            .BeLessThanOrEqualTo(_firstReadAfterCreation + _window + _pollSlack);
    }

    [Test]
    public void It_keeps_returning_the_rotated_value() => _everyLaterReadIsRotated.Should().BeTrue();

    [Test]
    public void It_returns_only_the_previous_or_the_rotated_value() =>
        _onlyKnownValuesReturned.Should().BeTrue();
}
