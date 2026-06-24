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

        // The 2,100 ceiling is SQL Server-specific. Even with a query parameter count that would blow that
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
            CreateNamespacePrefixes(1049),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1049),
            "ClaimEducationOrganizationIds"
        );

        // 1,049 + 1,049 + 2 paging == 2,100, exactly the SQL Server per-command ceiling, which is allowed.
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
            CreateNamespacePrefixes(1050),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1049),
            "ClaimEducationOrganizationIds"
        );

        // 1,050 + 1,049 + 2 paging == 2,101, one past the SQL Server per-command ceiling.
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

        // 1,999 scalar prefix parameters + 100 query filter parameters + 2 paging == 2,101, one past the
        // ceiling, even though the prefix list alone is within its own per-list cap and no relationship
        // parameterization is present.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 100 + PagingOnly
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

        // 1,999 + 99 query filter parameters + 2 paging == 2,100, exactly at the ceiling.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization: null,
                nonAuthorizationParameterCount: 99 + PagingOnly
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

        // 1,999 scalar claim parameters + 100 query filter parameters + 2 paging == 2,101, one past the
        // ceiling, with no namespace parameterization present.
        AuthorizationParameterBudget
            .ExceedsCommandParameterLimit(
                SqlDialect.Mssql,
                namespacePrefixParameterization: null,
                claimParameterization,
                nonAuthorizationParameterCount: 100 + PagingOnly
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
