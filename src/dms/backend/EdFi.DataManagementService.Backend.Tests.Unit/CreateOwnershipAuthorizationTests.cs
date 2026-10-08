// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

internal static class CreateOwnershipAuthorizationTestSupport
{
    public const short CreatorToken = 42;
    public const short ForeignToken = 7;
    public const int Limit = OwnershipTokenLimitExceededException.OwnershipTokenLimit;

    /// <summary>
    /// Distinct tokens starting at 1, so any range of at least 42 holds <see cref="CreatorToken"/>, and a
    /// verdict under the cap can never be mistaken for one the cap decided.
    /// </summary>
    public static IReadOnlyList<short> Tokens(int count) =>
        [.. Enumerable.Range(1, count).Select(static value => (short)value)];

    public static RelationalAuthorizationContext Context(
        short? creatorOwnershipTokenId,
        IReadOnlyList<short> ownershipTokenIds
    ) => new([], [], creatorOwnershipTokenId, ownershipTokenIds);

    public static OwnershipAuthorizationFailure DeniedFailure(UpsertResult? result) =>
        result.Should().BeOfType<UpsertResult.UpsertFailureOwnershipNotAuthorized>().Which.OwnershipFailure;
}

/// <summary>
/// Without a planned check the verdict owes nothing, even for the client every other fixture denies: no
/// creator token and an over-limit token list. This is what keeps a create unaffected where
/// <c>OwnershipBased</c> is not configured for it.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_with_no_planned_ownership_check(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            ownershipCheck: null,
            CreateOwnershipAuthorizationTestSupport.Context(
                null,
                CreateOwnershipAuthorizationTestSupport.Tokens(CreateOwnershipAuthorizationTestSupport.Limit)
            )
        );

    [Test]
    public void It_owes_nothing() => _result.Should().BeNull();
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_a_creator_token_among_the_callers_tokens(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                [
                    CreateOwnershipAuthorizationTestSupport.ForeignToken,
                    CreateOwnershipAuthorizationTestSupport.CreatorToken,
                ]
            )
        );

    [Test]
    public void It_lets_the_create_proceed() => _result.Should().BeNull();
}

/// <summary>
/// The smallest authorizing list: the creator token alone.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_a_creator_token_that_is_the_callers_only_token(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                [CreateOwnershipAuthorizationTestSupport.CreatorToken]
            )
        );

    [Test]
    public void It_lets_the_create_proceed() => _result.Should().BeNull();
}

/// <summary>
/// A client with no creator token would stamp NULL, the state §2.14 describes, so the create is refused with
/// that kind — even though the client holds a token of its own.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_a_caller_with_no_creator_token(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(2),
            CreateOwnershipAuthorizationTestSupport.Context(
                null,
                [CreateOwnershipAuthorizationTestSupport.ForeignToken]
            )
        );

    [Test]
    public void It_denies_the_create_as_uninitialized() =>
        CreateOwnershipAuthorizationTestSupport
            .DeniedFailure(_result)
            .Should()
            .Be(
                new OwnershipAuthorizationFailure(
                    OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized,
                    2,
                    AuthorizationStrategyNameConstants.OwnershipBased
                )
            );
}

/// <summary>
/// A creator token outside the client's own list would stamp a row the client could never reach, which is
/// the §2.13 mismatch.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_a_creator_token_outside_the_callers_tokens(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(3),
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                [CreateOwnershipAuthorizationTestSupport.ForeignToken]
            )
        );

    [Test]
    public void It_denies_the_create_as_a_mismatch() =>
        CreateOwnershipAuthorizationTestSupport
            .DeniedFailure(_result)
            .Should()
            .Be(
                new OwnershipAuthorizationFailure(
                    OwnershipAuthorizationFailureKind.OwnershipTokenMismatch,
                    3,
                    AuthorizationStrategyNameConstants.OwnershipBased
                )
            );
}

/// <summary>
/// An empty token list is valid configuration, not a cap or a planning failure: the creator token is simply
/// not in it.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_a_creator_token_and_an_empty_token_list(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                []
            )
        );

    [Test]
    public void It_denies_the_create_as_a_mismatch() =>
        CreateOwnershipAuthorizationTestSupport
            .DeniedFailure(_result)
            .FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
}

/// <summary>
/// At the cap the security-configuration 500 is owed ahead of either denial: the client here also has no
/// creator token, so an evaluator that checked the creator token first would report §2.14 instead.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_at_the_ownership_token_cap(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup() =>
        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(
                null,
                CreateOwnershipAuthorizationTestSupport.Tokens(CreateOwnershipAuthorizationTestSupport.Limit)
            )
        );

    [Test]
    public void It_owes_the_token_cap_security_configuration_failure() =>
        _result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .Equal(
                OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                    CreateOwnershipAuthorizationTestSupport.Limit
                )
            );

    [Test]
    public void It_reports_the_token_cap_diagnostic() =>
        _result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded);

    /// <summary>
    /// The executor attributes the selected action at its boundary, so the verdict itself carries none.
    /// </summary>
    [Test]
    public void It_leaves_the_failure_unattributed() =>
        _result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.TargetAction.Should()
            .BeNull();
}

/// <summary>
/// One token under the cap, the list is authorized normally in both directions: it lets a held creator token
/// create, and still denies a missing one as §2.14 rather than failing closed as if at the cap.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_one_token_under_the_ownership_token_cap(SqlDialect dialect)
{
    private UpsertResult? _heldCreatorResult;
    private UpsertResult? _missingCreatorResult;

    [SetUp]
    public void Setup()
    {
        var tokens = CreateOwnershipAuthorizationTestSupport.Tokens(
            CreateOwnershipAuthorizationTestSupport.Limit - 1
        );

        _heldCreatorResult = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                tokens
            )
        );
        _missingCreatorResult = CreateOwnershipAuthorization.Evaluate(
            dialect,
            new OwnershipAuthorizationCheckSpec(0),
            CreateOwnershipAuthorizationTestSupport.Context(null, tokens)
        );
    }

    [Test]
    public void It_lets_a_held_creator_token_create() => _heldCreatorResult.Should().BeNull();

    [Test]
    public void It_denies_a_missing_creator_token_as_uninitialized() =>
        CreateOwnershipAuthorizationTestSupport
            .DeniedFailure(_missingCreatorResult)
            .FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
}

/// <summary>
/// <c>OwnershipBased</c> configured more than once yields one verdict attributed to its earliest configured
/// occurrence, whatever order the configuration lists them in, because the verdict takes its index from the
/// planner's collapsed check.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_a_create_verdict_for_duplicate_OwnershipBased_configuration(SqlDialect dialect)
{
    private UpsertResult? _result;

    [SetUp]
    public void Setup()
    {
        var ownershipCheck = OwnershipAuthorizationPlanner.Plan(
            NamespaceAuthorizationOperation.Update,
            [
                new ConfiguredAuthorizationStrategy(AuthorizationStrategyNameConstants.OwnershipBased, 4),
                new ConfiguredAuthorizationStrategy(AuthorizationStrategyNameConstants.OwnershipBased, 1),
            ]
        );

        _result = CreateOwnershipAuthorization.Evaluate(
            dialect,
            ownershipCheck,
            CreateOwnershipAuthorizationTestSupport.Context(
                CreateOwnershipAuthorizationTestSupport.CreatorToken,
                [CreateOwnershipAuthorizationTestSupport.ForeignToken]
            )
        );
    }

    [Test]
    public void It_attributes_the_denial_to_the_earliest_configured_occurrence() =>
        CreateOwnershipAuthorizationTestSupport.DeniedFailure(_result).ConfiguredStrategyIndex.Should().Be(1);
}
