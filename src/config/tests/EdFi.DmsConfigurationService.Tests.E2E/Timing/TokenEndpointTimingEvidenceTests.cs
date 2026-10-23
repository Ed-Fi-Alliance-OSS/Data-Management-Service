// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.E2E.Management;
using EdFi.DmsConfigurationService.Tests.TimingEvidence;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Timing;

/// <summary>
/// Collects timing samples for <c>POST /connect/token</c> on a running Configuration Service, end
/// to end over HTTP, with one keep-alive client and no concurrency. It records samples and asserts
/// nothing about them: the offline analysis decides. Explicit, so it never runs in an ordinary E2E
/// run; run it only in the documented controlled environment, against a freshly provisioned stack,
/// with the <c>TIMING_EVIDENCE_*</c> settings: <c>TIMING_EVIDENCE_BACKEND</c> names the database the
/// stack uses, <c>TIMING_EVIDENCE_TARGET_BUILD</c> the build the stack runs (distinct from the
/// collector's own <c>TIMING_EVIDENCE_COMMIT</c>), <c>TIMING_EVIDENCE_DECLARED_ITERATIONS</c> its
/// configured hashing count, and <c>TIMING_EVIDENCE_CONTRACT</c> (<c>current</c>, the default, or
/// <c>pre-fix</c>) the error contract that build answers with. These are declarations; the run
/// submission verifies them against the deployment.
/// <para>
/// Arms: an absent client id; a client registered for the run, presented with a wrong secret; and
/// an unapproved API client created for the run, presented with its own secret. Each arm must
/// receive the response its contract names (<see cref="TokenEndpointTimingContract"/>); anything
/// else aborts the run. Each request's time covers building the form, sending it, reading the whole
/// response body and disposing the response. The clients the run creates are left in place.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Timing evidence collector; run only in the documented controlled environment.")]
[Category("TimingEvidence")]
[NonParallelizable]
public class Given_token_endpoint_requests_measured_for_timing_evidence
{
    private const string AdminScope = "edfi_admin_api/full_access";

    private HttpClient _client = null!;
    private TimingRunSettings _settings = null!;
    private TimingRunFiles _files = null!;
    private int _arms;

    private async Task<(HttpStatusCode Status, string Body)> RequestTokenAsync(string clientId, string secret)
    {
        using FormUrlEncodedContent form = new(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["grant_type"] = "client_credentials",
                ["scope"] = AdminScope,
            }
        );
        using HttpResponseMessage response = await _client.PostAsync("connect/token", form);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> AdminTokenAsync()
    {
        (HttpStatusCode status, string body) = await RequestTokenAsync(
            StepDefinitions.StepDefinitions.SystemClientId,
            StepDefinitions.StepDefinitions.SystemClientSecret
        );
        status.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!["access_token"]!.GetValue<string>();
    }

    private async Task RegisterAsync(string clientId, string secret)
    {
        using FormUrlEncodedContent form = new(
            new Dictionary<string, string>
            {
                ["ClientId"] = clientId,
                ["ClientSecret"] = secret,
                ["DisplayName"] = clientId,
            }
        );
        using HttpResponseMessage response = await _client.PostAsync("connect/register", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> CreateAsync(string path, object body, string adminToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using HttpResponseMessage response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return int.Parse(
            response.Headers.Location!.Segments[^1],
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    /// Creates a vendor, a data store and an application, then an API client with
    /// <c>isApproved: false</c>, as Token.feature's disabled-client scenario does, and returns that
    /// client's credentials.
    /// </summary>
    private async Task<(string Key, string Secret)> CreateUnapprovedApiClientAsync(
        string runId,
        string adminToken
    )
    {
        int vendorId = await CreateAsync(
            "v3/vendors",
            new
            {
                company = $"Timing-{runId}",
                contactName = "Timing",
                contactEmailAddress = "timing@example.com",
                namespacePrefixes = $"uri://timing-{runId}.org",
            },
            adminToken
        );
        int dataStoreId = await CreateAsync(
            "v3/dataStores",
            new
            {
                dataStoreType = "Test",
                name = $"Timing-{runId}",
                connectionString = "Server=test;Database=TestDb;",
            },
            adminToken
        );
        int applicationId = await CreateAsync(
            "v3/applications",
            new
            {
                vendorId,
                applicationName = $"Timing-{runId}",
                claimSetName = "TimingClaimSet",
                educationOrganizationIds = Array.Empty<long>(),
                dataStoreIds = new[] { dataStoreId },
            },
            adminToken
        );

        using HttpRequestMessage request = new(HttpMethod.Post, "v3/apiClients")
        {
            Content = JsonContent.Create(
                new
                {
                    applicationId,
                    name = $"Timing-unapproved-{runId}",
                    isApproved = false,
                    dataStoreIds = new[] { dataStoreId },
                }
            ),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        JsonNode created = JsonNode.Parse(body)!;
        return (created["key"]!.GetValue<string>(), created["secret"]!.GetValue<string>());
    }

    [OneTimeSetUp]
    public async Task Record()
    {
        _settings = TimingRunSettings.FromEnvironment();
        string backend = TimingRunSettings.Required("TIMING_EVIDENCE_BACKEND");
        TokenEndpointContract contract = TokenEndpointTimingContract.Parse(
            Environment.GetEnvironmentVariable("TIMING_EVIDENCE_CONTRACT")
        );
        string targetBuild = _settings.Declared("TIMING_EVIDENCE_TARGET_BUILD");
        string declaredIterations = _settings.Declared("TIMING_EVIDENCE_DECLARED_ITERATIONS");
        string apiUrl = new PlaywrightContext().ApiUrl;
        _client = new HttpClient(
            new SocketsHttpHandler { PooledConnectionLifetime = Timeout.InfiniteTimeSpan }
        )
        {
            BaseAddress = new Uri(apiUrl),
        };

        string runId = Guid.NewGuid().ToString("N");
        string adminToken = await AdminTokenAsync();

        string knownClientId = $"timing-known-{runId}";
        await RegisterAsync(knownClientId, $"S3cr3t!Timing{runId}");
        (string unapprovedKey, string unapprovedSecret) = await CreateUnapprovedApiClientAsync(
            runId,
            adminToken
        );

        // The absent and wrong-secret arms present the same secret, so the derivation input is alike.
        string absentClientId = $"timing-absent-{runId}";
        string wrongSecret = $"Wr0ng!Timing{runId}";

        Func<(HttpStatusCode Status, string Body), bool> Expects(string arm) =>
            response => TokenEndpointTimingContract.IsExpected(contract, arm, response.Status, response.Body);

        TimingArm<(HttpStatusCode, string)>[] arms =
        [
            new("absent", () => RequestTokenAsync(absentClientId, wrongSecret), Expects("absent")),
            new(
                TokenEndpointTimingContract.WrongSecretArm,
                () => RequestTokenAsync(knownClientId, wrongSecret),
                Expects(TokenEndpointTimingContract.WrongSecretArm)
            ),
            new(
                "unapproved_correct_secret",
                () => RequestTokenAsync(unapprovedKey, unapprovedSecret),
                Expects("unapproved_correct_secret")
            ),
        ];
        _arms = arms.Length;

        Dictionary<string, string> context = new()
        {
            ["backend"] = backend,
            ["contract"] = TokenEndpointTimingContract.Name(contract),
            ["target_build"] = targetBuild,
            ["api_authority"] = new Uri(apiUrl).Authority,
            ["declared_iterations"] = declaredIterations,
            ["bound_us"] = "250",
            ["estimand"] = "median(absent) - median(wrong_secret)",
            ["timed_span"] =
                "form construction, HttpClient.PostAsync, reading the whole response body, response disposal",
        };

        _files = await TimingEvidenceRecorder.RunAsync(_settings, $"endpoint-{backend}", context, arms);
        await TestContext.Out.WriteLineAsync($"Samples: {_files.SamplesPath}");
        await TestContext.Out.WriteLineAsync($"Header: {_files.HeaderPath}");
    }

    [OneTimeTearDown]
    public void Release() => _client?.Dispose();

    [Test]
    public void It_records_one_sample_per_arm_per_round() =>
        File.ReadAllLines(_files.SamplesPath).Length.Should().Be(1 + (_settings.Rounds * _arms));

    [Test]
    public void It_writes_the_run_header() => File.Exists(_files.HeaderPath).Should().BeTrue();
}
