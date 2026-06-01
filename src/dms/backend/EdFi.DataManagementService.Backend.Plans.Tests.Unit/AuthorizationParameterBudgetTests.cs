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
    [Test]
    public void It_does_not_flag_postgresql_array_parameterizations()
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

        AuthorizationParameterBudget
            .ExceedsCombinedLimit(namespacePrefixParameterization, claimParameterization)
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
            .ExceedsCombinedLimit(namespacePrefixParameterization, claimParameterization)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_combined_count_exactly_at_the_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1000),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1000),
            "ClaimEducationOrganizationIds"
        );

        // 1,000 + 1,000 == the 2,000 combined limit, which stays within the SQL Server ceiling.
        AuthorizationParameterBudget
            .ExceedsCombinedLimit(namespacePrefixParameterization, claimParameterization)
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_flags_a_combined_count_one_past_the_limit()
    {
        var namespacePrefixParameterization = NamespacePrefixParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateNamespacePrefixes(1000),
            "namespacePrefixes"
        );
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1001),
            "ClaimEducationOrganizationIds"
        );

        AuthorizationParameterBudget
            .ExceedsCombinedLimit(namespacePrefixParameterization, claimParameterization)
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
        // table-valued parameter; combined with 1,999 scalar prefix parameters that is 2,000 real
        // parameters, which must not be flagged.
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(2000),
            "ClaimEducationOrganizationIds"
        );

        AuthorizationParameterBudget
            .ExceedsCombinedLimit(namespacePrefixParameterization, claimParameterization)
            .Should()
            .BeFalse();
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
