// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Parity;

/// <summary>
/// Builds structurally valid sample parity rows that each invariant fixture mutates to introduce
/// exactly one violation. The valid baseline itself must satisfy every rule. The synthetic
/// canonical no-profile id set is {"NoProfileSample"}.
/// </summary>
internal static class ParityInvariantSamples
{
    public static readonly string[] CanonicalNoProfileIds = ["NoProfileSample"];

    public static ParityScenario BothApi() =>
        new()
        {
            Id = "Api/Sample/Behavior",
            Layer = ParityLayer.Api,
            BehavioralContract = "round-trips a resource over HTTP on both engines",
            SharedEntryPoint = "SampleApiScenario.It_behaves",
            Boundary = ProductionBoundary.HttpPipeline,
            PgsqlLocations = [new("Given_Postgresql_Sample.cs", "Given_Postgresql_Sample", ["It_behaves"])],
            MssqlLocations = [new("Given_Mssql_Sample.cs", "Given_Mssql_Sample", ["It_behaves"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Covered,
            Classification = ParityClassification.Both,
        };

    public static ParityScenario KnownGapNoProfile() =>
        new()
        {
            Id = "NoProfileSample",
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "no-profile family owed a SQL Server twin",
            SharedEntryPoint = "NoProfileSampleScenarios",
            Boundary = ProductionBoundary.NoProfilePersister,
            PgsqlLocations = [new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", ["It_persists"])],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
            Classification = ParityClassification.KnownGap,
            MssqlGapOwner = "DMS-1285",
        };

    public static ParityScenario SupportingSmoke(string id = "NoProfileSample/AuthoritativeBreadth") =>
        new()
        {
            Id = id,
            Layer = ParityLayer.NoProfile,
            BehavioralContract = "authoritative breadth on the same no-profile boundary",
            Boundary = ProductionBoundary.NoProfilePersister,
            PgsqlLocations =
            [
                new("PostgresqlSampleSmoke.cs", "Given_A_Postgresql_Sample_Smoke", ["It_persists"]),
            ],
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Mapped,
            Classification = ParityClassification.SupportingSmoke,
            CoveredByScenarioId = "NoProfileSample",
        };

    public static ParityScenario NaProfile() =>
        new()
        {
            Id = "ProfileSample/UnitOnlyVariant",
            Layer = ParityLayer.Profile,
            BehavioralContract = "provider-independent creatability rejection validated at unit level",
            Boundary = ProductionBoundary.ProfileMergeSynthesizer,
            UnitLocations =
            [
                new("SampleSynthesizerTests.cs", "Given_Sample_Unit_Fixture", ["It_returns_a_rejection"]),
            ],
            PgsqlCoverage = EngineCoverage.NotApplicable,
            MssqlCoverage = EngineCoverage.NotApplicable,
            Classification = ParityClassification.Na,
            ProviderSpecificEntryPointRationale =
                "provider-independent unit-level validation; the unit fixture is the effective entry point",
        };

    public static List<ParityScenario> Valid() =>
        [BothApi(), KnownGapNoProfile(), SupportingSmoke(), NaProfile()];

    public static IReadOnlyList<string> Validate(List<ParityScenario> scenarios) =>
        ParityCatalogInvariants.Validate(scenarios, CanonicalNoProfileIds);
}

[TestFixture]
public class Given_A_Structurally_Valid_Parity_Catalog
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup() => _violations = ParityInvariantSamples.Validate(ParityInvariantSamples.Valid());

    [Test]
    public void It_reports_no_violations() => _violations.Should().BeEmpty();
}

[TestFixture]
public class Given_A_Parity_Catalog_With_Duplicate_Scenario_Ids
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios.Add(ParityInvariantSamples.BothApi());
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_duplicate_id() =>
        _violations.Should().Contain(v => v.Contains("Api/Sample/Behavior", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Parity_Catalog_With_A_Blank_Scenario_Id
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[0] = scenarios[0] with { Id = "   " };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_a_blank_id_violation() => _violations.Should().NotBeEmpty();
}

[TestFixture]
public class Given_A_Known_Gap_Without_A_Sql_Server_Owner
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with { MssqlGapOwner = null };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_sql_server_owner() =>
        _violations
            .Should()
            .Contain(v => v.Contains("SQL Server gap requires an owning ticket", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_With_A_Postgresql_Gap_But_No_Owner
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        // A supporting smoke whose non-covered engine is a Gap (rather than Mapped) must have an owner.
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[2] = scenarios[2] with { MssqlCoverage = EngineCoverage.Gap };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_sql_server_owner_on_a_gap() =>
        _violations
            .Should()
            .Contain(v => v.Contains("SQL Server gap requires an owning ticket", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Non_Gap_Engine_With_An_Owner
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with { PgsqlGapOwner = "DMS-1023" };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_engine_owner() =>
        _violations
            .Should()
            .Contain(v => v.Contains("PostgreSQL owner is only valid", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Known_Gap_Whose_Sql_Server_Side_Is_Marked_Covered
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with
        {
            MssqlCoverage = EngineCoverage.Covered,
            MssqlLocations = [new("MssqlSample.cs", "Given_A_Mssql_Sample", ["It_persists"])],
            MssqlGapOwner = null,
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_that_a_known_gap_cannot_be_covered_on_sql_server() =>
        _violations.Should().Contain(v => v.Contains("MssqlCoverage=Gap", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Gap_Owner_On_A_Non_Known_Gap_Row
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[0] = scenarios[0] with { MssqlGapOwner = "DMS-1285" };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_gap_owner() =>
        _violations
            .Should()
            .Contain(v => v.Contains("SQL Server owner is only valid", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_Without_A_Covered_By_Scenario
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[2] = scenarios[2] with { CoveredByScenarioId = null };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_covered_by_reference() =>
        _violations
            .Should()
            .Contain(v => v.Contains("requires a CoveredByScenarioId", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_Deferring_To_A_Non_Canonical_Same_Boundary_Variant
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // A same-layer, same-boundary variant of the canonical family — but not itself a canonical id.
        var variant = ParityInvariantSamples.KnownGapNoProfile() with
        {
            Id = "NoProfileSample/SomeVariant",
        };
        scenarios.Add(variant);
        scenarios[2] = scenarios[2] with { CoveredByScenarioId = variant.Id };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_that_the_deferral_target_must_be_an_exact_canonical_id() =>
        _violations
            .Should()
            .Contain(v => v.Contains("exact canonical no-profile id", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_That_Is_Not_One_Covered_One_Mapped
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // Covered on both engines instead of Covered + Mapped.
        scenarios[2] = scenarios[2] with
        {
            MssqlCoverage = EngineCoverage.Covered,
            MssqlLocations = [new("MssqlSampleSmoke.cs", "Given_A_Mssql_Sample_Smoke", ["It_persists"])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_coverage_shape_violation() =>
        _violations
            .Should()
            .Contain(v =>
                v.Contains("Covered on one engine and Mapped on the other", StringComparison.Ordinal)
            );
}

[TestFixture]
public class Given_A_Dialect_Difference_Without_A_Rationale
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[0] = scenarios[0] with { DialectDifference = new DialectDifference("caps differ", "") };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_unjustified_dialect_difference() => _violations.Should().NotBeEmpty();
}

[TestFixture]
public class Given_A_Both_Row_Missing_A_Sql_Server_Location
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[0] = scenarios[0] with { MssqlLocations = [] };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_incomplete_both_row() =>
        _violations.Should().Contain(v => v.Contains("Api/Sample/Behavior", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Covered_Location_With_A_Blank_Test_Method
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with
        {
            PgsqlLocations = [new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", [" "])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_incomplete_location() =>
        _violations
            .Should()
            .Contain(v => v.Contains("at least one non-blank test method", StringComparison.Ordinal));
}

[TestFixture]
public class Given_An_Na_Row_With_Concrete_Engine_Coverage
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[3] = scenarios[3] with
        {
            PgsqlCoverage = EngineCoverage.Covered,
            PgsqlLocations = [new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", ["It_persists"])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_that_an_na_row_must_be_not_applicable_on_both_engines() =>
        _violations.Should().Contain(v => v.Contains("UnitOnlyVariant", StringComparison.Ordinal));
}

[TestFixture]
public class Given_An_Na_Row_Without_A_Unit_Location
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[3] = scenarios[3] with { UnitLocations = [] };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_unit_location() =>
        _violations
            .Should()
            .Contain(v => v.Contains("requires at least one Unit location", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Unit_Location_On_A_Non_Na_Row
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[0] = scenarios[0] with
        {
            UnitLocations =
            [
                new("SampleSynthesizerTests.cs", "Given_Sample_Unit_Fixture", ["It_returns_a_rejection"]),
            ],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_unit_location() =>
        _violations
            .Should()
            .Contain(v => v.Contains("Unit locations are only valid on an Na row", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Gap_Engine_That_Still_Names_A_Location
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with
        {
            MssqlLocations = [new("MssqlSample.cs", "Given_A_Mssql_Sample", ["It_persists"])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_location_on_a_non_covered_engine() =>
        _violations
            .Should()
            .Contain(v => v.Contains("only Covered may name a location", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Not_Applicable_Row_With_A_Provider_Location
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[3] = scenarios[3] with
        {
            PgsqlLocations = [new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", ["It_persists"])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_provider_location_on_a_not_applicable_engine() =>
        _violations
            .Should()
            .Contain(v => v.Contains("only Covered may name a location", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Mapped_Engine_That_Still_Names_A_Location
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[2] = scenarios[2] with
        {
            MssqlLocations = [new("MssqlSampleSmoke.cs", "Given_A_Mssql_Sample_Smoke", ["It_persists"])],
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_location_on_a_mapped_engine() =>
        _violations
            .Should()
            .Contain(v => v.Contains("only Covered may name a location", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Mapped_Engine_On_A_Non_Supporting_Row
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[1] = scenarios[1] with { PgsqlCoverage = EngineCoverage.Mapped, PgsqlLocations = [] };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_mapped_coverage_outside_a_supporting_smoke() =>
        _violations
            .Should()
            .Contain(v =>
                v.Contains("Mapped is only valid on the deferred engine", StringComparison.Ordinal)
            );
}

[TestFixture]
public class Given_A_Row_Without_A_Direct_Inherited_Or_Provider_Specific_Entry_Point
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // Remove the direct shared entry point without recording a provider-specific rationale, so the row no
        // longer resolves to any effective entry point.
        scenarios[0] = scenarios[0] with
        {
            SharedEntryPoint = "",
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_that_a_provider_specific_rationale_is_required() =>
        _violations
            .Should()
            .Contain(v =>
                v.Contains("requires a", StringComparison.Ordinal)
                && v.Contains("ProviderSpecificEntryPointRationale", StringComparison.Ordinal)
            );
}

[TestFixture]
public class Given_A_Direct_Row_That_Also_Declares_A_Provider_Specific_Rationale
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // A direct row must not also carry a provider-specific rationale, or the resolution mode is ambiguous.
        scenarios[1] = scenarios[1] with
        {
            ProviderSpecificEntryPointRationale = "stray rationale on a direct row",
        };
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_that_the_rationale_is_only_valid_when_provider_specific() =>
        _violations
            .Should()
            .Contain(v =>
                v.Contains("only valid when the effective entry point is", StringComparison.Ordinal)
            );
}

[TestFixture]
public class Given_A_Duplicate_Inheritance_Target_Scenario_Id
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        // Duplicate the canonical/covered-by target the supporting-smoke row inherits from. Effective-entry-point
        // resolution must not throw on the ambiguous inheritance lookup; Validate still returns the duplicate-id
        // violation. (A duplicated Direct row is not an inheritance target, so it never exercises this path.)
        var scenarios = ParityInvariantSamples.Valid();
        scenarios.Add(ParityInvariantSamples.KnownGapNoProfile());
        _violations = ParityInvariantSamples.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_duplicate_id_without_throwing() =>
        _violations.Should().Contain(v => v.Contains("NoProfileSample", StringComparison.Ordinal));
}
