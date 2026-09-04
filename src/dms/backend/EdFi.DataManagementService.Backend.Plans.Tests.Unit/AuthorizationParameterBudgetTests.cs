// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_AuthorizationParameterBudget
{
    // The query binds two paging parameters on top of the authorization lists; tests that only exercise
    // the authorization lists pass this as the non-authorization parameter count.
    private const int PagingOnly = AuthorizationParameterBudget.PaginationParameterCount;

    [Test]
    public void It_never_flags_postgresql_even_when_the_total_would_exceed_the_sql_server_ceiling()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateNamespacePrefixes(1999),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateClaimEducationOrganizationIds(1999),
            "ClaimEducationOrganizationIds"
        );

        // The 2,098 ceiling is SQL Server-specific. Even with a query parameter count that would blow that
        // ceiling, PostgreSQL is never flagged: it allows far more command parameters and binds each list
        // as a single array/table-valued parameter.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Pgsql,
                namespacePrefixParameterization,
                claimParameterization,
                nonAuthorizationParameterCount: 5000
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_sql_server_scalar_lists_that_together_exceed_the_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1500),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1500),
            "ClaimEducationOrganizationIds"
        );

        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimParameterization,
                PagingOnly
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_total_count_exactly_at_the_command_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1048),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1048),
            "ClaimEducationOrganizationIds"
        );

        // 1,048 + 1,048 + 2 paging == 2,098, exactly the usable SQL Server per-command ceiling (the 2,100
        // RPC limit less the two slots sp_executesql takes for @stmt/@params), which is allowed.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimParameterization,
                PagingOnly
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_a_total_count_one_past_the_command_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1049),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1048),
            "ClaimEducationOrganizationIds"
        );

        // 1,049 + 1,048 + 2 paging == 2,099, one past the usable SQL Server per-command ceiling. This is
        // inside the documented 2,100 RPC limit, so only counting the sp_executesql overhead catches it.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimParameterization,
                PagingOnly
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_counts_a_structured_claim_parameter_as_one_so_a_near_cap_prefix_list_is_not_flagged()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1999),
            "namespacePrefixes"
        );
        // 2,000 claim ids cross the structured-parameter threshold, so the claim list binds a single
        // table-valued parameter; combined with 1,999 scalar prefix parameters and 2 paging parameters
        // that is 2,002 real parameters, which must not be flagged.
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(2000),
            "ClaimEducationOrganizationIds"
        );

        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimParameterization,
                PagingOnly
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_a_namespace_only_list_that_with_query_parameters_exceeds_the_command_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1999),
            "namespacePrefixes"
        );

        // 1,999 scalar prefix parameters + 98 query filter parameters + 2 paging == 2,099, one past the
        // ceiling, even though the prefix list alone is within its own per-list cap and no relationship
        // parameterization is present.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 98 + PagingOnly
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_namespace_only_list_whose_total_is_within_the_command_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1999),
            "namespacePrefixes"
        );

        // 1,999 + 97 query filter parameters + 2 paging == 2,098, exactly at the ceiling.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 97 + PagingOnly
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_a_relationship_only_list_that_with_query_parameters_exceeds_the_command_limit()
    {
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1999),
            "ClaimEducationOrganizationIds"
        );

        // 1,999 scalar claim parameters + 98 query filter parameters + 2 paging == 2,099, one past the
        // ceiling, with no namespace parameterization present.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimParameterization,
                nonAuthorizationParameterCount: 98 + PagingOnly
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_rejects_a_negative_non_authorization_parameter_count()
    {
        Action act = () =>
            AuthorizationParameterBudget.ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: -1
            );

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("nonAuthorizationParameterCount");
    }

    // ── the OwnershipBased token list ──────────────────────────────────

    [Test]
    public void It_counts_nothing_for_ownership_when_the_argument_is_omitted_or_null()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(3),
            "namespacePrefixes"
        );

        // The trailing argument defaults to null, so the read paths that do not filter by ownership keep
        // their existing calls and their existing counts.
        AuthorizationParameterBudget
            .CountAuthorizationParameters(namespacePrefixParameterization, null)
            .Should()
            .Be(3);
        AuthorizationParameterBudget
            .CountAuthorizationParameters(namespacePrefixParameterization, null, null)
            .Should()
            .Be(3);
    }

    [Test]
    public void It_counts_one_sql_server_scalar_parameter_per_ownership_token()
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        AuthorizationParameterBudget
            .CountAuthorizationParameters(null, null, ownershipTokenParameterization)
            .Should()
            .Be(1999);
    }

    [Test]
    public void It_counts_a_postgresql_ownership_array_as_one_parameter()
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        AuthorizationParameterBudget
            .CountAuthorizationParameters(null, null, ownershipTokenParameterization)
            .Should()
            .Be(1);
    }

    /// <summary>
    /// An empty token list declares its base parameter name but renders a constant-false predicate that binds
    /// nothing, so it must count as zero on both providers rather than as the one name it declares.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_counts_an_empty_ownership_token_list_as_no_parameters(SqlDialect dialect)
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            dialect,
            [],
            "ownershipTokenIds"
        );

        AuthorizationParameterBudget
            .CountAuthorizationParameters(null, null, ownershipTokenParameterization)
            .Should()
            .Be(0);
    }

    /// <summary>
    /// 1,999 is the largest token count CMS permits. Alone on SQL Server it binds 1,999 scalars plus the two
    /// paging parameters, which is well inside the 2,098 ceiling, so an ownership-only page must not be flagged.
    /// </summary>
    [Test]
    public void It_does_not_flag_an_ownership_only_list_of_1999_sql_server_tokens()
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimEducationOrganizationIdParameterization: null,
                PagingOnly,
                ownershipTokenParameterization
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_does_not_flag_an_ownership_only_list_whose_total_is_exactly_at_the_command_limit()
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        // 1,999 scalar token parameters + 97 query filter parameters + 2 paging == 2,098, exactly at the
        // usable SQL Server per-command ceiling, which is allowed.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 97 + PagingOnly,
                ownershipTokenParameterization
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_an_ownership_only_list_that_with_query_parameters_is_one_past_the_command_limit()
    {
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        // 1,999 + 98 query filter parameters + 2 paging == 2,099, one past the ceiling, even though the token
        // list alone is within its own per-list cap.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 98 + PagingOnly,
                ownershipTokenParameterization
            )
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// The composition the GET-many story adds: a near-cap ownership list alongside a modest prefix list. Each
    /// is within its own per-list cap, yet together they exceed what one SQL Server command can bind.
    /// </summary>
    [Test]
    public void It_flags_sql_server_ownership_and_namespace_lists_that_together_exceed_the_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(100),
            "namespacePrefixes"
        );
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        // 100 + 1,999 + 2 paging == 2,101.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                PagingOnly,
                ownershipTokenParameterization
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_flags_all_three_sql_server_scalar_lists_that_together_exceed_the_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(700),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(700),
            "ClaimEducationOrganizationIds"
        );
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateOwnershipTokens(700),
            "ownershipTokenIds"
        );

        // 700 + 700 + 700 + 2 paging == 2,102.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimParameterization,
                PagingOnly,
                ownershipTokenParameterization
            )
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_never_flags_postgresql_ownership_composition_even_at_every_per_list_cap()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateNamespacePrefixes(1999),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateClaimEducationOrganizationIds(1999),
            "ClaimEducationOrganizationIds"
        );
        var ownershipTokenParameterization = OwnershipTokenParameterizationFactory.Create(
            SqlDialect.Pgsql,
            CreateOwnershipTokens(1999),
            "ownershipTokenIds"
        );

        // Three arrays bind three parameters, and the ceiling is SQL Server-specific anyway.
        AuthorizationParameterBudget
            .CountAuthorizationParameters(
                namespacePrefixParameterization,
                claimParameterization,
                ownershipTokenParameterization
            )
            .Should()
            .Be(3);
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Pgsql,
                namespacePrefixParameterization,
                claimParameterization,
                nonAuthorizationParameterCount: 5000,
                ownershipTokenParameterization
            )
            .Should()
            .BeFalse();
    }

    private static IReadOnlyList<short> CreateOwnershipTokens(int count)
    {
        short[] ownershipTokens = new short[count];

        for (var index = 0; index < count; index++)
        {
            ownershipTokens[index] = (short)(index + 1);
        }

        return ownershipTokens;
    }

    private static IReadOnlyList<string> CreateNamespacePrefixes(int count)
    {
        string[] namespacePrefixes = new string[count];

        for (var index = 0; index < count; index++)
        {
            namespacePrefixes[index] = $"uri://prefix-{index}/";
        }

        return namespacePrefixes;
    }

    private static IReadOnlyList<long> CreateClaimEducationOrganizationIds(int count)
    {
        long[] claimEducationOrganizationIds = new long[count];

        for (var index = 0; index < count; index++)
        {
            claimEducationOrganizationIds[index] = index + 1L;
        }

        return claimEducationOrganizationIds;
    }
}
