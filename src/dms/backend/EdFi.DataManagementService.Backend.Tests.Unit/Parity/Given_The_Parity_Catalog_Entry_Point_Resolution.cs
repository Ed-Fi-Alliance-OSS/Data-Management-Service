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
/// location resolves to a declared <c>[Test]</c> method in this Backend.Tests.Unit assembly (the provider-
/// independent Na synthesizer entry points), and every Direct/Inherited Backend.Tests.Common shared entry point
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
        ParityCatalogResolution.ResolveUnitLocations(Assembly.GetExecutingAssembly()).Should().BeEmpty();

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
/// A variant may deliberately pin a different production mechanic than its canonical family. Because a shared
/// assertion pins one mechanic, inheriting the family's contract across mechanics would certify assertions the
/// variant never runs, so effective-entry-point resolution must not inherit a family contract across boundaries.
/// </summary>
[TestFixture]
public class Given_A_Variant_At_A_Different_Boundary_Than_Its_Canonical_Family
{
    private EffectiveEntryPoint _resolved = null!;

    [SetUp]
    public void Setup()
    {
        // A real canonical id so CanonicalIdOf recognizes the "/variant" row as belonging to this family.
        var family = new ParityScenario
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
        var variant = new ParityScenario
        {
            Id = "NoProfilePostAsUpdate/RejectedAtADifferentBoundary",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "variant that pins a different mechanic than its family",
            Boundary = ProductionBoundary.IdentityStability,
            PgsqlLocations = [new("Sample.cs", "Given_A_Sample", ["It_rejects"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
            ProviderSpecificEntryPointRationale =
                "distinct-boundary variant; the recorded fixture is its entry point",
        };

        _resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(variant, [family, variant])!;
    }

    [Test]
    public void It_does_not_inherit_the_family_shared_contract() =>
        _resolved.Kind.Should().Be(EntryPointKind.ProviderSpecific);

    [Test]
    public void It_does_not_carry_the_family_shared_value() => _resolved.SharedValue.Should().BeNull();
}

/// <summary>
/// The complement: a variant that pins the same production mechanic as its canonical family still inherits the
/// family's shared contract, so the boundary guard narrows inheritance without disabling it.
/// </summary>
[TestFixture]
public class Given_A_Variant_At_The_Same_Boundary_As_Its_Canonical_Family
{
    private EffectiveEntryPoint _resolved = null!;

    [SetUp]
    public void Setup()
    {
        var family = new ParityScenario
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
        var variant = new ParityScenario
        {
            Id = "NoProfilePostAsUpdate/SameBoundaryVariant",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "variant pinning the same mechanic as its family",
            Boundary = ProductionBoundary.NoProfilePersister,
            PgsqlLocations = [new("Sample.cs", "Given_A_Sample", ["It_updates"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };

        _resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(variant, [family, variant])!;
    }

    [Test]
    public void It_inherits_the_family_shared_contract() =>
        _resolved.Kind.Should().Be(EntryPointKind.Inherited);

    [Test]
    public void It_carries_the_family_shared_value_and_source() =>
        (_resolved.SharedValue, _resolved.InheritedFromScenarioId)
            .Should()
            .Be(
                (
                    "NoProfilePostAsUpdateScenarios.AssertUpdatedExistingDocumentInPlace",
                    "NoProfilePostAsUpdate"
                )
            );
}
