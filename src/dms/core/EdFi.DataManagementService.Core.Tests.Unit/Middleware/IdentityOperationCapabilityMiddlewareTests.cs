// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Pins <see cref="IdentityOperationCapabilityMiddleware" /> (design.md "Pipeline", D9, story B1):
/// with the host default <see cref="NoIdentityService" />, every operation is answered
/// operation-unsupported 404 regardless of body shape, because the gate runs before any content-type
/// or body validation; the provider is resolved once and its <c>Capabilities</c> getter read once per
/// request; and the requested operation is gated against exactly the matching capability flag.
/// </summary>
[TestFixture]
public class IdentityOperationCapabilityMiddlewareTests
{
    private static IdentityOperation ParseOperation(string operationName) =>
        Enum.Parse<IdentityOperation>(operationName);

    private static IdentityOperationCapabilityMiddleware CreateMiddleware() =>
        new(
            new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance),
            NullLogger<IdentityOperationCapabilityMiddleware>.Instance
        );

    /// <summary>
    /// A mutable read-after count, because the DI factory that increments it does not run until
    /// <see cref="IServiceProvider.GetService" /> is actually called during pipeline execution - well
    /// after <see cref="CreateRequestInfo" /> returns.
    /// </summary>
    private sealed class ActivationCounter
    {
        public int Count { get; private set; }

        public void Increment() => Count++;
    }

    private static RequestInfo CreateRequestInfo(
        IIdentityService provider,
        string operationName,
        ActivationCounter? activationCounter = null,
        string? bodyParseErrorMessage = null,
        string? duplicatePropertyPath = null
    )
    {
        IServiceProvider services = new ServiceCollection()
            .AddScoped(_ =>
            {
                activationCounter?.Increment();
                return provider;
            })
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider;

        var requestInfo = No.RequestInfo("trace-id", services);
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with
        {
            BodyParseErrorMessage = bodyParseErrorMessage,
            DuplicatePropertyPath = duplicatePropertyPath,
        };
        requestInfo.IdentityOperation = ParseOperation(operationName);
        return requestInfo;
    }

    /// <summary>
    /// A stub <see cref="IIdentityService" /> that counts how many times its <c>Capabilities</c>
    /// getter is read, so tests can assert the gate reads it exactly once.
    /// </summary>
    private sealed class CountingIdentityService(IdentityCapabilities capabilities) : IIdentityService
    {
        public int CapabilitiesReadCount { get; private set; }

        public IdentityCapabilities Capabilities
        {
            get
            {
                CapabilitiesReadCount++;
                return capabilities;
            }
        }

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> GetByIdAsync(
            string uniqueId,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> FindAsync(
            IReadOnlyList<string> uniqueIds,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityAsyncResult> SearchAsync(
            IReadOnlyList<JsonObject> requests,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IdentityResult> ResultsAsync(
            string requestToken,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    [TestFixture]
    public class Given_the_host_default_NoIdentityService : IdentityOperationCapabilityMiddlewareTests
    {
        [TestCase("Create")]
        [TestCase("GetById")]
        [TestCase("Find")]
        [TestCase("Search")]
        [TestCase("Results")]
        public async Task Every_operation_returns_operation_unsupported_404(string operationName)
        {
            var requestInfo = CreateRequestInfo(new NoIdentityService(), operationName);
            var middleware = CreateMiddleware();
            bool nextCalled = false;

            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            JsonNode body = requestInfo.FrontendResponse.Body!;
            body["type"]!.ToString().Should().Be(IdentityFailureResponse.OperationNotSupportedType);
        }

        [Test]
        public async Task A_malformed_parsed_body_does_not_change_the_operation_unsupported_outcome()
        {
            var requestInfo = CreateRequestInfo(
                new NoIdentityService(),
                "Create",
                bodyParseErrorMessage: "'{' is an invalid start of a value."
            );
            var middleware = CreateMiddleware();

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be(IdentityFailureResponse.OperationNotSupportedType);
        }

        [Test]
        public async Task A_duplicate_property_path_does_not_change_the_operation_unsupported_outcome()
        {
            var requestInfo = CreateRequestInfo(
                new NoIdentityService(),
                "Create",
                duplicatePropertyPath: "$.firstName"
            );
            var middleware = CreateMiddleware();

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be(IdentityFailureResponse.OperationNotSupportedType);
        }
    }

    [TestFixture]
    public class Given_a_provider_resolved_from_the_request_scope : IdentityOperationCapabilityMiddlewareTests
    {
        [Test]
        public async Task The_provider_is_activated_exactly_once_per_request()
        {
            var provider = new CountingIdentityService(IdentityCapabilities.Create);
            var activationCounter = new ActivationCounter();
            var requestInfo = CreateRequestInfo(provider, "Create", activationCounter);
            var middleware = CreateMiddleware();

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            activationCounter.Count.Should().Be(1);
        }

        [Test]
        public async Task The_Capabilities_getter_is_read_exactly_once_and_the_captured_value_serves_the_gate()
        {
            const IdentityCapabilities expectedFlags =
                IdentityCapabilities.Create | IdentityCapabilities.Results;
            var provider = new CountingIdentityService(expectedFlags);
            var requestInfo = CreateRequestInfo(provider, "Create");
            var middleware = CreateMiddleware();

            await middleware.Execute(requestInfo, TestHelper.NullNext);

            provider.CapabilitiesReadCount.Should().Be(1);
            // The captured value on RequestInfo is what a later results-token invariant check (in
            // IdentityHandler) reads instead of the provider's getter again, so it must already carry
            // exactly the flags the single read observed.
            requestInfo.IdentityCapabilities.Should().Be(expectedFlags);
        }

        [Test]
        public void Verify_the_verifier_a_getter_read_twice_is_visibly_different_from_once()
        {
            // Negative control for the counting fake itself (Disciplines: "verify the verifier"): if a
            // caller reads the getter twice, the counter must show 2, not 1, so the assertions above
            // are trustworthy.
            var provider = new CountingIdentityService(IdentityCapabilities.Create);

            _ = provider.Capabilities;
            _ = provider.Capabilities;

            provider.CapabilitiesReadCount.Should().Be(2);
        }

        [TestCase("Create", IdentityCapabilities.Create)]
        [TestCase("GetById", IdentityCapabilities.GetById)]
        [TestCase("Find", IdentityCapabilities.Find)]
        [TestCase("Search", IdentityCapabilities.Search)]
        [TestCase("Results", IdentityCapabilities.Results)]
        public async Task Operation_proceeds_when_the_matching_capability_flag_is_present(
            string operationName,
            IdentityCapabilities matchingFlag
        )
        {
            var provider = new CountingIdentityService(matchingFlag);
            var requestInfo = CreateRequestInfo(provider, operationName);
            var middleware = CreateMiddleware();
            bool nextCalled = false;

            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            nextCalled.Should().BeTrue();
            requestInfo.IdentityProvider.Should().BeSameAs(provider);
            requestInfo.IdentityCapabilities.Should().Be(matchingFlag);
        }

        [TestCase("Create", IdentityCapabilities.GetById)]
        [TestCase("GetById", IdentityCapabilities.Find)]
        [TestCase("Find", IdentityCapabilities.Search)]
        [TestCase("Search", IdentityCapabilities.Results)]
        [TestCase("Results", IdentityCapabilities.Create)]
        public async Task Operation_returns_404_when_a_different_capability_flag_is_present(
            string operationName,
            IdentityCapabilities otherFlag
        )
        {
            var provider = new CountingIdentityService(otherFlag);
            var requestInfo = CreateRequestInfo(provider, operationName);
            var middleware = CreateMiddleware();
            bool nextCalled = false;

            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            nextCalled.Should().BeFalse();
            requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            requestInfo.FrontendResponse.Body!["type"]!
                .ToString()
                .Should()
                .Be(IdentityFailureResponse.OperationNotSupportedType);
        }
    }
}
