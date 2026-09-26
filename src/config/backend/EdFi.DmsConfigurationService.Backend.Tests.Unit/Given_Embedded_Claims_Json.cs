// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class Given_Embedded_Claims_Json
{
    private JsonObject _claims = null!;

    // SeedLoader is granted Update alongside Create because a POST that finds an existing record is
    // authorized as Update, and re-running seed delivery against a seeded database re-POSTs every record.
    //
    // Resource claims whose SeedLoader Create and Update grants declare an explicit
    // NoFurtherAuthorizationRequired override because the inherited authorization chain does not
    // cover either action. The canonical example is schoolYearType: the parent edFiTypes
    // defaultAuthorization defines neither Create nor Update, so the SeedLoader Application would otherwise
    // see zero strategies and 403 the Story-02 REST precondition POST. See
    // bootstrap-design.md §7.2 "schoolYearType override" for the design rationale.
    private static readonly HashSet<string> SeedLoaderOverrideExceptions = new(StringComparer.Ordinal)
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

    public static IEnumerable<TestCaseData> SeedLoaderGrantSource =>
        from resourceClaimUri in SeedLoaderInventory
        from actionName in new[] { "Create", "Update" }
        select new TestCaseData(resourceClaimUri, actionName);

    // The claim sets a default deployment loads. The E2E claim sets are defined only by the
    // test-owned claims fragments, never by the shipped Claims.json.
    private static readonly string[] ShippedClaimSetNames =
    [
        "SISVendor",
        "EdFiSandbox",
        "RosterVendor",
        "AssessmentVendor",
        "AssessmentRead",
        "BootstrapDescriptorsandEdOrgs",
        "SeedLoader",
        "DistrictHostedSISVendor",
        "EdFiODSAdminApp",
        "ABConnect",
        "EdFiAPIPublisherReader",
        "EdFiAPIPublisherWriter",
        "FinanceVendor",
        "EducationPreparationProgram",
    ];

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

    [Test]
    public void It_defines_ds61_claims_with_dms_operational_claim_sets_and_normalized_claim_names()
    {
        JsonObject claims = LoadEmbeddedClaims("ds61");
        JsonArray claimSets = claims["claimSets"]!.AsArray();

        claimSets
            .OfType<JsonObject>()
            .Select(claimSet => claimSet["claimSetName"]!.GetValue<string>())
            .Should()
            .Contain(["SeedLoader", "EdFiODSAdminApp"]);

        ClaimNames(claims["claimsHierarchy"]!)
            .Should()
            .NotContain(claimName => claimName.Contains("/ods/identity/claims", StringComparison.Ordinal));

        SeedLoaderGrant? epdmGrant = FindSeedLoaderGrant(
            claims["claimsHierarchy"]!,
            "http://ed-fi.org/identity/claims/domains/epdm",
            "Create",
            new SeedLoaderGrant(HasAction: false, HasOverride: false)
        );

        epdmGrant.Should().NotBeNull();
        epdmGrant!.HasAction.Should().BeTrue();
        epdmGrant.HasOverride.Should().BeFalse();
    }

    [Test]
    public async Task It_projects_ODS_effective_people_CRUD_claim_metadata()
    {
        const string noFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
        const string relationshipsWithEdOrgsOnly = "RelationshipsWithEdOrgsOnly";
        const string relationshipsWithEdOrgsAndPeople = "RelationshipsWithEdOrgsAndPeople";
        const string relationshipsWithStudentsOnly = "RelationshipsWithStudentsOnly";

        var metadata = await CreateClaimSetMetadata("EdFiSandbox");

        foreach (var personClaimName in new[] { "student", "contact", "staff" }.Select(EdFiClaim))
        {
            AssertActionStrategies(metadata, personClaimName, "Create", noFurtherAuthorizationRequired);
            AssertActionStrategies(metadata, personClaimName, "Read", relationshipsWithEdOrgsAndPeople);
            AssertActionStrategies(metadata, personClaimName, "Update", relationshipsWithEdOrgsAndPeople);
            AssertActionStrategies(metadata, personClaimName, "Delete", noFurtherAuthorizationRequired);
        }

        AssertActionStrategies(
            metadata,
            EdFiClaim("studentSchoolAssociation"),
            "Create",
            relationshipsWithEdOrgsOnly
        );
        AssertActionStrategies(
            metadata,
            EdFiClaim("studentSchoolAssociation"),
            "Read",
            relationshipsWithEdOrgsAndPeople
        );
        AssertActionStrategies(
            metadata,
            EdFiClaim("studentSchoolAssociation"),
            "Update",
            relationshipsWithEdOrgsAndPeople
        );
        AssertActionStrategies(
            metadata,
            EdFiClaim("studentSchoolAssociation"),
            "Delete",
            relationshipsWithEdOrgsAndPeople
        );

        foreach (var actionName in new[] { "Create", "Read", "Update", "Delete" })
        {
            AssertActionStrategies(
                metadata,
                EdFiClaim("studentContactAssociation"),
                actionName,
                relationshipsWithStudentsOnly
            );
            AssertActionStrategies(
                metadata,
                EdFiClaim("studentEducationOrganizationResponsibilityAssociation"),
                actionName,
                relationshipsWithEdOrgsAndPeople
            );
        }
    }

    [TestCase("ds52")]
    [TestCase("ds61")]
    public async Task It_projects_smoke_ReadChanges_claim_metadata(string standardFolder)
    {
        const string noFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
        const string relationshipsWithEdOrgsAndPeople = "RelationshipsWithEdOrgsAndPeople";
        const string relationshipsWithEdOrgsAndPeopleIncludingDeletes =
            "RelationshipsWithEdOrgsAndPeopleIncludingDeletes";

        _claims = LoadEmbeddedClaims(standardFolder);
        var metadata = await CreateClaimSetMetadata("EdFiSandbox");

        AssertActionStrategies(
            metadata,
            EdFiClaim("schoolYearType"),
            "ReadChanges",
            noFurtherAuthorizationRequired
        );

        foreach (
            var financeClaimName in new[]
            {
                "chartOfAccount",
                "localAccount",
                "localActual",
                "localBudget",
                "localContractedStaff",
                "localEncumbrance",
                "localPayroll",
            }.Select(EdFiClaim)
        )
        {
            AssertActionStrategies(
                metadata,
                financeClaimName,
                "ReadChanges",
                relationshipsWithEdOrgsAndPeopleIncludingDeletes
            );
        }

        AssertActionStrategies(
            metadata,
            EdFiClaim("studentHealth"),
            "ReadChanges",
            relationshipsWithEdOrgsAndPeopleIncludingDeletes
        );
        AssertActionStrategies(
            metadata,
            EdFiClaim("studentHealth"),
            "Read",
            relationshipsWithEdOrgsAndPeople
        );

        if (standardFolder == "ds61")
        {
            foreach (
                var specialEducationClaimName in new[]
                {
                    "IDEAEvent",
                    "StudentIEP",
                    "StudentIEPGoal",
                    "StudentIEPServiceDelivery",
                    "StudentIEPServicePrescription",
                }.Select(EdFiClaim)
            )
            {
                AssertActionStrategies(
                    metadata,
                    specialEducationClaimName,
                    "ReadChanges",
                    relationshipsWithEdOrgsAndPeopleIncludingDeletes
                );
            }
        }
    }

    [TestCase("ds52")]
    [TestCase("ds61")]
    public void It_declares_exactly_the_shipped_claim_sets(string standardFolder)
    {
        DeclaredClaimSetNames(LoadEmbeddedClaims(standardFolder))
            .Should()
            .BeEquivalentTo(ShippedClaimSetNames);
    }

    [TestCase("ds52")]
    [TestCase("ds61")]
    public void It_grants_only_declared_claim_sets_and_no_E2E_claim_set(string standardFolder)
    {
        JsonObject claims = LoadEmbeddedClaims(standardFolder);

        List<string> grantedClaimSetNames =
        [
            .. ClaimNodes(claims["claimsHierarchy"]!)
                .SelectMany(GrantedClaimSetNames)
                .Distinct(StringComparer.Ordinal),
        ];

        grantedClaimSetNames
            .Should()
            .Contain("EdFiSandbox", "the hierarchy walk must reach claim set grants");
        grantedClaimSetNames.Should().BeSubsetOf(DeclaredClaimSetNames(claims));
        grantedClaimSetNames.Should().NotContain(name => name.StartsWith("E2E-", StringComparison.Ordinal));
    }

    // Every default compose deployment runs Hybrid with the shipped AdditionalClaimsets directory
    // mounted, so a non-parent fragment there would register its claim set in every deployment
    [TestCase("ds52")]
    [TestCase("ds61")]
    public void It_registers_no_claim_set_from_the_default_fragment_directory(string standardFolder)
    {
        string defaultFragmentsPath = Path.Combine(
            Given_E2E_Test_Fragments.FindRepositoryRoot(),
            "src",
            "config",
            "backend",
            "EdFi.DmsConfigurationService.Backend",
            "Deploy",
            "AdditionalClaimsets"
        );
        JsonObject baseClaims = LoadEmbeddedClaims(standardFolder);

        ClaimsLoadResult result = new ClaimsFragmentComposer(
            A.Fake<ILogger<ClaimsFragmentComposer>>()
        ).ComposeClaimsFromFragments(
            new ClaimsDocument(baseClaims["claimSets"]!, baseClaims["claimsHierarchy"]!),
            defaultFragmentsPath
        );

        result.Failures.Should().BeEmpty();
        JsonObject composed = new()
        {
            ["claimSets"] = result.Nodes!.ClaimSetsNode.DeepClone(),
            ["claimsHierarchy"] = result.Nodes.ClaimsHierarchyNode.DeepClone(),
        };

        DeclaredClaimSetNames(composed).Should().BeEquivalentTo(ShippedClaimSetNames);
        ClaimNodes(composed["claimsHierarchy"]!)
            .SelectMany(GrantedClaimSetNames)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeSubsetOf(ShippedClaimSetNames);
    }

    [TestCase("ds52")]
    [TestCase("ds61")]
    public void It_has_no_empty_claim_sets_on_hierarchy_nodes(string standardFolder)
    {
        List<JsonObject> nodesWithClaimSets =
        [
            .. ClaimNodes(LoadEmbeddedClaims(standardFolder)["claimsHierarchy"]!)
                .Where(node => node.ContainsKey("claimSets")),
        ];

        nodesWithClaimSets.Should().NotBeEmpty();
        nodesWithClaimSets
            .Where(node => node["claimSets"] is not JsonArray { Count: > 0 })
            .Select(node => node["name"]!.GetValue<string>())
            .Should()
            .BeEmpty("a hierarchy node whose last grant was removed must drop its claimSets key");
    }

    [TestCaseSource(nameof(SeedLoaderGrantSource))]
    public void It_grants_SeedLoader_the_action_with_inherited_authorization(
        string resourceClaimUri,
        string actionName
    )
    {
        SeedLoaderGrant? result = FindSeedLoaderGrant(
            _claims["claimsHierarchy"]!,
            resourceClaimUri,
            actionName,
            new SeedLoaderGrant(HasAction: false, HasOverride: false)
        );

        result
            .Should()
            .NotBeNull(
                $"claim '{resourceClaimUri}' must exist in claimsHierarchy so the SeedLoader contract can be verified"
            );

        result!
            .HasAction.Should()
            .BeTrue($"SeedLoader {actionName} must be granted on '{resourceClaimUri}' or any ancestor");

        if (SeedLoaderOverrideExceptions.Contains(resourceClaimUri))
        {
            result
                .HasOverride.Should()
                .BeTrue(
                    $"SeedLoader {actionName} on '{resourceClaimUri}' must declare an explicit "
                        + "authorizationStrategyOverrides entry because the inherited authorization chain "
                        + $"does not cover {actionName} (e.g. edFiTypes defaultAuthorization does not define it for "
                        + "closed-XSD-enum types); see bootstrap-design.md §7.2 'schoolYearType override'"
                );
        }
        else
        {
            result
                .HasOverride.Should()
                .BeFalse(
                    $"SeedLoader {actionName} on '{resourceClaimUri}' (or any ancestor) must not declare "
                        + "authorizationStrategyOverrides — bootstrap-design.md §7.2 requires SeedLoader to inherit "
                        + "the claim's existing authorization strategy so the relationship-based-data strategies "
                        + "still gate the SeedLoader Application's namespace prefixes and EdOrg IDs at runtime"
                );
        }
    }

    private sealed record SeedLoaderGrant(bool HasAction, bool HasOverride);

    private static IEnumerable<string> ClaimNames(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                foreach (string itemClaimName in ClaimNames(item))
                {
                    yield return itemClaimName;
                }
            }

            yield break;
        }

        if (node is not JsonObject obj)
        {
            yield break;
        }

        if (
            obj.TryGetPropertyValue("name", out JsonNode? nameNode)
            && nameNode?.GetValue<string>() is string claimName
        )
        {
            yield return claimName;
        }

        if (obj.TryGetPropertyValue("claims", out JsonNode? claimsNode) && claimsNode is JsonArray claims)
        {
            foreach (JsonNode? child in claims)
            {
                if (child is null)
                {
                    continue;
                }

                foreach (string childClaimName in ClaimNames(child))
                {
                    yield return childClaimName;
                }
            }
        }
    }

    private static IEnumerable<string> DeclaredClaimSetNames(JsonObject claims) =>
        claims["claimSets"]!
            .AsArray()
            .OfType<JsonObject>()
            .Select(claimSet => claimSet["claimSetName"]!.GetValue<string>());

    private static IEnumerable<JsonObject> ClaimNodes(JsonNode node)
    {
        IEnumerable<JsonObject> nodes = node switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject obj => [obj],
            _ => [],
        };

        foreach (JsonObject claim in nodes)
        {
            yield return claim;

            if (claim["claims"] is JsonArray children)
            {
                foreach (JsonObject child in ClaimNodes(children))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<string> GrantedClaimSetNames(JsonObject claimNode) =>
        (claimNode["claimSets"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(claimSet => claimSet["name"]!.GetValue<string>());

    private static SeedLoaderGrant? FindSeedLoaderGrant(
        JsonNode node,
        string targetUri,
        string actionName,
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

                SeedLoaderGrant? result = FindSeedLoaderGrant(item, targetUri, actionName, ancestor);
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

        SeedLoaderGrant local = ReadSeedLoaderGrantAt(obj, actionName);
        SeedLoaderGrant effective = new(
            HasAction: ancestor.HasAction || local.HasAction,
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

                SeedLoaderGrant? result = FindSeedLoaderGrant(child, targetUri, actionName, effective);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static SeedLoaderGrant ReadSeedLoaderGrantAt(JsonObject claimNode, string actionName)
    {
        if (
            !claimNode.TryGetPropertyValue("claimSets", out JsonNode? claimSetsNode)
            || claimSetsNode is not JsonArray claimSets
        )
        {
            return new SeedLoaderGrant(HasAction: false, HasOverride: false);
        }

        JsonObject? seedLoader = claimSets
            .OfType<JsonObject>()
            .FirstOrDefault(cs =>
                cs.TryGetPropertyValue("name", out JsonNode? n) && n?.GetValue<string>() == "SeedLoader"
            );

        if (seedLoader is null)
        {
            return new SeedLoaderGrant(HasAction: false, HasOverride: false);
        }

        if (
            !seedLoader.TryGetPropertyValue("actions", out JsonNode? actionsNode)
            || actionsNode is not JsonArray actions
        )
        {
            return new SeedLoaderGrant(HasAction: false, HasOverride: false);
        }

        JsonObject? action = actions
            .OfType<JsonObject>()
            .FirstOrDefault(a =>
                a.TryGetPropertyValue("name", out JsonNode? n) && n?.GetValue<string>() == actionName
            );

        if (action is null)
        {
            return new SeedLoaderGrant(HasAction: false, HasOverride: false);
        }

        bool hasOverride = action.TryGetPropertyValue("authorizationStrategyOverrides", out _);

        return new SeedLoaderGrant(HasAction: true, HasOverride: hasOverride);
    }

    private async Task<ClaimSetMetadata> CreateClaimSetMetadata(string claimSetName)
    {
        var claimSetRepository = A.Fake<IClaimSetRepository>();
        A.CallTo(() => claimSetRepository.QueryClaimSet(A<ClaimSetQuery>.Ignored))
            .Returns(new ClaimSetQueryResult.Success([.. LoadClaimSetResponses()]));

        var hierarchy =
            JsonSerializer.Deserialize<List<Claim>>(
                _claims["claimsHierarchy"]!.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("Embedded claimsHierarchy parsed to null.");

        var response = await new AuthorizationMetadataResponseFactory(claimSetRepository).Create(
            claimSetName,
            hierarchy
        );

        return response.ClaimSets.Should().ContainSingle(c => c.ClaimSetName == claimSetName).Which;
    }

    private IReadOnlyList<ClaimSetResponse> LoadClaimSetResponses()
    {
        JsonArray claimSets = _claims["claimSets"]!.AsArray();

        return
        [
            .. claimSets
                .OfType<JsonObject>()
                .Select(
                    (claimSet, index) =>
                        new ClaimSetResponse
                        {
                            Id = index + 1,
                            Name = claimSet["claimSetName"]!.GetValue<string>(),
                            IsSystemReserved = claimSet["isSystemReserved"]?.GetValue<bool>() ?? false,
                        }
                ),
        ];
    }

    private static void AssertActionStrategies(
        ClaimSetMetadata metadata,
        string claimName,
        string actionName,
        params string[] expectedStrategies
    )
    {
        var claim = metadata.Claims.Should().ContainSingle(c => c.Name == claimName).Which;
        var authorization = metadata
            .Authorizations.Should()
            .ContainSingle(a => a.Id == claim.AuthorizationId)
            .Which;
        var action = authorization.Actions.Should().ContainSingle(a => a.Name == actionName).Which;

        action
            .AuthorizationStrategies.Select(static strategy => strategy.Name)
            .Should()
            .Equal(expectedStrategies);
    }

    private static string EdFiClaim(string claimName) =>
        $"http://ed-fi.org/identity/claims/ed-fi/{claimName}";

    private static JsonObject LoadEmbeddedClaims(string standardFolder = "ds52")
    {
        Assembly assembly = typeof(ClaimsProvider).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Claims.Standards.{standardFolder}.Claims.json";

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
