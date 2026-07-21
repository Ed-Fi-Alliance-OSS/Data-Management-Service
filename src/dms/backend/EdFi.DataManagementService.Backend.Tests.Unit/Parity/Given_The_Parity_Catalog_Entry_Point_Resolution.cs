// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Reflection;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Parity;

/// <summary>
/// Reflection meta-tests proving the catalog's effective entry points name real members: every unit-test
/// location owned by this Backend.Tests.Unit assembly resolves to a declared <c>[Test]</c> method here (the
/// provider-independent Na merge-synthesizer entry points; Core.Tests.Unit-owned unit locations are resolved by
/// the Core meta-test against the Core assembly), and every Direct/Inherited Backend.Tests.Common shared entry point
/// resolves every named type and member in the common assembly. The API shared entry points are validated
/// separately by <c>Given_The_Api_Parity_Catalog_Resolution</c> against the API assembly, and the backend
/// provider locations by the per-engine backend meta-tests. A filesystem pass additionally proves every recorded
/// source file exists, giving the diagnostic <c>File</c> field teeth. Pure reflection plus a source-tree scan —
/// no database.
/// </summary>
[TestFixture]
public class Given_The_Parity_Catalog_Entry_Point_Resolution
{
    [Test]
    public void It_resolves_every_unit_location_to_a_declared_test_method() =>
        ParityCatalogResolution
            .ResolveUnitLocations(Assembly.GetExecutingAssembly(), UnitTestAssembly.BackendTestsUnit)
            .Should()
            .BeEmpty();

    [Test]
    public void It_resolves_every_common_shared_entry_point_to_a_real_type() =>
        ParityCatalogResolution
            .ResolveCommonSharedEntryPoints(typeof(ParityScenarioCatalog).Assembly)
            .Should()
            .BeEmpty();

    [Test]
    public void It_resolves_every_catalog_location_file_to_a_real_source_file()
    {
        string repositoryRoot = FixturePathResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        string sourceSearchRoot = Path.Combine(repositoryRoot, "src", "dms");

        ParityCatalogResolution.ResolveSourceFileLocations(sourceSearchRoot).Should().BeEmpty();
    }
}

/// <summary>
/// A separator-only composite such as <c>"+"</c> is non-whitespace, so it resolves as a Direct entry
/// point, yet a <c>RemoveEmptyEntries</c> split parses it to zero parts — the reflection resolver would
/// verify zero members and report nothing. The resolver must reject a value that parses to no
/// <c>Type.Method</c> component while a valid multi-part composite still resolves clean.
/// </summary>
[TestFixture]
public class Given_A_Separator_Only_Shared_Entry_Point_Value
{
    private IReadOnlyList<string> _separatorOnlyViolations = null!;
    private IReadOnlyList<string> _validCompositeViolations = null!;

    [SetUp]
    public void Setup()
    {
        Assembly commonAssembly = typeof(ParityScenarioCatalog).Assembly;

        _separatorOnlyViolations = ParityCatalogResolution.ResolveSharedEntryPointValue(
            "SyntheticRow",
            "+",
            commonAssembly
        );

        _validCompositeViolations = ParityCatalogResolution.ResolveSharedEntryPointValue(
            "SyntheticRow",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged",
            commonAssembly
        );
    }

    [Test]
    public void It_rejects_the_value_that_parses_to_no_component() =>
        _separatorOnlyViolations
            .Should()
            .ContainSingle(v => v.Contains("parses to no Type.Method component", StringComparison.Ordinal));

    [Test]
    public void It_still_resolves_a_valid_multi_part_composite() =>
        _validCompositeViolations.Should().BeEmpty();
}

/// <summary>
/// Belonging to a canonical family (a shared id prefix) never inherits a contract. A shared assertion pins one
/// mechanic, and sharing a production boundary with the family does not imply running the family's assertion
/// helpers, so a same-boundary variant that records neither its own <c>SharedEntryPoint</c> nor a
/// <c>CoveredByScenarioId</c> deferral resolves unresolved (null) rather than silently inheriting the family
/// contract by boundary alone.
/// </summary>
[TestFixture]
public class Given_A_Same_Boundary_Variant_Without_An_Explicit_Entry_Point
{
    private ParityScenario _family = null!;
    private ParityScenario _variant = null!;

    [SetUp]
    public void Setup()
    {
        // A real canonical id so CanonicalIdOf recognizes the "/variant" row as belonging to this family.
        _family = new ParityScenario
        {
            Id = "NoProfilePostAsUpdate",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "canonical family at the persister boundary",
            SharedEntryPoint = "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace",
            Boundary = ProductionBoundary.NoProfilePersister,
            PgsqlLocations = [new("Family.cs", "Given_A_Family", ["It_updates"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };
        // Same boundary as its family, a recorded location, but no SharedEntryPoint, no CoveredByScenarioId, and
        // no provider-specific rationale — exactly the shape that previously inherited the family contract.
        _variant = new ParityScenario
        {
            Id = "NoProfilePostAsUpdate/SameBoundaryVariant",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "variant sharing its family's boundary but declaring no entry point",
            Boundary = ProductionBoundary.NoProfilePersister,
            PgsqlLocations = [new("Sample.cs", "Given_A_Sample", ["It_updates"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };
    }

    [Test]
    public void It_does_not_inherit_the_family_shared_contract() =>
        ParityEntryPointResolution
            .ResolveEffectiveEntryPoint(_variant, [_family, _variant])
            .Should()
            .BeNull();

    [Test]
    public void It_leaves_the_family_itself_resolving_to_its_own_direct_contract() =>
        ParityEntryPointResolution
            .ResolveEffectiveEntryPoint(_family, [_family, _variant])!
            .Kind.Should()
            .Be(EntryPointKind.Direct);
}

/// <summary>
/// Inheritance survives only through an explicit <c>CoveredByScenarioId</c> deferral (the supporting-smoke path):
/// a row that defers to a same-boundary canonical scenario with a shared contract resolves Inherited, carrying
/// that scenario's shared value and id.
/// </summary>
[TestFixture]
public class Given_A_Row_That_Defers_Through_CoveredByScenarioId
{
    private EffectiveEntryPoint _resolved = null!;

    [SetUp]
    public void Setup()
    {
        var covered = new ParityScenario
        {
            Id = "NoProfileGuardedNoOp",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "canonical family with a shared no-op contract",
            SharedEntryPoint = "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome",
            Boundary = ProductionBoundary.GuardedNoOp,
            PgsqlLocations = [new("Family.cs", "Given_A_Family", ["It_no_ops"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };
        var smoke = new ParityScenario
        {
            Id = "NoProfile/AuthoritativeSmoke/Sample/RepeatPutNoOp",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "supporting smoke deferring its mechanic to the canonical no-op contract",
            Boundary = ProductionBoundary.GuardedNoOp,
            PgsqlLocations = [new("Smoke.cs", "Given_A_Smoke", ["It_repeats"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Mapped,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = "NoProfileGuardedNoOp",
        };

        _resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(smoke, [covered, smoke])!;
    }

    [Test]
    public void It_resolves_inherited() => _resolved.Kind.Should().Be(EntryPointKind.Inherited);

    [Test]
    public void It_carries_the_covered_shared_value_and_source() =>
        (_resolved.SharedValue, _resolved.InheritedFromScenarioId)
            .Should()
            .Be(("NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome", "NoProfileGuardedNoOp"));
}

[TestFixture]
public class Given_A_Row_That_Defers_To_An_Exact_Variant_Of_Its_Canonical_Family
{
    private EffectiveEntryPoint _resolved = null!;

    [SetUp]
    public void Setup()
    {
        var coveredVariant = new ParityScenario
        {
            Id = "NoProfileFamily/PostVariant",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "the exact variant contract the smoke's test actually runs",
            SharedEntryPoint = "FamilyScenarios.AssertPostOutcome + FamilyScenarios.AssertRowsetUnchanged",
            Boundary = ProductionBoundary.GuardedNoOp,
            PgsqlLocations = [new("Variant.cs", "Given_A_Variant", ["It_no_ops"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };
        var smoke = new ParityScenario
        {
            Id = "NoProfile/AuthoritativeSmoke/Sample/RepeatPostNoOp",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "supporting smoke deferring its mechanic to the precise variant contract",
            Boundary = ProductionBoundary.GuardedNoOp,
            PgsqlLocations = [new("Smoke.cs", "Given_A_Smoke", ["It_repeats"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Mapped,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = "NoProfileFamily/PostVariant",
        };

        _resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(smoke, [coveredVariant, smoke])!;
    }

    [Test]
    public void It_resolves_inherited() => _resolved.Kind.Should().Be(EntryPointKind.Inherited);

    [Test]
    public void It_carries_the_variant_shared_value_and_source() =>
        (_resolved.SharedValue, _resolved.InheritedFromScenarioId)
            .Should()
            .Be(
                (
                    "FamilyScenarios.AssertPostOutcome + FamilyScenarios.AssertRowsetUnchanged",
                    "NoProfileFamily/PostVariant"
                )
            );
}

/// <summary>
/// Pins the exact effective reusable-contract member set for every no-profile family variant that previously
/// resolved by canonical-family inheritance. This is the 31-row audit guard: each variant must now resolve
/// Direct to precisely the helper(s) its adapter runs, so a variant cannot silently drift back to advertising a
/// family contract it does not execute (nor advertise a superset it does not run).
/// </summary>
[TestFixture]
public class Given_The_Parity_Catalog_Family_Variants_Effective_Entry_Points
{
    private static readonly (string Id, string ExpectedSharedValue)[] ExpectedVariantEntryPoints =
    [
        ("NoProfileFullSurfaceCreate/InsertSuccess", "NoProfileCreateBaselineScenarios.AssertInsertSuccess"),
        (
            "NoProfileFullSurfaceCreate/RootAndNestedCollectionStableIds",
            "NoProfileCreateBaselineScenarios.AssertRootAndNestedCollectionRows"
        ),
        (
            "NoProfileFullSurfaceCreate/RootAndCollectionExtensionAndExtensionChild",
            "NoProfileCreateBaselineScenarios.AssertRootAndCollectionExtensionAndExtensionChildRows"
        ),
        (
            "NoProfileChangedPutOmissionSemantics/ClearedInlinedColumn",
            "NoProfileUpdateSemanticsScenarios.AssertClearedOmittedInlinedColumn"
        ),
        (
            "NoProfileChangedPutOmissionSemantics/DeletedAlignedExtensionScope",
            "NoProfileUpdateSemanticsScenarios.AssertDeletedOmittedAlignedExtensionScope"
        ),
        (
            "NoProfileChangedPutOmissionSemantics/ContentVersionBump",
            "NoProfileUpdateSemanticsScenarios.AssertUpdateSuccessAndContentVersionBump"
        ),
        (
            "NoProfileChangedPutOmissionSemantics/DeletedBaseCollectionRows",
            "NoProfileMultiBatchCollectionScenarios.AssertMultiBatchDeleteUpdateReducedToRetainedRow"
        ),
        (
            "NoProfileChangedPutOmissionSemantics/DeletedAndReplacedChildCollectionRows",
            "NoProfilePostAsUpdateScenarios.AssertRetainedChildCollectionIdReuse"
        ),
        (
            "FullSurfaceCollectionReorder/OrdinalReuseStableIds",
            "NoProfileCollectionReorderScenarios.AssertReusesCollectionItemIdsWhileRecomputingOrdinals"
        ),
        (
            "FullSurfaceCollectionReorder/TwoRowSwapUnderSiblingUniqueness",
            "NoProfileCollectionReorderScenarios.AssertTwoRowSwapCommitsUnderSiblingUniqueness"
        ),
        (
            "FullSurfaceCollectionReorder/ContentVersionBump",
            "NoProfileCollectionReorderScenarios.AssertUpdateSuccessAndContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/Put",
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged"
        ),
        (
            "NoProfileGuardedNoOp/PostAsUpdate",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchanged"
        ),
        (
            "NoProfileGuardedNoOp/PutCurrentStateRefresh",
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertCurrentStateRefreshObservations + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/PostAsUpdateCurrentStateRefresh",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertCurrentStateRefreshObservations + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/PutAfterReorder",
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder"
        ),
        (
            "NoProfileGuardedNoOp/PostAsUpdateAfterReorder",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedAfterReorder"
        ),
        (
            "NoProfileGuardedNoOp/StalePut",
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/StalePostAsUpdate",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/PutCommitWindowRace",
            "NoProfileGuardedNoOpScenarios.AssertPutNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertCommitWindowFreshnessObservations + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileGuardedNoOp/PostAsUpdateCommitWindowRace",
            "NoProfileGuardedNoOpScenarios.AssertPostAsUpdateNoOpOutcome + NoProfileGuardedNoOpScenarios.AssertCommitWindowFreshnessObservations + NoProfileGuardedNoOpScenarios.AssertRowsetUnchangedExceptOneContentVersionBump"
        ),
        (
            "NoProfileMultiBatchCollection/Create",
            "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionCreatePersisted + NoProfileMultiBatchCollectionScenarios.AssertCreateBatchPartitions"
        ),
        (
            "NoProfileMultiBatchCollection/DeleteUpdate",
            "NoProfileMultiBatchCollectionScenarios.AssertMultiBatchDeleteUpdateReducedToRetainedRow + NoProfileMultiBatchCollectionScenarios.AssertDeleteBatchPartitions"
        ),
        (
            "NoProfileMultiBatchCollection/AlignedExtensionCreate",
            "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionAlignedExtensionCreatePersisted + NoProfileMultiBatchCollectionScenarios.AssertAlignedExtensionInsertBatchPartitions"
        ),
        (
            "NoProfileMultiBatchCollection/AuthoritativeParameterPressure",
            "NoProfileMultiBatchCollectionScenarios.AssertAuthoritativeLargeCollectionCreatePersisted + NoProfileMultiBatchCollectionScenarios.AssertParameterPressurePayload"
        ),
        (
            "NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions",
            "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionChangedDescriptorUpdatePersisted + NoProfileMultiBatchCollectionScenarios.AssertUpdateBatchPartitions"
        ),
        (
            "NoProfilePostAsUpdate/FocusedStableKey",
            "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace + NoProfilePostAsUpdateScenarios.AssertFocusedFullSurfaceStateApplied"
        ),
        (
            "NoProfilePostAsUpdate/CreateRaceConvertedToUpdate",
            "NoProfilePostAsUpdateScenarios.AssertStaleCreateConvertedToPostAsUpdate + NoProfilePostAsUpdateScenarios.AssertLastWriterStateApplied"
        ),
        (
            "NoProfilePostAsUpdate/AuthoritativeDs52SchoolYearType",
            "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace + NoProfilePostAsUpdateScenarios.AssertAuthoritativeSchoolYearTypeRowInPlace"
        ),
        (
            "NoProfilePostAsUpdate/AuthoritativeStudentAcademicRecord",
            "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace + NoProfilePostAsUpdateScenarios.AssertAuthoritativeRootAndExtensionInPlace"
        ),
        (
            "NoProfileRollbackSafety/CreateFailureAfterEarlyWrites",
            "NoProfileAtomicRollbackAssertions.AssertInjectedFailureAfterOrderedEarlyWrites + NoProfileAtomicRollbackAssertions.AssertFullSurfaceRollbackToPreState"
        ),
    ];

    [Test]
    public void It_pins_every_family_variant_to_its_direct_effective_entry_point()
    {
        List<string> mismatches = [];

        foreach ((string id, string expectedSharedValue) in ExpectedVariantEntryPoints)
        {
            ParityScenario? scenario = ParityScenarioCatalog.All.SingleOrDefault(s => s.Id == id);
            if (scenario is null)
            {
                mismatches.Add($"{id}: no catalog row with this id.");
                continue;
            }

            EffectiveEntryPoint? resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(scenario);
            if (
                resolved is null
                || resolved.Kind != EntryPointKind.Direct
                || !string.Equals(resolved.SharedValue, expectedSharedValue, StringComparison.Ordinal)
            )
            {
                mismatches.Add(
                    $"{id}: expected Direct '{expectedSharedValue}' but resolved "
                        + $"{resolved?.Kind.ToString() ?? "null"} '{resolved?.SharedValue}'."
                );
            }
        }

        mismatches.Should().BeEmpty();
    }

    [Test]
    public void It_audits_exactly_the_thirty_one_former_inheritance_variants() =>
        ExpectedVariantEntryPoints.Should().HaveCount(31);
}
