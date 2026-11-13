// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>
/// Run-wide provisioning through the real Configuration Service and DMS: one claim set per tenant granting only
/// Read on the projection claim, one projection application per tenant with no data stores, and the seeded
/// hierarchy in <c>Tenant_255901</c> / <c>255901/2025</c>, written by an application on the shipped
/// <c>EdFiSandbox</c> claim set (the fixture application's claim set cannot write state agencies or service
/// centers). <see cref="LiveServices"/> registers every record it creates in <see cref="Cleanup"/> as soon as its id
/// is known; <see cref="OneTimeTearDownAsync"/> removes them all, newest first, even when set-up failed part-way.
/// </summary>
[SetUpFixture]
public class ProjectionE2EFixture
{
    public const string SeededTenant = "Tenant_255901";
    public const string SeededQualifier = "255901/2025";
    public const string EmptyTenant = "Tenant_255902";
    public const string EmptyQualifier = "255902/2024";

    private static ProjectionE2EFixture? _current;

    private ProjectionE2EEnvironment? _environment;
    private LiveServices? _services;

    /// <summary>The hierarchy written for the run, in ascending id order, as the projection serves it.</summary>
    public static IReadOnlyList<EducationOrganizationProjectionItem> SeededItems =>
        ProjectionHierarchySeed.Items;

    public static ProjectionE2EFixture Current =>
        _current ?? throw new InvalidOperationException("The projection E2E fixture was not set up.");

    public CleanupRegistry Cleanup { get; } = new();

    public ProjectionE2EEnvironment Environment => _environment!;

    public LiveServices Services => _services!;

    /// <summary>The projection credential of each tenant, as a CMS job's settings would hold it.</summary>
    public Dictionary<string, ClientCredentials> ProjectionClients { get; } = new(StringComparer.Ordinal);

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        _environment = ProjectionE2EEnvironment.Read();
        _services = new LiveServices(_environment, Cleanup);

        foreach (string tenant in new[] { SeededTenant, EmptyTenant })
        {
            // CMS claim set names are unique across tenants, so each tenant's has its own.
            string claimSetName = $"ProjectionReaderE2E{tenant[^6..]}";
            await Services.ImportProjectionClaimSetAsync(tenant, claimSetName);
            ProjectionClients[tenant] = await Services.CreateApplicationAsync(
                tenant,
                $"Projection Reader E2E {tenant}",
                claimSetName,
                dataStoreIds: []
            );
            await Services.ReloadClaimSetsAsync(tenant);
        }

        await SeedHierarchyAsync();

        // The empty store must really be empty, or its case would pass for the wrong reason.
        (HttpStatusCode status, string body) = await Services.GetProjectionPageAsync(
            await Services.ClientTokenAsync(ProjectionClients[EmptyTenant]),
            Environment.Route(EmptyQualifier)
        );
        JsonNode? page = status == HttpStatusCode.OK ? JsonNode.Parse(body) : null;
        if (page?["items"]?.AsArray().Count != 0 || page["nextCursor"] is not null)
        {
            throw new InvalidOperationException(
                $"Setup failed: the {EmptyTenant} {EmptyQualifier} store is not an empty projection "
                    + $"({(int)status}): {body}"
            );
        }

        _current = this;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        IReadOnlyList<string> failures = await Cleanup.RunAsync();
        _services?.Dispose();

        // Reported on its own: a set-up failure stays the failure each test reports.
        if (failures.Count > 0)
        {
            Assert.Fail("DMS projection reader E2E cleanup failed: " + string.Join("; ", failures));
        }
    }

    private async Task SeedHierarchyAsync()
    {
        FixtureRoute route = Environment.Route(SeededQualifier);
        ClientCredentials seeder = await Services.CreateApplicationAsync(
            SeededTenant,
            "Projection Reader E2E seeder",
            "EdFiSandbox",
            [
                .. Environment
                    .Routes.Where(candidate => candidate.Tenant == SeededTenant)
                    .Select(r => r.DataStoreId),
            ]
        );
        string token = await Services.ClientTokenAsync(seeder);
        await ProjectionHierarchySeed.SeedAsync(Services, token, route);
    }
}
