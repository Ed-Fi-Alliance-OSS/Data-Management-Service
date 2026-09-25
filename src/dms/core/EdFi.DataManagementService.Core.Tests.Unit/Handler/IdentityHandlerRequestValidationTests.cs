// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// Pins <see cref="IdentityHandler" />'s inbound protocol checks (design.md "Pipeline", "The four
/// inbound protocol checks", D9, story B3): the four checks the JSON-body identity pipeline performs
/// before <see cref="IdentityHandler" /> runs (media type, well-formed JSON, no duplicate property,
/// expected top-level shape), plus the two the handler performs itself (top-level shape and a
/// present-but-blank route value). No provider method is ever called for a rejected request.
/// The JSON-body cases are driven through the real tail of the JSON-body pipeline -
/// <see cref="ValidateContentTypeMiddleware" />, <see cref="ParseBodyMiddleware" />,
/// <see cref="DuplicatePropertiesMiddleware" />, then <see cref="IdentityHandler" /> - so this fixture
/// proves the composed contract, not only the handler's own slice of it.
/// </summary>
public class IdentityHandlerRequestValidationTests
{
    /// <summary>
    /// Counts every operation call, so a rejected request that reaches the provider anyway is caught
    /// even though its returned outcome would otherwise look unremarkable.
    /// </summary>
    private sealed class CountingIdentityService : IIdentityService
    {
        public int InvocationCount { get; private set; }

        public IdentityCapabilities Capabilities =>
            IdentityCapabilities.Create
            | IdentityCapabilities.Find
            | IdentityCapabilities.Search
            | IdentityCapabilities.GetById
            | IdentityCapabilities.Results;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            InvocationCount++;
            return Task.FromResult(
                new IdentityResult { Status = IdentityResultStatus.Success, Payload = "unique-id" }
            );
        }

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            InvocationCount++;
            return Task.FromResult(
                new IdentityResult { Status = IdentityResultStatus.Success, Payload = new JsonObject() }
            );
        }

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            InvocationCount++;
            return Task.FromResult(
                new IdentityAsyncResult { Status = IdentityResultStatus.Success, Payload = new JsonObject() }
            );
        }

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            InvocationCount++;
            return Task.FromResult(
                new IdentityAsyncResult { Status = IdentityResultStatus.Success, Payload = new JsonObject() }
            );
        }

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            InvocationCount++;
            return Task.FromResult(
                new IdentityResult { Status = IdentityResultStatus.Success, Payload = new JsonObject() }
            );
        }
    }

    private static IdentityHandler CreateHandler() =>
        new(
            new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance),
            IdentityHandler.DefaultMaxRequestLineSize,
            NullLogger<IdentityHandler>.Instance
        );

    private static RequestInfo CreateRequestInfo(
        IdentityOperation operation,
        CountingIdentityService provider,
        string? body = null,
        string? contentType = null,
        string? routeValue = null
    )
    {
        var requestInfo = No.RequestInfo("validation-trace", No.ServiceProvider);
        requestInfo.IdentityOperation = operation;
        requestInfo.IdentityProvider = provider;
        requestInfo.IdentityCapabilities = provider.Capabilities;
        requestInfo.IdentityRouteValue = routeValue;
        requestInfo.ClientAuthorizations = No.ClientAuthorizations with { ClientId = "client-1" };

        Dictionary<string, string> headers = [];
        if (contentType is not null)
        {
            headers["Content-Type"] = contentType;
        }

        requestInfo.FrontendRequest = requestInfo.FrontendRequest with
        {
            Body = body,
            Headers = headers,
            ParsedBody = null,
        };
        return requestInfo;
    }

    /// <summary>
    /// Runs the real tail of the JSON-body identity pipeline: content-type validation (baseline JSON
    /// only), body parsing, duplicate-property detection, then the handler.
    /// </summary>
    private static async Task RunJsonBodyChain(RequestInfo requestInfo)
    {
        var contentType = new ValidateContentTypeMiddleware(
            NullLogger<ValidateContentTypeMiddleware>.Instance,
            ContentTypePolicy.BaselineJsonOnly
        );
        var parseBody = new ParseBodyMiddleware(NullLogger<ParseBodyMiddleware>.Instance);
        var duplicateProperties = new DuplicatePropertiesMiddleware(
            NullLogger<DuplicatePropertiesMiddleware>.Instance
        );
        var handler = CreateHandler();

        await contentType.Execute(
            requestInfo,
            async () =>
                await parseBody.Execute(
                    requestInfo,
                    async () =>
                        await duplicateProperties.Execute(
                            requestInfo,
                            async () => await handler.Execute(requestInfo, TestHelper.NullNext)
                        )
                )
        );
    }

    // ---------------------------------------------------------------- media type (415)

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unsupported_Media_Type
    {
        [TestCase("application/xml")]
        [TestCase("application/vnd.ed-fi.student.v1+json")]
        public async Task It_is_rejected_with_415_and_the_provider_is_not_called(string contentType)
        {
            var provider = new CountingIdentityService();
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Create,
                provider,
                body: "{}",
                contentType: contentType
            );

            await RunJsonBodyChain(requestInfo);

            requestInfo.FrontendResponse.StatusCode.Should().Be(415);
            provider.InvocationCount.Should().Be(0);
        }
    }

    // ---------------------------------------------------------------- malformed / empty body (400)

    [TestFixture]
    [Parallelizable]
    public class Given_Malformed_JSON
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Create, _provider, body: "{ not valid json");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Empty_Body
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Create, _provider, body: "");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    // ---------------------------------------------------------------- duplicate property (400, data-validation)

    [TestFixture]
    [Parallelizable]
    public class Given_A_Duplicate_Top_Level_Property
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(
                IdentityOperation.Create,
                _provider,
                body: """{"firstName":"Jane","firstName":"Jane"}"""
            );

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_includes_validation_errors_in_the_body()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"].Should().NotBeNull();
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    // ---------------------------------------------------------------- top-level shape (400)

    [TestFixture]
    [Parallelizable]
    public class Given_A_Create_Request_With_A_Non_Object_Top_Level_Body
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Create, _provider, body: "[]");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Request_With_A_Non_Array_Top_Level_Body
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Find, _provider, body: "{}");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Request_With_An_Array_Containing_A_Non_String_Entry
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Find, _provider, body: """["a", 123]""");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Find_Request_With_An_Array_Containing_A_Null_Entry
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Find, _provider, body: """["a", null]""");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Search_Request_With_A_Non_Array_Top_Level_Body
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(IdentityOperation.Search, _provider, body: "{}");

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Search_Request_With_An_Array_Containing_A_Non_Object_Entry
    {
        private RequestInfo _requestInfo = null!;
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            _requestInfo = CreateRequestInfo(
                IdentityOperation.Search,
                _provider,
                body: """[{"firstName":"a"}, "not-an-object"]"""
            );

            await RunJsonBodyChain(_requestInfo);
        }

        [Test]
        public void It_is_rejected_with_400()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_does_not_call_the_provider()
        {
            _provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Well_Formed_Matching_Body
    {
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            var requestInfo = CreateRequestInfo(IdentityOperation.Create, _provider, body: "{}");

            await RunJsonBodyChain(requestInfo);
        }

        [Test]
        public void It_calls_the_provider_exactly_once()
        {
            _provider.InvocationCount.Should().Be(1);
        }
    }

    // ---------------------------------------------------------------- blank-but-present route value (400)

    [TestFixture]
    [Parallelizable]
    public class Given_A_GetById_Request_With_A_Blank_Route_Value
    {
        [TestCase("")]
        [TestCase("   ")]
        public async Task It_is_rejected_with_400_and_the_provider_is_not_called(string blankRouteValue)
        {
            var provider = new CountingIdentityService();
            var requestInfo = CreateRequestInfo(
                IdentityOperation.GetById,
                provider,
                routeValue: blankRouteValue
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Results_Request_With_A_Blank_Route_Value
    {
        [TestCase("")]
        [TestCase("   ")]
        public async Task It_is_rejected_with_400_and_the_provider_is_not_called(string blankRouteValue)
        {
            var provider = new CountingIdentityService();
            var requestInfo = CreateRequestInfo(
                IdentityOperation.Results,
                provider,
                routeValue: blankRouteValue
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(400);
            provider.InvocationCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_GetById_Request_With_A_Non_Blank_Route_Value
    {
        private CountingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            _provider = new CountingIdentityService();
            var requestInfo = CreateRequestInfo(
                IdentityOperation.GetById,
                _provider,
                routeValue: "unique-id-1"
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_calls_the_provider_exactly_once()
        {
            _provider.InvocationCount.Should().Be(1);
        }
    }
}
