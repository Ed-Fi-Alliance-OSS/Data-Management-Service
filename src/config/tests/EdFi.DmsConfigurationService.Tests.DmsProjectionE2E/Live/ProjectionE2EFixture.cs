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

    private const string Namespace = "uri://ed-fi.org";
    private const string DescriptorCode = "ProjectionReaderE2E";

    private static ProjectionE2EFixture? _current;

    private ProjectionE2EEnvironment? _environment;
    private LiveServices? _services;

    /// <summary>The hierarchy written for the run, in ascending id order, as the projection serves it.</summary>
    public static IReadOnlyList<EducationOrganizationProjectionItem> SeededItems { get; } =
    [
        new(1, "Reader E2E State", "RE SEA", "edfi.StateEducationAgency", null),
        new(10, "Reader E2E Region Ten", null, "edfi.EducationServiceCenter", 1),
        new(100, "Reader E2E District 100", "RE 100", "edfi.LocalEducationAgency", 10),
        new(101, "Reader E2E District 101", null, "edfi.LocalEducationAgency", 100),
        new(100001, "Reader E2E School 100001", null, "edfi.School", 100),
        new(101001, "Escuela Lectora 101001 ñ", null, "edfi.School", 101),
        new(900001, "Reader E2E School 900001", null, "edfi.School", null),
    ];

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

        foreach (
            (string resource, string descriptor) in new[]
            {
                ("educationOrganizationCategoryDescriptors", "EducationOrganizationCategoryDescriptor"),
                ("gradeLevelDescriptors", "GradeLevelDescriptor"),
                ("localEducationAgencyCategoryDescriptors", "LocalEducationAgencyCategoryDescriptor"),
            }
        )
        {
            await Services.PostResourceAsync(
                token,
                route,
                resource,
                new JsonObject
                {
                    ["namespace"] = $"{Namespace}/{descriptor}",
                    ["codeValue"] = DescriptorCode,
                    ["shortDescription"] = DescriptorCode,
                },
                allowExisting: true
            );
        }

        foreach (EducationOrganizationProjectionItem item in SeededItems)
        {
            (string resource, JsonObject body) = Body(item);
            await Services.PostResourceAsync(token, route, resource, body, allowExisting: false);
        }
    }

    /// <summary>The resource and body that make DMS serve <paramref name="item"/> with its expected parent.</summary>
    private static (string Resource, JsonObject Body) Body(EducationOrganizationProjectionItem item)
    {
        JsonObject body = new() { ["nameOfInstitution"] = item.NameOfInstitution };
        if (item.ShortNameOfInstitution is { } shortName)
        {
            body["shortNameOfInstitution"] = shortName;
        }

        JsonArray Categories() =>
            new(
                new JsonObject
                {
                    ["educationOrganizationCategoryDescriptor"] =
                        $"{Namespace}/EducationOrganizationCategoryDescriptor#{DescriptorCode}",
                }
            );

        long id = item.EducationOrganizationId;
        switch (item.Discriminator)
        {
            case "edfi.StateEducationAgency":
                body["stateEducationAgencyId"] = id;
                body["categories"] = Categories();
                return ("stateEducationAgencies", body);
            case "edfi.EducationServiceCenter":
                body["educationServiceCenterId"] = id;
                body["categories"] = Categories();
                body["stateEducationAgencyReference"] = new JsonObject { ["stateEducationAgencyId"] = 1 };
                return ("educationServiceCenters", body);
            case "edfi.LocalEducationAgency":
                body["localEducationAgencyId"] = id;
                body["categories"] = Categories();
                body["localEducationAgencyCategoryDescriptor"] =
                    $"{Namespace}/LocalEducationAgencyCategoryDescriptor#{DescriptorCode}";
                if (id == 100)
                {
                    // Parent precedence: the service center wins over the state agency.
                    body["educationServiceCenterReference"] = new JsonObject
                    {
                        ["educationServiceCenterId"] = 10,
                    };
                    body["stateEducationAgencyReference"] = new JsonObject { ["stateEducationAgencyId"] = 1 };
                }
                else
                {
                    body["parentLocalEducationAgencyReference"] = new JsonObject
                    {
                        ["localEducationAgencyId"] = item.ParentId,
                    };
                }

                return ("localEducationAgencies", body);
            case "edfi.School":
                body["schoolId"] = id;
                body["educationOrganizationCategories"] = Categories();
                body["gradeLevels"] = new JsonArray(
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = $"{Namespace}/GradeLevelDescriptor#{DescriptorCode}",
                    }
                );
                if (item.ParentId is { } localAgency)
                {
                    body["localEducationAgencyReference"] = new JsonObject
                    {
                        ["localEducationAgencyId"] = localAgency,
                    };
                }

                return ("schools", body);
            default:
                throw new InvalidOperationException($"No seeding body for {item.Discriminator}.");
        }
    }
}
