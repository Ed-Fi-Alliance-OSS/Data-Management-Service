// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// Pins every row of the result-mapping table (design.md:825-871, story B4): status code, problem
/// <c>type</c>, body presence, and <c>Location</c> for each combination of
/// <see cref="Identity.IdentityOperation" /> and <see cref="IdentityResultStatus" /> - including every
/// provider-contract-violation row a synchronous find/search or a misused results status can produce.
/// </summary>
[TestFixture]
public class IdentityHandlerResponseMappingTests
{
    private const string PollPathPrefix = "/identity/v2/identities/results";

    /// <summary>
    /// A stub provider whose next call returns a fixed, pre-configured result, so each test drives one
    /// row of the mapping table without a real backing store.
    /// </summary>
    private sealed class ScriptedIdentityService : IIdentityService
    {
        public IdentityResult? NextResult { get; set; }
        public IdentityAsyncResult? NextAsyncResult { get; set; }

        public IdentityCapabilities Capabilities { get; set; } =
            IdentityCapabilities.Create
            | IdentityCapabilities.GetById
            | IdentityCapabilities.Find
            | IdentityCapabilities.Search
            | IdentityCapabilities.Results;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextResult!);

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextResult!);

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextAsyncResult!);

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextAsyncResult!);

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => Task.FromResult(NextResult!);
    }

    private static IdentityHandler CreateHandler() =>
        new(
            new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance),
            8192,
            NullLogger<IdentityHandler>.Instance
        );

    private static RequestInfo CreateRequestInfo(
        IdentityOperation operation,
        ScriptedIdentityService provider,
        JsonNode? parsedBody = null,
        string? routeValue = null
    )
    {
        var requestInfo = No.RequestInfo("mapping-trace", No.ServiceProvider);
        requestInfo.IdentityOperation = operation;
        requestInfo.IdentityProvider = provider;
        requestInfo.IdentityCapabilities = provider.Capabilities;
        requestInfo.IdentityRouteValue = routeValue;
        requestInfo.IdentityPollPathPrefix = PollPathPrefix;
        requestInfo.ParsedBody = parsedBody ?? new JsonObject();
        requestInfo.ClientAuthorizations = No.ClientAuthorizations with { ClientId = "client-1" };
        return requestInfo;
    }

    private static async Task<IFrontendResponse> Execute(RequestInfo requestInfo)
    {
        await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        return requestInfo.FrontendResponse;
    }

    // ---------------------------------------------------------------- Create

    [Test]
    public async Task Create_Success_with_a_string_payload_is_200_with_the_string_body()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.Success,
                Payload = "unique-id-1",
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(200);
        response.Body!.GetValue<string>().Should().Be("unique-id-1");
        response.LocationHeaderPath.Should().BeNull();
    }

    [Test]
    public async Task Create_Success_with_no_payload_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task Create_Success_with_a_non_string_payload_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.Success,
                Payload = new JsonObject(),
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task Create_InvalidProperties_projects_errors_into_a_400()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.InvalidProperties,
                Errors = [new IdentityError { Path = "$.firstName", Message = "First name is required." }],
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(400);
        response.Body!["validationErrors"]!["$.firstName"]!
            .AsArray()
            .Select(n => n!.ToString())
            .Should()
            .Equal("First name is required.");
    }

    [Test]
    public async Task Create_NotFound_is_404_identity_not_found()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        AssertNotFound(response);
    }

    [TestCase("Incomplete")]
    [TestCase("JobFailed")]
    public async Task Create_Incomplete_or_JobFailed_is_502_provider_contract_violation(string statusName)
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = Enum.Parse<IdentityResultStatus>(statusName) },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    // ---------------------------------------------------------------- GetById

    [Test]
    public async Task GetById_Success_with_a_payload_is_200()
    {
        JsonObject payload = new() { ["uniqueId"] = "abc" };
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = payload },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(200);
        response.Body!["uniqueId"]!.ToString().Should().Be("abc");
    }

    [Test]
    public async Task GetById_Success_with_no_payload_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task GetById_NotFound_is_404_identity_not_found()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

        IFrontendResponse response = await Execute(requestInfo);

        AssertNotFound(response);
    }

    // ---------------------------------------------------------------- Find / Search synchronous and async

    [TestCase("Find")]
    [TestCase("Search")]
    public async Task Synchronous_Success_with_a_payload_and_no_token_is_200(string operationName)
    {
        JsonObject payload = new() { ["status"] = "Complete" };
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = payload,
                RequestToken = null,
            },
        };
        var requestInfo = CreateRequestInfo(
            Enum.Parse<IdentityOperation>(operationName),
            provider,
            parsedBody: operationName == "Find" ? new JsonArray("a") : new JsonArray(new JsonObject())
        );

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(200);
        response.Body!["status"]!.ToString().Should().Be("Complete");
        response.LocationHeaderPath.Should().BeNull();
    }

    [TestCase("Find")]
    [TestCase("Search")]
    public async Task Asynchronous_Success_with_a_usable_token_and_no_payload_is_202_with_Location(
        string operationName
    )
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = null,
                RequestToken = "job-token-1",
            },
        };
        var requestInfo = CreateRequestInfo(
            Enum.Parse<IdentityOperation>(operationName),
            provider,
            parsedBody: operationName == "Find" ? new JsonArray("a") : new JsonArray(new JsonObject())
        );

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(202);
        response.Body.Should().BeNull();
        response.LocationHeaderPath.Should().Be($"{PollPathPrefix}/job-token-1");
    }

    [Test]
    public async Task Asynchronous_Success_with_an_unusable_token_is_502_with_no_Location()
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = null,
                RequestToken = "bad/token",
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Find, provider, parsedBody: new JsonArray("a"));

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
        response.LocationHeaderPath.Should().BeNull();
    }

    [Test]
    public async Task Success_with_both_payload_and_token_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = new JsonObject(),
                RequestToken = "job-token-1",
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Find, provider, parsedBody: new JsonArray("a"));

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task Success_with_neither_payload_nor_token_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = null,
                RequestToken = null,
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Find, provider, parsedBody: new JsonArray("a"));

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task A_token_while_the_Results_capability_is_absent_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            Capabilities = IdentityCapabilities.Find,
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.Success,
                Payload = null,
                RequestToken = "job-token-1",
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Find, provider, parsedBody: new JsonArray("a"));

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
        response.LocationHeaderPath.Should().BeNull();
    }

    [TestCase("Find")]
    [TestCase("Search")]
    public async Task Find_or_Search_InvalidProperties_projects_errors_into_a_400(string operationName)
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = IdentityResultStatus.InvalidProperties,
                Errors = [new IdentityError { Path = "$[0].firstName", Message = "First name is required." }],
            },
        };
        var requestInfo = CreateRequestInfo(
            Enum.Parse<IdentityOperation>(operationName),
            provider,
            parsedBody: operationName == "Find" ? new JsonArray("a") : new JsonArray(new JsonObject())
        );

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(400);
        response.Body!["validationErrors"]!.AsObject().Should().ContainKey("$[0].firstName");
    }

    [TestCase("Find")]
    [TestCase("Search")]
    public async Task Find_or_Search_NotFound_is_404_identity_not_found(string operationName)
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult { Status = IdentityResultStatus.NotFound },
        };
        var requestInfo = CreateRequestInfo(
            Enum.Parse<IdentityOperation>(operationName),
            provider,
            parsedBody: operationName == "Find" ? new JsonArray("a") : new JsonArray(new JsonObject())
        );

        IFrontendResponse response = await Execute(requestInfo);

        AssertNotFound(response);
    }

    [TestCase("Find", "Incomplete")]
    [TestCase("Find", "JobFailed")]
    [TestCase("Search", "Incomplete")]
    [TestCase("Search", "JobFailed")]
    public async Task Find_or_Search_Incomplete_or_JobFailed_is_502_provider_contract_violation(
        string operationName,
        string statusName
    )
    {
        var provider = new ScriptedIdentityService
        {
            NextAsyncResult = new IdentityAsyncResult
            {
                Status = Enum.Parse<IdentityResultStatus>(statusName),
            },
        };
        var requestInfo = CreateRequestInfo(
            Enum.Parse<IdentityOperation>(operationName),
            provider,
            parsedBody: operationName == "Find" ? new JsonArray("a") : new JsonArray(new JsonObject())
        );

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    // ---------------------------------------------------------------- Results

    [Test]
    public async Task Results_Success_with_a_payload_is_200()
    {
        JsonObject payload = new() { ["status"] = "Complete" };
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = payload },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(200);
        response.Body!["status"]!.ToString().Should().Be("Complete");
        response.LocationHeaderPath.Should().BeNull();
    }

    [Test]
    public async Task Results_Success_with_no_payload_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task Results_Incomplete_with_a_payload_is_200_with_Location_to_the_current_poll_path()
    {
        JsonObject payload = new() { ["status"] = "Incomplete" };
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Incomplete, Payload = payload },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(200);
        response.Body!["status"]!.ToString().Should().Be("Incomplete");
        response.LocationHeaderPath.Should().Be($"{PollPathPrefix}/job-token-1");
    }

    [Test]
    public async Task Results_Incomplete_with_no_payload_is_502_provider_contract_violation()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.Incomplete, Payload = null },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        AssertContractViolation(response);
    }

    [Test]
    public async Task Results_JobFailed_is_502_job_failed_with_the_fixed_title_and_detail_and_no_Location()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.JobFailed,
                Payload = new JsonObject { ["ignored"] = true },
                Errors = [new IdentityError { Message = "ignored" }],
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(502);
        response.Body!["type"]!.ToString().Should().Be(IdentityFailureResponse.JobFailedType);
        response.Body!["title"]!.ToString().Should().Be("Identity job failed");
        response.Body!["detail"]!
            .ToString()
            .Should()
            .Be("The accepted identity request failed permanently. Stop polling this job.");
        response.LocationHeaderPath.Should().BeNull();
    }

    [Test]
    public async Task Results_InvalidProperties_projects_errors_into_a_400()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult
            {
                Status = IdentityResultStatus.InvalidProperties,
                Errors = [new IdentityError { Message = "job input was invalid" }],
            },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        response.StatusCode.Should().Be(400);
        response.Body!["errors"]!
            .AsArray()
            .Select(n => n!.ToString())
            .Should()
            .Equal("job input was invalid");
    }

    [Test]
    public async Task Results_NotFound_is_404_identity_not_found()
    {
        var provider = new ScriptedIdentityService
        {
            NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
        };
        var requestInfo = CreateRequestInfo(IdentityOperation.Results, provider, routeValue: "job-token-1");

        IFrontendResponse response = await Execute(requestInfo);

        AssertNotFound(response);
    }

    // ---------------------------------------------------------------- shared assertions

    private static void AssertContractViolation(IFrontendResponse response)
    {
        response.StatusCode.Should().Be(502);
        response.ContentType.Should().Be("application/problem+json");
        response.Body!["type"]!.ToString().Should().Be(IdentityFailureResponse.ProviderContractViolationType);
    }

    private static void AssertNotFound(IFrontendResponse response)
    {
        response.StatusCode.Should().Be(404);
        response.ContentType.Should().Be("application/problem+json");
        response.Body!["type"]!.ToString().Should().Be(IdentityFailureResponse.NotFoundType);
    }
}
