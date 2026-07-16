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
/// exactly one violation. The valid baseline itself must satisfy every rule.
/// </summary>
internal static class ParityInvariantSamples
{
    public static ParityScenario BothApi() =>
        new()
        {
            Id = "Api/Sample/Behavior",
            Layer = ParityLayer.Api,
            BehavioralContract = "round-trips a resource over HTTP on both engines",
            Boundary = ProductionBoundary.HttpPipeline,
            Pgsql = new("Given_Postgresql_Sample.cs", "Given_Postgresql_Sample", ["It_behaves"]),
            Mssql = new("Given_Mssql_Sample.cs", "Given_Mssql_Sample", ["It_behaves"]),
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
            Boundary = ProductionBoundary.NoProfilePersister,
            Pgsql = new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", ["It_persists"]),
            Mssql = null,
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
            Pgsql = new("PostgresqlSampleSmoke.cs", "Given_A_Postgresql_Sample_Smoke", ["It_persists"]),
            Mssql = null,
            PgsqlCoverage = EngineCoverage.Covered,
            MssqlCoverage = EngineCoverage.Gap,
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
            Pgsql = null,
            Mssql = null,
            Unit = new("SampleSynthesizerTests.cs", "Given_Sample_Unit_Fixture", ["It_returns_a_rejection"]),
            PgsqlCoverage = EngineCoverage.NotApplicable,
            MssqlCoverage = EngineCoverage.NotApplicable,
            Classification = ParityClassification.Na,
        };

    public static List<ParityScenario> Valid() =>
        [BothApi(), KnownGapNoProfile(), SupportingSmoke(), NaProfile()];
}

[TestFixture]
public class Given_A_Structurally_Valid_Parity_Catalog
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup() => _violations = ParityCatalogInvariants.Validate(ParityInvariantSamples.Valid());

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
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_sql_server_owner() =>
        _violations.Should().Contain(v => v.Contains("NoProfileSample", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Known_Gap_With_A_Postgresql_Gap_But_No_Postgresql_Owner
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // Both engines a gap, but only the SQL Server side has an owner.
        scenarios[1] = scenarios[1] with
        {
            Pgsql = null,
            PgsqlCoverage = EngineCoverage.Gap,
            PgsqlGapOwner = null,
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_postgresql_owner() =>
        _violations
            .Should()
            .Contain(v => v.Contains("PostgreSQL gap requires an owning ticket", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Non_Gap_Engine_With_An_Owner
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // PostgreSQL is Covered, so it must not carry a gap owner.
        scenarios[1] = scenarios[1] with
        {
            PgsqlGapOwner = "DMS-1023",
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_engine_owner() =>
        _violations.Should().Contain(v => v.Contains("NoProfileSample", StringComparison.Ordinal));
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
            Mssql = new("MssqlSample.cs", "Given_A_Mssql_Sample", ["It_persists"]),
            MssqlGapOwner = null,
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_gap_owner() =>
        _violations.Should().Contain(v => v.Contains("Api/Sample/Behavior", StringComparison.Ordinal));
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
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_covered_by_reference() =>
        _violations.Should().Contain(v => v.Contains("AuthoritativeBreadth", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_Deferring_To_A_Missing_Scenario
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        scenarios[2] = scenarios[2] with { CoveredByScenarioId = "NoSuchScenario" };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_dangling_covered_by_reference() =>
        _violations.Should().Contain(v => v.Contains("NoSuchScenario", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_Deferring_To_A_Different_Boundary_In_The_Same_Layer
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        // Same NoProfile layer as its target, but a different production boundary.
        scenarios[2] = scenarios[2] with
        {
            Boundary = ProductionBoundary.GuardedNoOp,
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_cross_boundary_deferral() =>
        _violations.Should().Contain(v => v.Contains("same production boundary", StringComparison.Ordinal));
}

[TestFixture]
public class Given_A_Supporting_Smoke_Deferring_To_Another_Supporting_Smoke
{
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        var scenarios = ParityInvariantSamples.Valid();
        var secondSmoke = ParityInvariantSamples.SupportingSmoke("NoProfileSample/AuthoritativeBreadth2");
        scenarios.Add(secondSmoke);
        // Defer to the smoke instead of a canonical row (same layer and boundary).
        scenarios[2] = scenarios[2] with
        {
            CoveredByScenarioId = secondSmoke.Id,
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_non_canonical_deferral_target() =>
        _violations
            .Should()
            .Contain(v => v.Contains("must be a canonical scenario", StringComparison.Ordinal));
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
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
        scenarios[0] = scenarios[0] with { Mssql = null };
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
            Pgsql = new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", [" "]),
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
            Pgsql = new("PostgresqlSample.cs", "Given_A_Postgresql_Sample", ["It_persists"]),
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
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
        scenarios[3] = scenarios[3] with { Unit = null };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_missing_unit_location() =>
        _violations.Should().Contain(v => v.Contains("requires a Unit location", StringComparison.Ordinal));
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
            Unit = new("SampleSynthesizerTests.cs", "Given_Sample_Unit_Fixture", ["It_returns_a_rejection"]),
        };
        _violations = ParityCatalogInvariants.Validate(scenarios);
    }

    [Test]
    public void It_reports_the_stray_unit_location() =>
        _violations.Should().Contain(v => v.Contains("only valid on an Na row", StringComparison.Ordinal));
}
