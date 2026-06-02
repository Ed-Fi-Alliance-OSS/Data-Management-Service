// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespacePrefixParameterizationFactory
{
    [Test]
    public void It_creates_a_pgsql_array_parameterization_carrying_one_parameter_with_each_prefix_already_carrying_a_trailing_percent()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"],
            "namespacePrefixes"
        );

        parameterization.Kind.Should().Be(NamespacePrefixParameterizationKind.PgsqlArray);
        parameterization.BaseParameterName.Should().Be("namespacePrefixes");
        parameterization.LikePatternsInOrder.Should().Equal("uri://ed-fi.org/%", "uri://gbisd.edu/%");
        parameterization.ParameterNamesInOrder.Should().Equal("namespacePrefixes");
    }

    [Test]
    public void It_creates_a_mssql_scalar_parameterization_with_one_indexed_parameter_per_prefix()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/", "uri://acme.test/"],
            "namespacePrefixes"
        );

        parameterization.Kind.Should().Be(NamespacePrefixParameterizationKind.MssqlScalar);
        parameterization
            .LikePatternsInOrder.Should()
            .Equal("uri://acme.test/%", "uri://ed-fi.org/%", "uri://gbisd.edu/%");
        parameterization
            .ParameterNamesInOrder.Should()
            .Equal("namespacePrefixes_0", "namespacePrefixes_1", "namespacePrefixes_2");
    }

    [Test]
    public void It_deduplicates_and_orders_prefixes_before_appending_the_trailing_percent()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://b.org/", "uri://a.org/", "uri://b.org/"],
            "namespacePrefixes"
        );

        parameterization.LikePatternsInOrder.Should().Equal("uri://a.org/%", "uri://b.org/%");
    }

    [Test]
    public void It_carries_raw_configured_prefixes_separately_from_the_escaped_like_patterns()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            ["uri://b_org/", "uri://a.org/"],
            "namespacePrefixes"
        );

        // Raw prefixes are deduped + ordinal-sorted but neither escaped nor wildcard-suffixed; the
        // escaped LIKE patterns line up 1:1 for SQL binding.
        parameterization.ConfiguredPrefixesInOrder.Should().Equal("uri://a.org/", "uri://b_org/");
        parameterization.LikePatternsInOrder.Should().Equal("uri://a.org/%", "uri://b\\_org/%");
    }

    [Test]
    public void It_escapes_like_metacharacters_in_pgsql_prefixes_keeping_the_trailing_percent_a_wildcard()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://a_b/", "uri://c%d/", "uri://e\\f/"],
            "namespacePrefixes"
        );

        // Underscore, percent, and backslash in the prefix are escaped with a backslash; the trailing
        // wildcard percent is appended after escaping so it remains a wildcard.
        parameterization
            .LikePatternsInOrder.Should()
            .Equal("uri://a\\_b/%", "uri://c\\%d/%", "uri://e\\\\f/%");
    }

    [Test]
    public void It_escapes_square_brackets_only_for_mssql()
    {
        var mssql = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            ["uri://a[b]/"],
            "namespacePrefixes"
        );
        var pgsql = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://a[b]/"],
            "namespacePrefixes"
        );

        // SQL Server LIKE treats '[' as a character-class opener, so it must be escaped; PostgreSQL
        // does not, so the bracket is left untouched there.
        mssql.LikePatternsInOrder.Should().Equal("uri://a\\[b]/%");
        pgsql.LikePatternsInOrder.Should().Equal("uri://a[b]/%");
    }

    [Test]
    public void It_throws_when_mssql_prefix_count_reaches_2000()
    {
        var prefixes = Enumerable
            .Range(0, NamespacePrefixLimitExceededException.MssqlScalarParameterLimit)
            .Select(static index => $"uri://prefix-{index:D5}/")
            .ToArray();

        var act = () =>
            NamespacePrefixParameterizationFactory.Create(SqlDialect.Mssql, prefixes, "namespacePrefixes");

        act.Should()
            .Throw<NamespacePrefixLimitExceededException>()
            .Which.PrefixCount.Should()
            .Be(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit);
    }

    [Test]
    public void It_allows_mssql_prefix_count_of_1999_just_below_the_limit()
    {
        var prefixes = Enumerable
            .Range(0, NamespacePrefixLimitExceededException.MssqlScalarParameterLimit - 1)
            .Select(static index => $"uri://prefix-{index:D5}/")
            .ToArray();

        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            prefixes,
            "namespacePrefixes"
        );

        parameterization
            .LikePatternsInOrder.Should()
            .HaveCount(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit - 1);
        parameterization
            .ParameterNamesInOrder.Should()
            .HaveCount(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit - 1);
    }

    [Test]
    public void It_does_not_apply_the_prefix_cap_to_pgsql()
    {
        var prefixes = Enumerable
            .Range(0, NamespacePrefixLimitExceededException.MssqlScalarParameterLimit + 1)
            .Select(static index => $"uri://prefix-{index:D5}/")
            .ToArray();

        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            prefixes,
            "namespacePrefixes"
        );

        parameterization.Kind.Should().Be(NamespacePrefixParameterizationKind.PgsqlArray);
        parameterization
            .LikePatternsInOrder.Should()
            .HaveCount(NamespacePrefixLimitExceededException.MssqlScalarParameterLimit + 1);
        parameterization.ParameterNamesInOrder.Should().HaveCount(1);
    }

    [Test]
    public void It_matches_a_stored_value_that_starts_with_a_configured_prefix()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/", "uri://gbisd.edu/"],
            "namespacePrefixes"
        );

        parameterization.MatchesAnyPrefix("uri://gbisd.edu/SchoolTypeDescriptor").Should().BeTrue();
    }

    [Test]
    public void It_does_not_match_a_stored_value_that_starts_with_no_configured_prefix()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/"],
            "namespacePrefixes"
        );

        parameterization.MatchesAnyPrefix("uri://other.org/SchoolTypeDescriptor").Should().BeFalse();
    }

    [Test]
    public void It_matches_case_sensitively_for_pgsql_mirroring_the_case_sensitive_like()
    {
        // PostgreSQL LIKE is case-sensitive under the Namespace column's deterministic default
        // collation, so an in-memory match must reject a value that only case-differs from a prefix.
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/"],
            "namespacePrefixes"
        );

        parameterization.MatchesAnyPrefix("uri://ED-FI.ORG/SchoolTypeDescriptor").Should().BeFalse();
    }

    [Test]
    public void It_matches_case_insensitively_for_mssql_mirroring_the_case_insensitive_like()
    {
        // SQL Server LIKE is case-insensitive under the Namespace column's default collation, so an
        // in-memory match must accept a value that only case-differs from a prefix.
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            ["uri://ed-fi.org/"],
            "namespacePrefixes"
        );

        parameterization.MatchesAnyPrefix("uri://ED-FI.ORG/SchoolTypeDescriptor").Should().BeTrue();
    }

    [Test]
    public void It_treats_like_metacharacters_in_a_prefix_as_literals_mirroring_the_escaped_like_pattern()
    {
        // The raw prefix contains an underscore. The SQL path escapes it so it matches literally; the
        // in-memory starts-with over the raw prefix must do the same, accepting only a literal
        // underscore and rejecting an arbitrary single character in that position.
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://a_b/"],
            "namespacePrefixes"
        );

        parameterization.MatchesAnyPrefix("uri://a_b/Descriptor").Should().BeTrue();
        parameterization.MatchesAnyPrefix("uri://aXb/Descriptor").Should().BeFalse();
    }

    [Test]
    public void It_throws_when_the_stored_value_is_null()
    {
        var parameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            ["uri://ed-fi.org/"],
            "namespacePrefixes"
        );

        var act = () => parameterization.MatchesAnyPrefix(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_throws_when_prefixes_are_empty()
    {
        var act = () =>
            NamespacePrefixParameterizationFactory.Create(SqlDialect.Pgsql, [], "namespacePrefixes");

        act.Should().Throw<ArgumentException>().WithParameterName("namespacePrefixes");
    }

    [Test]
    public void It_throws_when_any_prefix_is_null_or_empty()
    {
        var actNull = () =>
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/", null!],
                "namespacePrefixes"
            );
        var actEmpty = () =>
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/", ""],
                "namespacePrefixes"
            );

        actNull.Should().Throw<ArgumentException>().WithParameterName("namespacePrefixes");
        actEmpty.Should().Throw<ArgumentException>().WithParameterName("namespacePrefixes");
    }
}
