// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// A provider that answers <c>Incomplete</c> or <c>JobFailed</c> from
/// create, get-by-id, find or search has misused the contract, so each answers the
/// provider-contract-violation <c>502</c> with no <c>Location</c>; the same two statuses from a results
/// poll are legitimate (an incomplete <c>200</c> and the terminal job-failed <c>502</c>), and a failed
/// job that also supplied a payload and errors exposes neither.
/// </summary>
/// <remarks>
/// The negative controls are the results route itself, where <c>Incomplete</c> is a normal <c>200</c>
/// and <c>JobFailed</c> is a different problem type, so the host maps by operation and not by status
/// alone. The fixture's payload and error texts are searched for in the response body, and a projected
/// <c>InvalidProperties</c> error is searched for the same way to show the check can see such text.
/// </remarks>
[Category("PluginIntegration")]
public sealed class Given_TheProviderReturnsAStatusTheOperationForbids
{
    private IdentityHttpRun? _run;
    private readonly Dictionary<string, IdentityHttpOutcome> _outcomes = [];
    private IdentityHttpOutcome? _resultsIncomplete;
    private IdentityHttpOutcome? _resultsJobFailed;
    private IdentityHttpOutcome? _resultsJobFailedWithPayload;
    private IdentityHttpOutcome? _projectedInvalid;

    private static string Key(string operation, string variant) => $"{operation}:{variant}";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _run = await IdentityHttpRun.StartAsync();

        foreach (string variant in new[] { "incomplete", "jobfailed", "jobfailed-payload" })
        {
            _outcomes[Key("create", variant)] = await _run.PostAsync(
                "identities",
                $$"""{ "LastSurname": "Rivera", "~FixtureReturn": "{{variant}}" }"""
            );
            _outcomes[Key("getById", variant)] = await _run.GetAsync($"identities/~fixture-return-{variant}");
            _outcomes[Key("find", variant)] = await _run.PostAsync(
                "identities/find",
                $$"""["~fixture:return:{{variant}}"]"""
            );
            _outcomes[Key("search", variant)] = await _run.PostAsync(
                "identities/search",
                $$"""[{ "~FixtureReturn": "{{variant}}" }]"""
            );
        }

        _resultsIncomplete = await PollResultsVariantAsync("incomplete");
        _resultsJobFailed = await PollResultsVariantAsync("jobfailed");
        _resultsJobFailedWithPayload = await PollResultsVariantAsync("jobfailed-payload");
        _projectedInvalid = await _run.PostAsync(
            "identities",
            """{ "~FixtureReturn": "invalid-createFieldError" }"""
        );
    }

    private async Task<IdentityHttpOutcome> PollResultsVariantAsync(string variant)
    {
        IdentityHttpOutcome accepted = await _run!.PostAsync(
            "identities/find",
            $$"""["~fixture:results:{{variant}}"]"""
        );
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        return await _run.GetAsync(accepted.Location!);
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_run is not null)
        {
            await _run.DisposeAsync();
            _run = null;
        }
    }

    [TestCase("create", "incomplete")]
    [TestCase("getById", "incomplete")]
    [TestCase("find", "incomplete")]
    [TestCase("search", "incomplete")]
    [TestCase("create", "jobfailed")]
    [TestCase("getById", "jobfailed")]
    [TestCase("find", "jobfailed")]
    [TestCase("search", "jobfailed")]
    [TestCase("create", "jobfailed-payload")]
    [TestCase("getById", "jobfailed-payload")]
    [TestCase("find", "jobfailed-payload")]
    [TestCase("search", "jobfailed-payload")]
    public void It_answers_502_for_a_status_the_operation_may_not_return(string operation, string variant)
    {
        _outcomes[Key(operation, variant)].Status.Should().Be(HttpStatusCode.BadGateway);
    }

    [TestCase("create", "incomplete")]
    [TestCase("getById", "incomplete")]
    [TestCase("find", "incomplete")]
    [TestCase("search", "incomplete")]
    [TestCase("create", "jobfailed")]
    [TestCase("getById", "jobfailed")]
    [TestCase("find", "jobfailed")]
    [TestCase("search", "jobfailed")]
    [TestCase("create", "jobfailed-payload")]
    [TestCase("getById", "jobfailed-payload")]
    [TestCase("find", "jobfailed-payload")]
    [TestCase("search", "jobfailed-payload")]
    public void It_names_the_problem_provider_contract_violation(string operation, string variant)
    {
        _outcomes[Key(operation, variant)]
            .ProblemType.Should()
            .Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    [TestCase("create", "incomplete")]
    [TestCase("getById", "incomplete")]
    [TestCase("find", "incomplete")]
    [TestCase("search", "incomplete")]
    [TestCase("create", "jobfailed")]
    [TestCase("getById", "jobfailed")]
    [TestCase("find", "jobfailed")]
    [TestCase("search", "jobfailed")]
    [TestCase("create", "jobfailed-payload")]
    [TestCase("getById", "jobfailed-payload")]
    [TestCase("find", "jobfailed-payload")]
    [TestCase("search", "jobfailed-payload")]
    public void It_returns_no_location_for_a_forbidden_status(string operation, string variant)
    {
        _outcomes[Key(operation, variant)].Location.Should().BeNull();
    }

    [TestCase("create", "jobfailed-payload")]
    [TestCase("getById", "jobfailed-payload")]
    [TestCase("find", "jobfailed-payload")]
    [TestCase("search", "jobfailed-payload")]
    public void It_exposes_neither_the_payload_nor_the_errors_of_a_job_failed_misuse(
        string operation,
        string variant
    )
    {
        string text = _outcomes[Key(operation, variant)].Text;
        text.Should().NotContain("The job failed.");
        text.Should().NotContain("$.FirstName");
        text.Should().NotContain("SearchResponses");
    }

    [Test]
    public void It_answers_an_incomplete_poll_with_200_not_a_violation()
    {
        _resultsIncomplete!.Status.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_carries_a_location_on_the_incomplete_poll()
    {
        _resultsIncomplete!.Location.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_answers_a_job_failed_poll_with_502()
    {
        _resultsJobFailed!.Status.Should().Be(HttpStatusCode.BadGateway);
    }

    [Test]
    public void It_names_a_job_failed_poll_job_failed_not_a_contract_violation()
    {
        _resultsJobFailed!.ProblemType.Should().Be(IdentityFailureResponse.JobFailedType);
    }

    [Test]
    public void It_returns_no_location_with_a_job_failed_poll()
    {
        _resultsJobFailed!.Location.Should().BeNull();
    }

    [Test]
    public void It_names_a_job_failed_poll_with_a_payload_job_failed()
    {
        _resultsJobFailedWithPayload!.Status.Should().Be(HttpStatusCode.BadGateway);
        _resultsJobFailedWithPayload.ProblemType.Should().Be(IdentityFailureResponse.JobFailedType);
    }

    [Test]
    public void It_exposes_neither_the_payload_nor_the_errors_of_a_failed_job_poll()
    {
        string text = _resultsJobFailedWithPayload!.Text;
        text.Should().NotContain("The job failed.");
        text.Should().NotContain("$.FirstName");
        text.Should().NotContain("SearchResponses");
    }

    [Test]
    public void It_would_see_a_provider_error_text_if_the_host_projected_one()
    {
        _projectedInvalid!.Text.Should().Contain("$.FirstName");
    }
}
