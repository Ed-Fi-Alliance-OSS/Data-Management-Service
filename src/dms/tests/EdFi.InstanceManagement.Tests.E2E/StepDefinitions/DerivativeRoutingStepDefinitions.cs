// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// Steps for the snapshot and read-replica routing feature.
/// </summary>
/// <remarks>
/// The arrangement comes from the suite-owned fixture: one route whose data store carries a read replica
/// and a snapshot, each pointing at another fixture route's database. That is what makes the three
/// physical databases separately writable through ordinary routes, so a scenario seeds each one through
/// the production API and then names which one answered a derivative-routed read. Nothing here writes to
/// a database directly, and no route qualifier is spelled out: reading the arrangement from the fixture
/// keeps the feature correct if the fixture's routes ever move.
/// </remarks>
[Binding]
public class DerivativeRoutingStepDefinitions(InstanceManagementContext context)
{
    /// <summary>The role names a scenario uses to name one of the two derivative databases.</summary>
    private const string ReadReplicaRole = "read replica";
    private const string SnapshotRole = "snapshot";

    private static InstanceFixtureState Fixture => InstanceFixtureState.Current;

    private static InstanceFixtureRoute DerivativeRoute => Fixture.DerivativeRoutingRoute;

    /// <summary>
    /// The ordinary route through which a scenario seeds the database that serves one of the derivatives.
    /// </summary>
    /// <remarks>
    /// Resolved through the derivative arrangement rather than by route name, so a scenario cannot seed a
    /// database the routing data store does not actually use and then assert against it.
    /// </remarks>
    private static InstanceFixtureRoute RouteForRole(string role)
    {
        var derivativeType = role switch
        {
            _ when string.Equals(role, ReadReplicaRole, StringComparison.OrdinalIgnoreCase) =>
                InstanceFixtureDerivativeTypes.ReadReplica,
            _ when string.Equals(role, SnapshotRole, StringComparison.OrdinalIgnoreCase) =>
                InstanceFixtureDerivativeTypes.Snapshot,
            _ => throw new ArgumentException(
                $"'{role}' is not a known database role. Use '{ReadReplicaRole}' or '{SnapshotRole}'.",
                nameof(role)
            ),
        };

        var derivative = DerivativeRoute.GetDerivative(derivativeType);

        var route =
            Fixture.Routes.FirstOrDefault(r => r.DatabaseOrdinal == derivative.DatabaseOrdinal)
            ?? throw new InvalidOperationException(
                $"The '{derivativeType}' derivative points at database ordinal {derivative.DatabaseOrdinal}, "
                    + "which no fixture route owns, so a scenario cannot seed it through the API."
            );

        // One token authenticates every request in a scenario, so the routes that seed the three
        // databases must belong to the derivative route's tenant. If the fixture ever moves one of them,
        // fail here with the reason rather than as an unexplained 404 from a mismatched tenant.
        if (!string.Equals(route.TenantName, DerivativeRoute.TenantName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The '{derivativeType}' derivative's database is owned by route '{route.RouteQualifier}' "
                    + $"under tenant '{route.TenantName}', but the derivative route is under "
                    + $"'{DerivativeRoute.TenantName}'. This feature authenticates once per scenario."
            );
        }

        return route;
    }

    private DmsApiClient NewClient(string tenantName) =>
        new(TestConfiguration.DmsApiUrl, context.DmsToken!, tenantName);

    private void RequireAuthenticated() =>
        context.DmsToken.Should().NotBeNullOrEmpty("Must be authenticated to DMS first");

    private static async Task<long> ReadNewestChangeVersionAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue();

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);

        return document.RootElement.GetProperty("newestChangeVersion").GetInt64();
    }

    /// <summary>
    /// Proves the scenario authenticated as the tenant that owns the derivative arrangement, so a
    /// misaddressed feature fails on its premise rather than on an unexplained routed response.
    /// </summary>
    [Given("the authenticated tenant owns the derivative-routing route")]
    public void GivenAuthenticatedTenantOwnsDerivativeRoute()
    {
        RequireAuthenticated();

        context
            .CurrentTenant.Should()
            .Be(
                DerivativeRoute.TenantName,
                "the derivative-routing route and the routes that seed its replica and snapshot are all "
                    + "owned by that tenant"
            );
    }

    [When("a POST request is made to the {string} database resource {string} with body:")]
    public async Task PostToRoleDatabase(string role, string resource, string jsonBody)
    {
        RequireAuthenticated();

        var route = RouteForRole(role);
        var body = JsonSerializer.Deserialize<JsonElement>(jsonBody);

        using var client = NewClient(route.TenantName);
        context.LastResponse = await client.PostResourceAsync(
            route.DistrictId,
            route.SchoolYear,
            resource,
            body
        );
    }

    [When("a GET request is made to the derivative route resource {string}")]
    public async Task GetThroughDerivativeRoute(string resource)
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetResourceAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource
        );
    }

    [When("a GET request is made to the derivative route resource {string} with Use-Snapshot {string}")]
    public async Task GetThroughDerivativeRouteWithUseSnapshot(string resource, string useSnapshot)
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetResourceAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource,
            useSnapshot: useSnapshot
        );
    }

    [When("the id from the response location is captured as {string}")]
    public void CaptureIdFromLocation(string variableName)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var location =
            context.LastResponse.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("The response carried no Location header.");

        context.CapturedIds[variableName] = location[(location.LastIndexOf('/') + 1)..];
    }

    [When("a GET by id request is made to the derivative route resource {string} using captured {string}")]
    public async Task GetByIdThroughDerivativeRoute(string resource, string variableName)
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetResourceByIdAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource,
            context.CapturedIds[variableName]
        );
    }

    [When(
        "a GET by id request is made to the derivative route resource {string} using captured {string} with Use-Snapshot {string}"
    )]
    public async Task GetByIdThroughDerivativeRouteWithUseSnapshot(
        string resource,
        string variableName,
        string useSnapshot
    )
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetResourceByIdAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource,
            context.CapturedIds[variableName],
            useSnapshot
        );
    }

    [When("a GET request is made to the derivative route {string} for resource {string}")]
    public async Task GetTrackedChangesThroughDerivativeRoute(string segment, string resource)
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetTrackedChangesAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource,
            segment
        );
    }

    [When(
        "a GET request is made to the derivative route {string} for resource {string} with Use-Snapshot {string}"
    )]
    public async Task GetTrackedChangesThroughDerivativeRouteWithUseSnapshot(
        string segment,
        string resource,
        string useSnapshot
    )
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        context.LastResponse = await client.GetTrackedChangesAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            resource,
            segment,
            useSnapshot
        );
    }

    [When("I capture the newest change version for the derivative route as {string}")]
    public async Task CaptureChangeVersionForDerivativeRoute(string variableName)
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        var response = await client.GetAvailableChangeVersionsAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear
        );

        context.CapturedChangeVersions[variableName] = await ReadNewestChangeVersionAsync(response);
    }

    [When(
        "I capture the newest change version for the derivative route with Use-Snapshot {string} as {string}"
    )]
    public async Task CaptureChangeVersionForDerivativeRouteWithUseSnapshot(
        string useSnapshot,
        string variableName
    )
    {
        RequireAuthenticated();

        using var client = NewClient(DerivativeRoute.TenantName);
        var response = await client.GetAvailableChangeVersionsAsync(
            DerivativeRoute.DistrictId,
            DerivativeRoute.SchoolYear,
            useSnapshot
        );

        context.CapturedChangeVersions[variableName] = await ReadNewestChangeVersionAsync(response);
    }

    [Then("the captured change version {string} equals the captured change version {string}")]
    public void ThenCapturedChangeVersionsMatch(string left, string right)
    {
        context
            .CapturedChangeVersions[left]
            .Should()
            .Be(
                context.CapturedChangeVersions[right],
                "a read answered by a database that received no write must report an unchanged value"
            );
    }

    [Then("the captured change version {string} is greater than the captured change version {string}")]
    public void ThenCapturedChangeVersionIsGreater(string left, string right)
    {
        context
            .CapturedChangeVersions[left]
            .Should()
            .BeGreaterThan(
                context.CapturedChangeVersions[right],
                "only the target that received the write can report a higher change version afterwards"
            );
    }
}
