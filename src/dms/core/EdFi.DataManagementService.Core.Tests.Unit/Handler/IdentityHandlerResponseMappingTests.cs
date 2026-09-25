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
            IdentityHandler.DefaultMaxRequestLineSize,
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

    // ---------------------------------------------------------------- Create

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_Success_With_A_String_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(200);
        }

        [Test]
        public void It_returns_the_string_body()
        {
            _response.Body!.GetValue<string>().Should().Be("unique-id-1");
        }

        [Test]
        public void It_has_no_Location()
        {
            _response.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_Success_With_No_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_Success_With_A_Non_String_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_InvalidProperties_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult
                {
                    Status = IdentityResultStatus.InvalidProperties,
                    Errors =
                    [
                        new IdentityError { Path = "$.firstName", Message = "First name is required." },
                    ],
                },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_400()
        {
            _response.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_projects_the_errors_into_validationErrors()
        {
            _response.Body!["validationErrors"]!["$.firstName"]!
                .AsArray()
                .Select(n => n!.ToString())
                .Should()
                .Equal("First name is required.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_NotFound_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_404_identity_not_found()
        {
            AssertNotFound(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_Incomplete_Or_JobFailed_Result
    {
        [TestCase("Incomplete")]
        [TestCase("JobFailed")]
        public async Task It_is_a_502_provider_contract_violation(string statusName)
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = Enum.Parse<IdentityResultStatus>(statusName) },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.Create, provider);

            IFrontendResponse response = await Execute(requestInfo);

            AssertContractViolation(response);
        }
    }

    // ---------------------------------------------------------------- GetById

    [TestFixture]
    [Parallelizable]
    public class Given_A_GetById_Success_With_A_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            JsonObject payload = new() { ["uniqueId"] = "abc" };
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = payload },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(200);
        }

        [Test]
        public void It_returns_the_payload_body()
        {
            _response.Body!["uniqueId"]!.ToString().Should().Be("abc");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_GetById_Success_With_No_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_GetById_NotFound_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
            };
            var requestInfo = CreateRequestInfo(IdentityOperation.GetById, provider, routeValue: "abc");

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_404_identity_not_found()
        {
            AssertNotFound(_response);
        }
    }

    // ---------------------------------------------------------------- Find / Search synchronous and async

    [TestFixture]
    [Parallelizable]
    public class Given_A_Synchronous_Find_Or_Search_Success
    {
        [TestCase("Find")]
        [TestCase("Search")]
        public async Task It_returns_200_with_the_payload_body_and_no_Location(string operationName)
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Asynchronous_Find_Or_Search_Success_With_A_Usable_Token
    {
        [TestCase("Find")]
        [TestCase("Search")]
        public async Task It_returns_202_with_no_body_and_a_Location(string operationName)
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Asynchronous_Find_Success_With_An_Unusable_Token
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Find,
                provider,
                parsedBody: new JsonArray("a")
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }

        [Test]
        public void It_has_no_Location()
        {
            _response.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Success_With_Both_Payload_And_Token
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Find,
                provider,
                parsedBody: new JsonArray("a")
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Success_With_Neither_Payload_Nor_Token
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Find,
                provider,
                parsedBody: new JsonArray("a")
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Token_While_The_Results_Capability_Is_Absent
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Find,
                provider,
                parsedBody: new JsonArray("a")
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }

        [Test]
        public void It_has_no_Location()
        {
            _response.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Or_Search_InvalidProperties_Result
    {
        [TestCase("Find")]
        [TestCase("Search")]
        public async Task It_returns_400_with_the_errors_projected(string operationName)
        {
            var provider = new ScriptedIdentityService
            {
                NextAsyncResult = new IdentityAsyncResult
                {
                    Status = IdentityResultStatus.InvalidProperties,
                    Errors =
                    [
                        new IdentityError { Path = "$[0].firstName", Message = "First name is required." },
                    ],
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Or_Search_NotFound_Result
    {
        [TestCase("Find")]
        [TestCase("Search")]
        public async Task It_is_404_identity_not_found(string operationName)
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Or_Search_Incomplete_Or_JobFailed_Result
    {
        [TestCase("Find", "Incomplete")]
        [TestCase("Find", "JobFailed")]
        [TestCase("Search", "Incomplete")]
        [TestCase("Search", "JobFailed")]
        public async Task It_is_a_502_provider_contract_violation(string operationName, string statusName)
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
    }

    // ---------------------------------------------------------------- Results

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_Success_With_A_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            JsonObject payload = new() { ["status"] = "Complete" };
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = payload },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(200);
        }

        [Test]
        public void It_returns_the_payload_body()
        {
            _response.Body!["status"]!.ToString().Should().Be("Complete");
        }

        [Test]
        public void It_has_no_Location()
        {
            _response.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_Success_With_No_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Success, Payload = null },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_Incomplete_With_A_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            JsonObject payload = new() { ["status"] = "Incomplete" };
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult
                {
                    Status = IdentityResultStatus.Incomplete,
                    Payload = payload,
                },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_200()
        {
            _response.StatusCode.Should().Be(200);
        }

        [Test]
        public void It_returns_the_payload_body()
        {
            _response.Body!["status"]!.ToString().Should().Be("Incomplete");
        }

        [Test]
        public void It_has_a_Location_to_the_current_poll_path()
        {
            _response.LocationHeaderPath.Should().Be($"{PollPathPrefix}/job-token-1");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_Incomplete_With_No_Payload
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.Incomplete, Payload = null },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_a_502_provider_contract_violation()
        {
            AssertContractViolation(_response);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_JobFailed_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
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
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_502()
        {
            _response.StatusCode.Should().Be(502);
        }

        [Test]
        public void It_returns_the_job_failed_type()
        {
            _response.Body!["type"]!.ToString().Should().Be(IdentityFailureResponse.JobFailedType);
        }

        [Test]
        public void It_returns_the_fixed_title()
        {
            _response.Body!["title"]!.ToString().Should().Be("Identity job failed");
        }

        [Test]
        public void It_returns_the_fixed_detail()
        {
            _response.Body!["detail"]!
                .ToString()
                .Should()
                .Be("The accepted identity request failed permanently. Stop polling this job.");
        }

        [Test]
        public void It_has_no_Location()
        {
            _response.LocationHeaderPath.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_InvalidProperties_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult
                {
                    Status = IdentityResultStatus.InvalidProperties,
                    Errors = [new IdentityError { Message = "job input was invalid" }],
                },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_returns_400()
        {
            _response.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_projects_the_errors()
        {
            _response.Body!["errors"]!
                .AsArray()
                .Select(n => n!.ToString())
                .Should()
                .Equal("job input was invalid");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_NotFound_Result
    {
        private IFrontendResponse _response = null!;

        [SetUp]
        public async Task Setup()
        {
            var provider = new ScriptedIdentityService
            {
                NextResult = new IdentityResult { Status = IdentityResultStatus.NotFound },
            };
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: "job-token-1"
            );

            _response = await Execute(requestInfo);
        }

        [Test]
        public void It_is_404_identity_not_found()
        {
            AssertNotFound(_response);
        }
    }

    /// <summary>
    /// Pins the null-provider-result path for every one of the five identity operations: a provider
    /// that breaks the non-nullable <see cref="IIdentityService" /> contract and returns null from an
    /// operation is distinguished from a boundary failure (design.md D9) and mapped to the existing
    /// provider-contract-violation 502 - never the default bodyless 503 that a missed null check would
    /// leave in place.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_a_provider_operation_that_returns_null_from_a_non_throwing_call
    {
        [TestCase("Create")]
        [TestCase("GetById")]
        [TestCase("Find")]
        [TestCase("Search")]
        [TestCase("Results")]
        public async Task It_is_502_provider_contract_violation_with_no_result_detail_and_no_Location(
            string operationName
        )
        {
            IdentityOperation operation = Enum.Parse<IdentityOperation>(operationName);
            var provider = new ScriptedIdentityService { NextResult = null, NextAsyncResult = null };
            RequestInfo requestInfo = operation switch
            {
                IdentityOperation.Create => CreateRequestInfo(operation, provider),
                IdentityOperation.GetById => CreateRequestInfo(operation, provider, routeValue: "abc"),
                IdentityOperation.Find => CreateRequestInfo(
                    operation,
                    provider,
                    parsedBody: new JsonArray("a")
                ),
                IdentityOperation.Search => CreateRequestInfo(
                    operation,
                    provider,
                    parsedBody: new JsonArray(new JsonObject())
                ),
                IdentityOperation.Results => CreateRequestInfo(
                    operation,
                    provider,
                    routeValue: "job-token-1"
                ),
                _ => throw new InvalidOperationException($"Unsupported operation '{operation}'."),
            };

            IFrontendResponse response = await Execute(requestInfo);

            AssertContractViolation(response);
            response.Body!["detail"]!.ToString().Should().Be("The identity provider returned no result.");
            response.LocationHeaderPath.Should().BeNull();
        }
    }
}
