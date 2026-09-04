// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The preflight is defence in depth behind the planner's own token-cap terminal: it converts the
/// parameterization factory's throw into a clean security-configuration result so a caller that skipped the
/// planner still fails closed instead of emitting an over-limit parameter list or letting the exception
/// escape as a generic failure.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_OwnershipTokenParameterizationPreflight
{
    private const int Limit = OwnershipTokenLimitExceededException.OwnershipTokenLimit;

    private static IReadOnlyList<short> Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(static value => (short)value)];

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_creates_a_parameterization_below_the_limit(SqlDialect dialect)
    {
        var created = OwnershipTokenParameterizationPreflight.TryCreate(
            dialect,
            [3, 7],
            out var parameterization,
            out var securityConfigurationMessage,
            out var diagnostics
        );

        created.Should().BeTrue();
        parameterization.Should().NotBeNull();
        parameterization.TokensInOrder.Should().Equal((short)3, (short)7);
        securityConfigurationMessage.Should().BeEmpty();
        diagnostics.Should().BeEmpty();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_creates_a_parameterization_for_an_empty_token_list(SqlDialect dialect)
    {
        var created = OwnershipTokenParameterizationPreflight.TryCreate(
            dialect,
            [],
            out var parameterization,
            out _,
            out var diagnostics
        );

        created.Should().BeTrue();
        parameterization.MatchesNoToken.Should().BeTrue();
        diagnostics.Should().BeEmpty();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_accepts_one_token_below_the_limit_on_every_provider(SqlDialect dialect)
    {
        OwnershipTokenParameterizationPreflight
            .TryCreate(dialect, Tokens(Limit - 1), out _, out _, out _)
            .Should()
            .BeTrue();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_fails_closed_at_the_limit_on_every_provider(SqlDialect dialect)
    {
        var created = OwnershipTokenParameterizationPreflight.TryCreate(
            dialect,
            Tokens(Limit),
            out var parameterization,
            out var securityConfigurationMessage,
            out var diagnostics
        );

        created.Should().BeFalse();
        parameterization.Should().BeNull();
        securityConfigurationMessage
            .Should()
            .Be(OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(Limit));

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic
            .ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded);
        diagnostic.ConfiguredStrategyNames.Should().Equal(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The cap reads the configured count, so a list whose duplicates would bring it under the limit still
    /// fails closed.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_fails_closed_before_deduplication_on_every_provider(SqlDialect dialect)
    {
        List<short> withDuplicates = [.. Tokens(1400), .. Tokens(1200)];

        OwnershipTokenParameterizationPreflight
            .TryCreate(dialect, withDuplicates, out _, out var securityConfigurationMessage, out _)
            .Should()
            .BeFalse();

        securityConfigurationMessage
            .Should()
            .Be(OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(2600));
    }

    /// <summary>
    /// A public failure response must never disclose ownership-token values. The count is safe — it tells an
    /// operator what to fix — but the tokens identify other clients' data partitions.
    /// </summary>
    [Test]
    public void It_reports_no_ownership_token_value_in_the_failure_message_or_diagnostics()
    {
        const short DistinctiveToken = 31337;
        List<short> tokens = [.. Tokens(Limit - 1), DistinctiveToken];

        OwnershipTokenParameterizationPreflight.TryCreate(
            SqlDialect.Mssql,
            tokens,
            out _,
            out var securityConfigurationMessage,
            out var diagnostics
        );

        securityConfigurationMessage.Should().NotContain("31337");
        diagnostics.Should().ContainSingle();
        diagnostics[0].PhysicalPath.Should().BeNull();
        diagnostics[0].ResourceFullName.Should().BeNull();
    }
}
