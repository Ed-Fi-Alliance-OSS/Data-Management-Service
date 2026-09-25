// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins how <see cref="IdentityHandler" /> builds <see cref="IdentityRequestContext" /> (design.md
/// "Route-Qualifier Context", D15, story B9): tenant and qualifier spelling is preserved exactly as
/// the frontend sent it, <see cref="IdentityRequestContext.RouteQualifiers" /> keys compare
/// case-insensitively, a qualifier-name collision under that comparer fails loudly instead of
/// silently overwriting, and <see cref="IdentityRequestContext.ClientId" /> is passed through with its
/// original case.
/// </summary>
public class IdentityRequestContextTests
{
    /// <summary>
    /// A provider whose CreateAsync captures the <see cref="IdentityRequestContext" /> it was invoked
    /// with, so the test can inspect exactly what IdentityHandler built.
    /// </summary>
    private sealed class CapturingIdentityService : IIdentityService
    {
        public IdentityRequestContext? CapturedContext { get; private set; }

        public IdentityCapabilities Capabilities => IdentityCapabilities.Create;

        public Task<IdentityResult> CreateAsync(
            JsonObject request,
            IdentityRequestContext context,
            CancellationToken cancellationToken
        )
        {
            CapturedContext = context;
            return Task.FromResult(
                new IdentityResult { Status = IdentityResultStatus.Success, Payload = "unique-id" }
            );
        }

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

    private static (RequestInfo RequestInfo, CapturingIdentityService Provider) CreateExecutedCreateRequest(
        Dictionary<RouteQualifierName, RouteQualifierValue> routeQualifiers,
        string? tenant,
        string clientId
    )
    {
        var provider = new CapturingIdentityService();
        var requestInfo = No.RequestInfo("context-trace", No.ServiceProvider);
        requestInfo.IdentityOperation = IdentityOperation.Create;
        requestInfo.IdentityProvider = provider;
        requestInfo.IdentityCapabilities = IdentityCapabilities.Create;
        requestInfo.ParsedBody = new JsonObject();
        requestInfo.ClientAuthorizations = No.ClientAuthorizations with { ClientId = clientId };
        requestInfo.FrontendRequest = requestInfo.FrontendRequest with
        {
            Tenant = tenant,
            RouteQualifiers = routeQualifiers,
        };
        return (requestInfo, provider);
    }

    private static IdentityHandler CreateHandler() =>
        new(
            new IdentityProviderBoundary(NullLogger<IdentityProviderBoundary>.Instance),
            IdentityHandler.DefaultMaxRequestLineSize,
            NullLogger<IdentityHandler>.Instance
        );

    [TestFixture]
    public class Given_A_Mixed_Case_Tenant : IdentityRequestContextTests
    {
        private CapturingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            (RequestInfo requestInfo, _provider) = CreateExecutedCreateRequest(
                [],
                tenant: "TenantMixedCase",
                clientId: "client-1"
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_preserves_the_tenant_spelling_exactly()
        {
            _provider.CapturedContext!.Tenant.Should().Be("TenantMixedCase");
        }
    }

    [TestFixture]
    public class Given_A_Null_Tenant : IdentityRequestContextTests
    {
        private CapturingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            (RequestInfo requestInfo, _provider) = CreateExecutedCreateRequest(
                [],
                tenant: null,
                clientId: "client-1"
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_denotes_single_tenant_mode()
        {
            _provider.CapturedContext!.Tenant.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Route_Qualifiers_With_Distinct_Names : IdentityRequestContextTests
    {
        private CapturingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            Dictionary<RouteQualifierName, RouteQualifierValue> qualifiers = new()
            {
                [new RouteQualifierName("districtId")] = new RouteQualifierValue("255901"),
                [new RouteQualifierName("schoolYear")] = new RouteQualifierValue("2026"),
            };
            (RequestInfo requestInfo, _provider) = CreateExecutedCreateRequest(
                qualifiers,
                tenant: "tenant-a",
                clientId: "client-1"
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_preserves_names_and_values_spelling()
        {
            _provider
                .CapturedContext!.RouteQualifiers.Should()
                .BeEquivalentTo(
                    new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2026" }
                );
        }
    }

    [TestFixture]
    public class Given_A_Route_Qualifier_Looked_Up_With_A_Different_Case : IdentityRequestContextTests
    {
        private CapturingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            Dictionary<RouteQualifierName, RouteQualifierValue> qualifiers = new()
            {
                [new RouteQualifierName("districtId")] = new RouteQualifierValue("255901"),
            };
            (RequestInfo requestInfo, _provider) = CreateExecutedCreateRequest(
                qualifiers,
                tenant: "tenant-a",
                clientId: "client-1"
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_finds_the_key_under_a_different_case()
        {
            // Looking the key up with a different case still finds it: proves the map's comparer is
            // OrdinalIgnoreCase, not just that the original spelling round-trips.
            _provider.CapturedContext!.RouteQualifiers.Should().ContainKey("DISTRICTID");
        }

        [Test]
        public void It_returns_the_correct_value_for_that_key()
        {
            _provider.CapturedContext!.RouteQualifiers["DISTRICTID"].Should().Be("255901");
        }
    }

    [TestFixture]
    public class Given_A_Qualifier_Name_Collision_Under_Case_Insensitive_Comparison
        : IdentityRequestContextTests
    {
        /// <summary>
        /// An exception test: the act throws, so it stays inside the assertion rather than moving to
        /// SetUp, but the message's two content checks are one logical outcome about the same
        /// exception.
        /// </summary>
        [Test]
        public async Task It_throws_naming_the_qualifier_and_the_collision()
        {
            Dictionary<RouteQualifierName, RouteQualifierValue> qualifiers = new()
            {
                [new RouteQualifierName("districtId")] = new RouteQualifierValue("255901"),
                [new RouteQualifierName("DistrictId")] = new RouteQualifierValue("999999"),
            };
            (RequestInfo requestInfo, CapturingIdentityService _) = CreateExecutedCreateRequest(
                qualifiers,
                tenant: "tenant-a",
                clientId: "client-1"
            );

            Func<Task> act = async () => await CreateHandler().Execute(requestInfo, TestHelper.NullNext);

            var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
            thrown.Which.Message.Should().Contain("qualifier").And.Contain("collides");
        }
    }

    [TestFixture]
    public class Given_Various_ClientId_Casings : IdentityRequestContextTests
    {
        [TestCase("Client-One")]
        [TestCase("client-one")]
        [TestCase("CLIENT-ONE")]
        public async Task It_passes_the_ClientId_through_unchanged_with_its_original_case(string clientId)
        {
            (RequestInfo requestInfo, CapturingIdentityService provider) = CreateExecutedCreateRequest(
                [],
                tenant: "tenant-a",
                clientId: clientId
            );

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);

            provider.CapturedContext!.ClientId.Should().Be(clientId);
        }
    }

    [TestFixture]
    public class Given_A_Custom_Trace_Id : IdentityRequestContextTests
    {
        private CapturingIdentityService _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            (RequestInfo requestInfo, _provider) = CreateExecutedCreateRequest(
                [],
                tenant: "tenant-a",
                clientId: "client-1"
            );
            requestInfo.FrontendRequest = requestInfo.FrontendRequest with
            {
                TraceId = new TraceId("trace-xyz"),
            };

            await CreateHandler().Execute(requestInfo, TestHelper.NullNext);
        }

        [Test]
        public void It_is_carried_from_the_frontend_request()
        {
            _provider.CapturedContext!.TraceId.Should().Be("trace-xyz");
        }
    }
}
