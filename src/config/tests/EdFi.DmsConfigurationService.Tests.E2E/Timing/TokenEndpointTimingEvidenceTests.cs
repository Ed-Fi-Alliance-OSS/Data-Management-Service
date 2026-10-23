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
/// with the <c>TIMING_EVIDENCE_*</c> settings and <c>TIMING_EVIDENCE_BACKEND</c> naming the
/// database the stack uses.
/// <para>
/// Arms: an absent client id; a client registered for the run, presented with a wrong secret; and
/// an unapproved API client created for the run, presented with its own secret. Each request's time
/// covers sending it and reading the whole response body. The clients the run creates are left in
/// place.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Timing evidence collector; run only in the documented controlled environment.")]
[Category("TimingEvidence")]
[NonParallelizable]
public class Given_token_endpoint_requests_measured_for_timing_evidence
{
    private const string ExpectedError = "invalid_client. Invalid client or Invalid client credentials";
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

    private static bool IsInvalidClient((HttpStatusCode Status, string Body) response) =>
        response.Status == HttpStatusCode.Unauthorized
        && JsonNode.Parse(response.Body)?["errors"]?[0]?.GetValue<string>() == ExpectedError;

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

        TimingArm<(HttpStatusCode, string)>[] arms =
        [
            new("absent", () => RequestTokenAsync(absentClientId, wrongSecret), IsInvalidClient),
            new("wrong_secret", () => RequestTokenAsync(knownClientId, wrongSecret), IsInvalidClient),
            new(
                "unapproved_correct_secret",
                () => RequestTokenAsync(unapprovedKey, unapprovedSecret),
                IsInvalidClient
            ),
        ];
        _arms = arms.Length;

        Dictionary<string, string> context = new()
        {
            ["backend"] = backend,
            ["api_authority"] = new Uri(apiUrl).Authority,
            ["declared_iterations"] =
                Environment.GetEnvironmentVariable("TIMING_EVIDENCE_DECLARED_ITERATIONS") ?? "not declared",
            ["bound_us"] = "250",
            ["estimand"] = "median(absent) - median(wrong_secret)",
            ["timed_span"] = "HttpClient.PostAsync through reading the whole response body",
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
