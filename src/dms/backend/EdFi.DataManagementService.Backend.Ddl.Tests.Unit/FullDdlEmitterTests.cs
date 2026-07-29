// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_JoinSegments_With_All_Segments_Having_Trailing_Newlines
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;\n", "CREATE TABLE b;\n", "INSERT 1;\n");
    }

    [Test]
    public void It_should_concatenate_without_extra_newlines()
    {
        _result.Should().Be("CREATE TABLE a;\nCREATE TABLE b;\nINSERT 1;\n");
    }
}

[TestFixture]
public class Given_JoinSegments_With_Missing_Trailing_Newlines
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;", "CREATE TABLE b;", "INSERT 1;");
    }

    [Test]
    public void It_should_insert_newline_boundaries_between_segments()
    {
        _result.Should().Be("CREATE TABLE a;\nCREATE TABLE b;\nINSERT 1;");
    }

    [Test]
    public void It_should_not_have_consecutive_statements_on_same_line()
    {
        _result.Should().NotContain(";C");
    }
}

[TestFixture]
public class Given_JoinSegments_With_Empty_Segments
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;\n", "", "INSERT 1;\n");
    }

    [Test]
    public void It_should_skip_empty_segments()
    {
        _result.Should().Be("CREATE TABLE a;\nINSERT 1;\n");
    }
}

[TestFixture]
public class Given_JoinSegments_With_All_Empty_Segments
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("", "", "");
    }

    [Test]
    public void It_should_return_empty_string()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_JoinSegments_With_Single_Segment
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("SELECT 1;");
    }

    [Test]
    public void It_should_return_segment_unchanged()
    {
        _result.Should().Be("SELECT 1;");
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_FullDdlEmitter_With_Bounded_Preflight_Guards(SqlDialect dialect)
{
    private string _sql = default!;

    [OneTimeSetUp]
    public void Setup()
    {
        var effectiveSchemaSet = SmallFixtureEffectiveSchemaSetLoader.Load("minimal");
        (_, _sql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect, strict: false);
    }

    [Test]
    public void It_should_emit_guard_reads_before_the_first_mutating_statement()
    {
        var hashGuard = _sql.IndexOf("Preflight: validate EffectiveSchema", StringComparison.Ordinal);
        var singletonGuard = _sql.IndexOf("Preflight: protect completed", StringComparison.Ordinal);
        var legacyGuard = _sql.IndexOf("Preflight: reject known legacy", StringComparison.Ordinal);
        var firstMutation =
            dialect == SqlDialect.Pgsql
                ? _sql.IndexOf("CREATE SCHEMA", StringComparison.Ordinal)
                : _sql.IndexOf("CREATE SCHEMA [dms]", StringComparison.Ordinal);

        hashGuard.Should().BeGreaterOrEqualTo(0);
        singletonGuard.Should().BeGreaterThan(hashGuard);
        legacyGuard.Should().BeGreaterThan(singletonGuard);
        firstMutation.Should().BeGreaterThan(legacyGuard);
    }

    [Test]
    public void It_should_emit_unique_full_ddl_phase_numbers_in_order()
    {
        var phaseNumbers = Regex
            .Matches(_sql, "^-- Phase (?<phase>[0-9]+):", RegexOptions.Multiline)
            .Select(match => int.Parse(match.Groups["phase"].Value))
            .ToList();

        phaseNumbers.Should().NotBeEmpty();
        phaseNumbers.Should().OnlyHaveUniqueItems();
        phaseNumbers.Should().BeInAscendingOrder();
    }

    [Test]
    public void It_should_emit_seed_initialization_as_the_final_full_ddl_phase()
    {
        var seedPhase = _sql.IndexOf(
            "Phase 10: Seed Data (insert-if-missing + validation)",
            StringComparison.Ordinal
        );
        var priorDdlPhase =
            dialect == SqlDialect.Pgsql
                ? _sql.IndexOf("Phase 9: Security and Grants", StringComparison.Ordinal)
                : _sql.IndexOf("Phase 8: Triggers", StringComparison.Ordinal);

        priorDdlPhase.Should().BeGreaterOrEqualTo(0);
        seedPhase.Should().BeGreaterThan(priorDdlPhase);
    }
}
