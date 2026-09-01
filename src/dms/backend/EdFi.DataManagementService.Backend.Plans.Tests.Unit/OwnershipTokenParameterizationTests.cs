// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OwnershipTokenParameterizationFactory
{
    private const string BaseParameterName = "ownershipTokenIds";
    private const int Limit = OwnershipTokenLimitExceededException.OwnershipTokenLimit;

    private static IReadOnlyList<short> Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(static value => (short)value)];

    [Test]
    public void It_binds_one_array_parameter_for_postgresql()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [7, 3, 3, 11],
            BaseParameterName
        );

        parameterization.Kind.Should().Be(OwnershipTokenParameterizationKind.PgsqlArray);
        parameterization.ParameterNamesInOrder.Should().Equal(BaseParameterName);
        parameterization.TokensInOrder.Should().Equal((short)3, (short)7, (short)11);
    }

    [Test]
    public void It_binds_one_scalar_parameter_per_token_for_sql_server()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            [7, 3, 3, 11],
            BaseParameterName
        );

        parameterization.Kind.Should().Be(OwnershipTokenParameterizationKind.MssqlScalar);
        parameterization.TokensInOrder.Should().Equal((short)3, (short)7, (short)11);
        parameterization
            .ParameterNamesInOrder.Should()
            .Equal("ownershipTokenIds_0", "ownershipTokenIds_1", "ownershipTokenIds_2");
    }

    /// <summary>
    /// Deterministic binding order matters because the emitted SQL text is keyed by it: two requests with the
    /// same token set must produce the same statement so the engine can reuse the plan.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_normalizes_tokens_to_distinct_ascending(SqlDialect dialect)
    {
        var first = OwnershipTokenParameterizationFactory.Create(dialect, [11, 3, 7, 3], BaseParameterName);
        var second = OwnershipTokenParameterizationFactory.Create(dialect, [3, 7, 11, 11], BaseParameterName);

        first.TokensInOrder.Should().Equal(second.TokensInOrder);
        first.ParameterNamesInOrder.Should().Equal(second.ParameterNamesInOrder);
    }

    /// <summary>
    /// An empty token list is a valid parameterization, not a failure: the stored-row check still runs so the
    /// response can distinguish §2.14 from §2.13. This is where ownership deliberately differs from the
    /// namespace prefix parameterization, whose empty case is a §2.9 preflight denial.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_an_empty_token_list(SqlDialect dialect)
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(dialect, [], BaseParameterName);

        parameterization.TokensInOrder.Should().BeEmpty();
        parameterization.MatchesNoToken.Should().BeTrue();
    }

    [Test]
    public void It_binds_no_scalar_parameters_for_an_empty_sql_server_token_list()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            [],
            BaseParameterName
        );

        parameterization.ParameterNamesInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_still_binds_the_array_parameter_for_an_empty_postgresql_token_list()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [],
            BaseParameterName
        );

        parameterization.ParameterNamesInOrder.Should().Equal(BaseParameterName);
    }

    // ── the provider-independent cap ───────────────────────────────────

    /// <summary>
    /// 1,999 is the count CMS permits, so it must remain a working configuration on both providers.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_one_token_below_the_limit(SqlDialect dialect)
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            dialect,
            Tokens(Limit - 1),
            BaseParameterName
        );

        parameterization.TokensInOrder.Should().HaveCount(Limit - 1);
    }

    /// <summary>
    /// The cap is provider-independent: PostgreSQL binds one array parameter and has no engine limit at this
    /// size, but the limit is a defensive bound on the configuration rather than a dialect artifact.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_fails_closed_at_the_limit_on_every_provider(SqlDialect dialect)
    {
        Action act = () =>
            OwnershipTokenParameterizationFactory.Create(dialect, Tokens(Limit), BaseParameterName);

        act.Should()
            .Throw<OwnershipTokenLimitExceededException>()
            .Which.OwnershipTokenCount.Should()
            .Be(Limit);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_fails_closed_above_the_limit_on_every_provider(SqlDialect dialect)
    {
        Action act = () =>
            OwnershipTokenParameterizationFactory.Create(dialect, Tokens(Limit + 500), BaseParameterName);

        act.Should().Throw<OwnershipTokenLimitExceededException>();
    }

    /// <summary>
    /// The guard reads the configured count, not the deduplicated one. A cap applied after deduplication
    /// would be data-dependent — this input would slip through with 1,400 distinct tokens — which is the
    /// opposite of what a defensive bound is for.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_applies_the_cap_before_deduplication(SqlDialect dialect)
    {
        // 2,600 configured entries whose distinct count is only 1,400.
        List<short> withDuplicates = [.. Tokens(1400), .. Tokens(1200)];
        withDuplicates.Should().HaveCount(2600);
        withDuplicates.Distinct().Should().HaveCount(1400);

        Action act = () =>
            OwnershipTokenParameterizationFactory.Create(dialect, withDuplicates, BaseParameterName);

        act.Should()
            .Throw<OwnershipTokenLimitExceededException>()
            .Which.OwnershipTokenCount.Should()
            .Be(2600);
    }

    [Test]
    public void It_reports_the_cap_message_without_disclosing_token_values()
    {
        var created = OwnershipTokenParameterizationFactory.TryCreate(
            SqlDialect.Mssql,
            Tokens(Limit),
            BaseParameterName,
            out var parameterization,
            out var securityConfigurationMessage,
            out var failureKind
        );

        created.Should().BeFalse();
        parameterization.Should().BeNull();
        failureKind.Should().Be(OwnershipTokenParameterizationFailureKind.TokenCapExceeded);
        securityConfigurationMessage.Should().Contain(Limit.ToString(CultureInfo.InvariantCulture));
        securityConfigurationMessage.Should().Contain("ownership tokens");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_reports_success_through_try_create_below_the_limit(SqlDialect dialect)
    {
        var created = OwnershipTokenParameterizationFactory.TryCreate(
            dialect,
            [4, 9],
            BaseParameterName,
            out var parameterization,
            out var securityConfigurationMessage,
            out var failureKind
        );

        created.Should().BeTrue();
        parameterization.Should().NotBeNull();
        securityConfigurationMessage.Should().BeEmpty();
        failureKind.Should().BeNull();
    }

    [Test]
    public void It_rejects_an_unsupported_dialect()
    {
        Action act = () =>
            OwnershipTokenParameterizationFactory.Create((SqlDialect)999, [1], BaseParameterName);

        act.Should().Throw<NotSupportedException>();
    }

    [Test]
    public void It_rejects_a_null_token_list()
    {
        Action act = () =>
            OwnershipTokenParameterizationFactory.Create(SqlDialect.Pgsql, null!, BaseParameterName);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── in-memory mirror of the emitted predicate ──────────────────────

    [Test]
    public void It_matches_a_stored_token_that_is_in_the_list()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [3, 7],
            BaseParameterName
        );

        parameterization.MatchesStoredToken(7).Should().BeTrue();
    }

    [TestCase((short)11)]
    [TestCase(null)]
    public void It_does_not_match_a_nonmatching_or_null_stored_token(short? storedOwnershipTokenId)
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [3, 7],
            BaseParameterName
        );

        parameterization.MatchesStoredToken(storedOwnershipTokenId).Should().BeFalse();
    }

    [Test]
    public void It_matches_nothing_when_the_token_list_is_empty()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [],
            BaseParameterName
        );

        parameterization.MatchesStoredToken(3).Should().BeFalse();
        parameterization.MatchesStoredToken(null).Should().BeFalse();
    }
}

[TestFixture]
[Parallelizable]
public class Given_OwnershipTokenParameterizationValidator
{
    private const string BaseParameterName = "ownershipTokenIds";

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_a_parameterization_built_for_its_own_dialect(SqlDialect dialect)
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            dialect,
            [3, 7],
            BaseParameterName
        );

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                parameterization,
                dialect,
                nameof(parameterization),
                "Test"
            );

        act.Should().NotThrow();
    }

    [TestCase(SqlDialect.Pgsql, SqlDialect.Mssql)]
    [TestCase(SqlDialect.Mssql, SqlDialect.Pgsql)]
    public void It_rejects_a_parameterization_built_for_the_other_dialect(
        SqlDialect builtFor,
        SqlDialect validatedAgainst
    )
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            builtFor,
            [3, 7],
            BaseParameterName
        );

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                parameterization,
                validatedAgainst,
                nameof(parameterization),
                "Test"
            );

        act.Should().Throw<ArgumentException>();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_an_empty_token_list(SqlDialect dialect)
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(dialect, [], BaseParameterName);

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                parameterization,
                dialect,
                nameof(parameterization),
                "Test"
            );

        act.Should().NotThrow();
    }

    /// <summary>
    /// The factory cannot produce an over-limit parameterization, but a hand-constructed record can. The
    /// validator is the SQL boundary's last guard, so it must reject one rather than let the compiler emit it.
    /// </summary>
    [Test]
    public void It_rejects_a_hand_constructed_over_limit_parameterization()
    {
        var overLimit = new OwnershipTokenParameterization(
            OwnershipTokenParameterizationKind.PgsqlArray,
            BaseParameterName,
            [
                .. Enumerable
                    .Range(1, OwnershipTokenLimitExceededException.OwnershipTokenLimit)
                    .Select(static value => (short)value),
            ],
            [BaseParameterName]
        );

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                overLimit,
                SqlDialect.Pgsql,
                nameof(overLimit),
                "Test"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_sql_server_parameterization_whose_name_count_does_not_match_its_token_count()
    {
        var mismatched = new OwnershipTokenParameterization(
            OwnershipTokenParameterizationKind.MssqlScalar,
            BaseParameterName,
            [3, 7],
            ["ownershipTokenIds_0"]
        );

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                mismatched,
                SqlDialect.Mssql,
                nameof(mismatched),
                "Test"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_an_unsupported_dialect()
    {
        var parameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [3],
            BaseParameterName
        );

        Action act = () =>
            OwnershipTokenParameterizationValidator.ValidateOrThrow(
                parameterization,
                (SqlDialect)999,
                nameof(parameterization),
                "Test"
            );

        act.Should().Throw<NotSupportedException>();
    }
}
