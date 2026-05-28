// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class Given_Embedded_Claims_Json
{
    private JsonObject _claims = null!;

    // Resource claims whose SeedLoader Create grant declares an explicit
    // NoFurtherAuthorizationRequired override because the inherited authorization chain does not
    // cover Create. The canonical example is schoolYearType: the parent edFiTypes
    // defaultAuthorization only defines Read, so the SeedLoader Application would otherwise see
    // zero strategies on Create and 403 the Story-02 REST precondition POST. See
    // bootstrap-design.md §7.2 "schoolYearType override" for the design rationale.
    private static readonly HashSet<string> SeedLoaderCreateOverrideExceptions = new(StringComparer.Ordinal)
    {
        "http://ed-fi.org/identity/claims/ed-fi/schoolYearType",
    };

    // NOTE: This inventory is hand-curated against the bootstrap-design.md §7.2 SeedLoader
    // contract. When Story 06 activates the built-in Populated XML path, new resource types
    // introduced by future Ed-Fi-Data-Standard tags must be reflected here — otherwise a
    // missing claim grants this test green coverage while seed delivery 403s at runtime.
    // Regenerate alongside any change to the Populated seed manifest.
    private static readonly string[] SeedLoaderInventory =
    [
        // Minimal tier
        "http://ed-fi.org/identity/claims/domains/systemDescriptors",
        "http://ed-fi.org/identity/claims/domains/managedDescriptors",
        "http://ed-fi.org/identity/claims/ed-fi/schoolYearType",
        // Populated: EducationOrganization.xml
        "http://ed-fi.org/identity/claims/ed-fi/accountabilityRating",
        "http://ed-fi.org/identity/claims/ed-fi/classPeriod",
        "http://ed-fi.org/identity/claims/ed-fi/communityOrganization",
        "http://ed-fi.org/identity/claims/ed-fi/communityProvider",
        "http://ed-fi.org/identity/claims/ed-fi/communityProviderLicense",
        "http://ed-fi.org/identity/claims/ed-fi/course",
        "http://ed-fi.org/identity/claims/ed-fi/educationServiceCenter",
        "http://ed-fi.org/identity/claims/ed-fi/localEducationAgency",
        "http://ed-fi.org/identity/claims/ed-fi/location",
        "http://ed-fi.org/identity/claims/ed-fi/organizationDepartment",
        "http://ed-fi.org/identity/claims/ed-fi/postSecondaryInstitution",
        "http://ed-fi.org/identity/claims/ed-fi/program",
        "http://ed-fi.org/identity/claims/ed-fi/school",
        // Populated: Student.xml
        "http://ed-fi.org/identity/claims/ed-fi/person",
        "http://ed-fi.org/identity/claims/ed-fi/student",
        // Populated: StudentEnrollment.xml
        "http://ed-fi.org/identity/claims/ed-fi/crisisEvent",
        "http://ed-fi.org/identity/claims/ed-fi/graduationPlan",
        "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation",
        "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationResponsibilityAssociation",
        "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation",
        "http://ed-fi.org/identity/claims/ed-fi/studentSectionAssociation",
        // Populated tier — design contract domain/service claims (per bootstrap-design.md §7.2).
        // SeedLoader Create must be granted directly or via ancestor inheritance, and must not
        // declare authorizationStrategyOverrides — the inherited authorization strategy
        // (NamespaceBased or RelationshipsWithEdOrgsAndPeople) still gates the runtime grant
        // against the SeedLoader Application's namespace prefixes and EdOrg IDs.
        "http://ed-fi.org/identity/claims/domains/educationOrganizations",
        "http://ed-fi.org/identity/claims/domains/people",
        "http://ed-fi.org/identity/claims/domains/relationshipBasedData",
        "http://ed-fi.org/identity/claims/domains/primaryRelationships",
        "http://ed-fi.org/identity/claims/domains/educationStandards",
        "http://ed-fi.org/identity/claims/domains/assessmentMetadata",
        "http://ed-fi.org/identity/claims/domains/surveyDomain",
        "http://ed-fi.org/identity/claims/domains/finance",
        "http://ed-fi.org/identity/claims/domains/tpdm",
        "http://ed-fi.org/identity/claims/ed-fi/studentHealth",
        "http://ed-fi.org/identity/claims/ed-fi/educationContent",
        "http://ed-fi.org/identity/claims/services/identity",
    ];

    public static IEnumerable<string> SeedLoaderInventorySource => SeedLoaderInventory;

    [SetUp]
    public void Setup()
    {
        _claims = LoadEmbeddedClaims();
    }

    [Test]
    public void It_defines_SeedLoader_as_a_system_reserved_claim_set()
    {
        JsonArray claimSets = _claims["claimSets"]!.AsArray();

        JsonObject seedLoaderClaimSet = claimSets
            .OfType<JsonObject>()
            .Single(claimSet => claimSet["claimSetName"]!.GetValue<string>() == "SeedLoader");

        seedLoaderClaimSet["isSystemReserved"]!.GetValue<bool>().Should().BeTrue();
    }

    [TestCaseSource(nameof(SeedLoaderInventorySource))]
    public void It_grants_SeedLoader_Create_with_inherited_authorization(string resourceClaimUri)
    {
        SeedLoaderGrant? result = FindSeedLoaderGrant(
            _claims["claimsHierarchy"]!,
            resourceClaimUri,
            new SeedLoaderGrant(HasCreate: false, HasOverride: false)
        );

        result
            .Should()
            .NotBeNull(
                $"claim '{resourceClaimUri}' must exist in claimsHierarchy so the SeedLoader contract can be verified"
            );

        result!
            .HasCreate.Should()
            .BeTrue($"SeedLoader Create must be granted on '{resourceClaimUri}' or any ancestor");

        if (SeedLoaderCreateOverrideExceptions.Contains(resourceClaimUri))
        {
            result
                .HasOverride.Should()
                .BeTrue(
                    $"SeedLoader Create on '{resourceClaimUri}' must declare an explicit "
                        + "authorizationStrategyOverrides entry because the inherited authorization chain "
                        + "does not cover Create (e.g. edFiTypes defaultAuthorization defines only Read for "
                        + "closed-XSD-enum types); see bootstrap-design.md §7.2 'schoolYearType override'"
                );
        }
        else
        {
            result
                .HasOverride.Should()
                .BeFalse(
                    $"SeedLoader Create on '{resourceClaimUri}' (or any ancestor) must not declare "
                        + "authorizationStrategyOverrides — bootstrap-design.md §7.2 requires SeedLoader to inherit "
                        + "the claim's existing authorization strategy so the relationship-based-data strategies "
                        + "still gate the SeedLoader Application's namespace prefixes and EdOrg IDs at runtime"
                );
        }
    }

    private sealed record SeedLoaderGrant(bool HasCreate, bool HasOverride);

    private static SeedLoaderGrant? FindSeedLoaderGrant(
        JsonNode node,
        string targetUri,
        SeedLoaderGrant ancestor
    )
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                SeedLoaderGrant? result = FindSeedLoaderGrant(item, targetUri, ancestor);
                if (result is not null)
                {
                    return result;
                }
            }

            return null;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        SeedLoaderGrant local = ReadSeedLoaderGrantAt(obj);
        SeedLoaderGrant effective = new(
            HasCreate: ancestor.HasCreate || local.HasCreate,
            HasOverride: ancestor.HasOverride || local.HasOverride
        );

        if (
            obj.TryGetPropertyValue("name", out JsonNode? nameNode)
            && nameNode?.GetValue<string>() == targetUri
        )
        {
            return effective;
        }

        if (
            obj.TryGetPropertyValue("claims", out JsonNode? claimsNode) && claimsNode is JsonArray claimsArray
        )
        {
            foreach (JsonNode? child in claimsArray)
            {
                if (child is null)
                {
                    continue;
                }

                SeedLoaderGrant? result = FindSeedLoaderGrant(child, targetUri, effective);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static SeedLoaderGrant ReadSeedLoaderGrantAt(JsonObject claimNode)
    {
        if (
            !claimNode.TryGetPropertyValue("claimSets", out JsonNode? claimSetsNode)
            || claimSetsNode is not JsonArray claimSets
        )
        {
            return new SeedLoaderGrant(HasCreate: false, HasOverride: false);
        }

        JsonObject? seedLoader = claimSets
            .OfType<JsonObject>()
            .FirstOrDefault(cs =>
                cs.TryGetPropertyValue("name", out JsonNode? n) && n?.GetValue<string>() == "SeedLoader"
            );

        if (seedLoader is null)
        {
            return new SeedLoaderGrant(HasCreate: false, HasOverride: false);
        }

        if (
            !seedLoader.TryGetPropertyValue("actions", out JsonNode? actionsNode)
            || actionsNode is not JsonArray actions
        )
        {
            return new SeedLoaderGrant(HasCreate: false, HasOverride: false);
        }

        JsonObject? createAction = actions
            .OfType<JsonObject>()
            .FirstOrDefault(a =>
                a.TryGetPropertyValue("name", out JsonNode? n) && n?.GetValue<string>() == "Create"
            );

        if (createAction is null)
        {
            return new SeedLoaderGrant(HasCreate: false, HasOverride: false);
        }

        bool hasOverride = createAction.TryGetPropertyValue("authorizationStrategyOverrides", out _);

        return new SeedLoaderGrant(HasCreate: true, HasOverride: hasOverride);
    }

    private static JsonObject LoadEmbeddedClaims()
    {
        Assembly assembly = typeof(ClaimsProvider).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Claims.Claims.json";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not load embedded resource '{resourceName}'.");
        using StreamReader reader = new(stream);

        JsonNode claims =
            JsonNode.Parse(reader.ReadToEnd())
            ?? throw new InvalidOperationException("Embedded Claims.json parsed to null.");

        return claims.AsObject();
    }
}
