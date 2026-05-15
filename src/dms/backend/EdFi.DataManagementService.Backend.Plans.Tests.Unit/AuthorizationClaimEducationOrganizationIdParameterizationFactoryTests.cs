// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_AuthorizationClaimEducationOrganizationIdParameterizationFactory
{
    [Test]
    public void It_should_use_a_single_postgresql_array_parameter_for_one_claim_id()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [42L],
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray);
        parameterization.ClaimEducationOrganizationIds.Should().Equal(42L);
        parameterization.ParameterNamesInOrder.Should().Equal("ClaimEducationOrganizationIds");
    }

    [Test]
    public void It_should_use_sql_server_scalar_parameters_for_one_unique_claim_id()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            [42L],
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar);
        parameterization.ClaimEducationOrganizationIds.Should().Equal(42L);
        parameterization.ParameterNamesInOrder.Should().Equal("ClaimEducationOrganizationIds_0");
    }

    [Test]
    public void It_should_use_sql_server_scalar_parameters_for_one_thousand_nine_hundred_ninety_nine_unique_claim_ids()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(1999),
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar);
        parameterization.ClaimEducationOrganizationIds.Should().HaveCount(1999);
        parameterization.ClaimEducationOrganizationIds[0].Should().Be(1L);
        parameterization.ClaimEducationOrganizationIds[^1].Should().Be(1999L);
        parameterization.ParameterNamesInOrder.Should().HaveCount(1999);
        parameterization.ParameterNamesInOrder[0].Should().Be("ClaimEducationOrganizationIds_0");
        parameterization.ParameterNamesInOrder[^1].Should().Be("ClaimEducationOrganizationIds_1998");
    }

    [Test]
    public void It_should_use_sql_server_structured_parameter_for_two_thousand_unique_claim_ids()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            CreateClaimEducationOrganizationIds(2000),
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured);
        parameterization.ClaimEducationOrganizationIds.Should().HaveCount(2000);
        parameterization.ClaimEducationOrganizationIds[0].Should().Be(1L);
        parameterization.ClaimEducationOrganizationIds[^1].Should().Be(2000L);
        parameterization.ParameterNamesInOrder.Should().Equal("ClaimEducationOrganizationIds");
    }

    [Test]
    public void It_should_dedupe_and_sort_postgresql_claim_ids()
    {
        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            [30L, 10L, 20L, 10L, 30L],
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray);
        parameterization.ClaimEducationOrganizationIds.Should().Equal(10L, 20L, 30L);
        parameterization.ParameterNamesInOrder.Should().Equal("ClaimEducationOrganizationIds");
    }

    [Test]
    public void It_should_dedupe_and_sort_sql_server_claim_ids_before_threshold_selection()
    {
        List<long> claimEducationOrganizationIds = [.. CreateClaimEducationOrganizationIds(1999)];
        claimEducationOrganizationIds.AddRange(CreateClaimEducationOrganizationIds(1999).Reverse());
        claimEducationOrganizationIds.Add(1999L);

        var parameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Mssql,
            claimEducationOrganizationIds,
            "ClaimEducationOrganizationIds"
        );

        parameterization
            .Kind.Should()
            .Be(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar);
        parameterization.ClaimEducationOrganizationIds.Should().HaveCount(1999);
        parameterization.ClaimEducationOrganizationIds[0].Should().Be(1L);
        parameterization.ClaimEducationOrganizationIds[^1].Should().Be(1999L);
        parameterization.ParameterNamesInOrder.Should().HaveCount(1999);
        parameterization.ParameterNamesInOrder[0].Should().Be("ClaimEducationOrganizationIds_0");
        parameterization.ParameterNamesInOrder[^1].Should().Be("ClaimEducationOrganizationIds_1998");
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
