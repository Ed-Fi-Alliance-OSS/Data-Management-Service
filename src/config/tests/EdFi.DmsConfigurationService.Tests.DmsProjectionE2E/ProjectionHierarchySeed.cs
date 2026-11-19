// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E;

/// <summary>
/// The education-organization hierarchy every live reader fixture seeds through the DMS API, and the items the
/// projection serves for it, in ascending id order.
/// </summary>
public static class ProjectionHierarchySeed
{
    private const string Namespace = "uri://ed-fi.org";
    private const string DescriptorCode = "ProjectionReaderE2E";

    /// <summary>The namespace prefix an application writing the seed needs.</summary>
    public const string NamespacePrefix = Namespace;

    /// <summary>The hierarchy, in ascending id order, as the projection serves it.</summary>
    public static IReadOnlyList<EducationOrganizationProjectionItem> Items { get; } =
    [
        new(1, "Reader E2E State", "RE SEA", "edfi.StateEducationAgency", null),
        new(10, "Reader E2E Region Ten", null, "edfi.EducationServiceCenter", 1),
        new(100, "Reader E2E District 100", "RE 100", "edfi.LocalEducationAgency", 10),
        new(101, "Reader E2E District 101", null, "edfi.LocalEducationAgency", 100),
        new(100001, "Reader E2E School 100001", null, "edfi.School", 100),
        new(101001, "Escuela Lectora 101001 ñ", null, "edfi.School", 101),
        new(900001, "Reader E2E School 900001", null, "edfi.School", null),
    ];

    /// <summary>
    /// Writes the descriptors the hierarchy references (an existing one is reused) and then every item, through
    /// <paramref name="route"/> with <paramref name="token"/>. <see cref="LiveServices"/> registers each document it
    /// creates for deletion.
    /// </summary>
    public static async Task SeedAsync(LiveServices services, string token, FixtureRoute route)
    {
        foreach (
            (string resource, string descriptor) in new[]
            {
                ("educationOrganizationCategoryDescriptors", "EducationOrganizationCategoryDescriptor"),
                ("gradeLevelDescriptors", "GradeLevelDescriptor"),
                ("localEducationAgencyCategoryDescriptors", "LocalEducationAgencyCategoryDescriptor"),
            }
        )
        {
            await services.PostResourceAsync(
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

        foreach (EducationOrganizationProjectionItem item in Items)
        {
            (string resource, JsonObject body) = Body(item);
            await services.PostResourceAsync(token, route, resource, body, allowExisting: false);
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
