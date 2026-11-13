// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

/// <summary>
/// A stand-in Configuration Service: every create answers 201 with the next id in its Location (and, for an
/// application or an import, in its body), every delete answers 204, unless a call is told to answer otherwise or
/// to throw. It records every call in order.
/// </summary>
internal sealed class FakeConfigurationService
{
    private int _nextId = 100;
    private readonly Dictionary<string, HttpStatusCode> _answers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _throwing = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    public IEnumerable<string> Deletions =>
        Calls.Where(call => call.StartsWith("DELETE ", StringComparison.Ordinal));

    public void Answer(HttpMethod method, string path, HttpStatusCode status) =>
        _answers[$"{method} {path}"] = status;

    public void Throw(HttpMethod method, string path) => _throwing.Add($"{method} {path}");

    public Task<ConfigurationServiceAnswer> CallAsync(HttpMethod method, string path, string json)
    {
        string call = $"{method} {path}";
        Calls.Add(call);

        if (_throwing.Contains(call))
        {
            throw new HttpRequestException("connection refused");
        }

        if (_answers.TryGetValue(call, out HttpStatusCode status))
        {
            return Task.FromResult(new ConfigurationServiceAnswer(status, "{}", null));
        }

        if (method == HttpMethod.Delete)
        {
            return Task.FromResult(new ConfigurationServiceAnswer(HttpStatusCode.NoContent, "", null));
        }

        int id = _nextId++;
        string collection = path == "v3/claimSets/import" ? "v3/claimSets" : path;
        string body = path switch
        {
            "v3/applications" => $$"""{"id":{{id}},"key":"key-{{id}}","secret":"secret-{{id}}"}""",
            "v3/claimSets/import" => $$"""{"id":{{id}},"warnings":[]}""",
            _ => "",
        };
        return Task.FromResult(
            new ConfigurationServiceAnswer(
                HttpStatusCode.Created,
                body,
                new Uri($"http://cms/{collection}/{id}")
            )
        );
    }
}

public class ProjectionScenarioRecordsTests
{
    private static JsonObject ClaimSet() => new() { ["claimSetName"] = "E2EProjectionTest" };

    [TestFixture]
    public class Given_a_scenario_that_created_all_its_records
    {
        private FakeConfigurationService _service = null!;
        private IReadOnlyList<string> _failures = null!;
        private ClientCredentials _resourceOnly = null!;
        private int _removalsOnASecondPass;

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before every test of the fixture: each starts with a fresh service.
            _service = new FakeConfigurationService();
            ProjectionScenarioRecords records = new(_service.CallAsync);

            int dataStoreId = await records.CreateDataStoreAsync("Host=dms-postgresql"); // 100
            int resourceVendorId = await records.CreateVendorAsync("uri://ed-fi.org"); // 101
            _resourceOnly = await records.CreateApplicationAsync(
                resourceVendorId,
                "EdFiSandbox",
                [dataStoreId]
            ); // 102
            await records.ImportClaimSetAsync(ClaimSet()); // 103
            int projectionVendorId = await records.CreateVendorAsync("uri://ed-fi.org"); // 104
            await records.CreateApplicationAsync(projectionVendorId, "E2EProjectionTest", []); // 105

            _failures = await records.RemoveAllAsync();
            int deletions = _service.Deletions.Count();
            await records.RemoveAllAsync();
            _removalsOnASecondPass = _service.Deletions.Count() - deletions;
        }

        [Test]
        public void It_returns_the_application_client_credentials() =>
            _resourceOnly.Should().Be(new ClientCredentials("key-102", "secret-102"));

        [Test]
        public void It_deletes_every_record_once_applications_first_then_claim_sets_vendors_and_data_stores() =>
            _service
                .Deletions.Should()
                .Equal(
                    "DELETE v3/applications/105",
                    "DELETE v3/applications/102",
                    "DELETE v3/claimSets/103",
                    "DELETE v3/vendors/104",
                    "DELETE v3/vendors/101",
                    "DELETE v3/dataStores/100"
                );

        [Test]
        public void It_reports_no_failure() => _failures.Should().BeEmpty();

        [Test]
        public void It_has_nothing_left_to_remove_afterwards() => _removalsOnASecondPass.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_scenario_that_fails_right_after_the_claim_set_import
    {
        private FakeConfigurationService _service = null!;
        private int _claimSetId;

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before every test of the fixture: each starts with a fresh service.
            _service = new FakeConfigurationService();
            ProjectionScenarioRecords records = new(_service.CallAsync);

            try
            {
                int dataStoreId = await records.CreateDataStoreAsync("Host=dms-postgresql"); // 100
                int vendorId = await records.CreateVendorAsync("uri://ed-fi.org"); // 101
                await records.CreateApplicationAsync(vendorId, "EdFiSandbox", [dataStoreId]); // 102
                _claimSetId = await records.ImportClaimSetAsync(ClaimSet()); // 103

                // The scenario's next check (the export read back, or the DMS reload) fails here.
                throw new InvalidOperationException("the export does not grant Read");
            }
            catch (InvalidOperationException)
            {
                await records.RemoveAllAsync();
            }
        }

        [Test]
        public void It_knows_the_imported_claim_set_from_the_import_answer() => _claimSetId.Should().Be(103);

        [Test]
        public void It_removes_the_imported_claim_set_and_every_record_created_before_it() =>
            _service
                .Deletions.Should()
                .Equal(
                    "DELETE v3/applications/102",
                    "DELETE v3/claimSets/103",
                    "DELETE v3/vendors/101",
                    "DELETE v3/dataStores/100"
                );
    }

    [TestFixture]
    public class Given_a_create_the_service_refuses
    {
        private FakeConfigurationService _service = null!;
        private Exception? _refusal;

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before every test of the fixture: each starts with a fresh service.
            _service = new FakeConfigurationService();
            _service.Answer(HttpMethod.Post, "v3/vendors", HttpStatusCode.Conflict);
            ProjectionScenarioRecords records = new(_service.CallAsync);

            await records.CreateDataStoreAsync("Host=dms-postgresql"); // 100
            try
            {
                await records.CreateVendorAsync("uri://ed-fi.org");
            }
            catch (InvalidOperationException exception)
            {
                _refusal = exception;
            }

            await records.RemoveAllAsync();
        }

        [Test]
        public void It_fails_naming_the_call_and_the_answer() =>
            _refusal!.Message.Should().Be("Setup failed: creating a vendor answered 409.");

        [Test]
        public void It_removes_only_what_was_created() =>
            _service.Deletions.Should().Equal("DELETE v3/dataStores/100");
    }

    [TestFixture]
    public class Given_deletions_that_fail
    {
        private FakeConfigurationService _service = null!;
        private IReadOnlyList<string> _failures = null!;

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before every test of the fixture: each starts with a fresh service.
            _service = new FakeConfigurationService();
            _service.Answer(HttpMethod.Delete, "v3/applications/102", HttpStatusCode.InternalServerError);
            _service.Throw(HttpMethod.Delete, "v3/claimSets/103");
            ProjectionScenarioRecords records = new(_service.CallAsync);

            int dataStoreId = await records.CreateDataStoreAsync("Host=dms-postgresql"); // 100
            int vendorId = await records.CreateVendorAsync("uri://ed-fi.org"); // 101
            await records.CreateApplicationAsync(vendorId, "EdFiSandbox", [dataStoreId]); // 102
            await records.ImportClaimSetAsync(ClaimSet()); // 103

            _failures = await records.RemoveAllAsync();
        }

        [Test]
        public void It_still_attempts_every_remaining_deletion() =>
            _service
                .Deletions.Should()
                .Equal(
                    "DELETE v3/applications/102",
                    "DELETE v3/claimSets/103",
                    "DELETE v3/vendors/101",
                    "DELETE v3/dataStores/100"
                );

        [Test]
        public void It_reports_each_failure() =>
            _failures
                .Should()
                .Equal(
                    "deleting v3/applications/102 answered 500",
                    "deleting v3/claimSets/103 failed with HttpRequestException"
                );
    }

    [TestFixture]
    public class Given_a_record_already_removed
    {
        private FakeConfigurationService _service = null!;
        private IReadOnlyList<string> _failures = null!;

        [SetUp]
        public async Task Setup()
        {
            // NUnit runs SetUp before every test of the fixture: each starts with a fresh service.
            _service = new FakeConfigurationService();
            _service.Answer(HttpMethod.Delete, "v3/dataStores/100", HttpStatusCode.NotFound);
            ProjectionScenarioRecords records = new(_service.CallAsync);
            await records.CreateDataStoreAsync("Host=dms-postgresql"); // 100

            _failures = await records.RemoveAllAsync();
        }

        [Test]
        public void It_counts_it_as_removed() => _failures.Should().BeEmpty();
    }
}
