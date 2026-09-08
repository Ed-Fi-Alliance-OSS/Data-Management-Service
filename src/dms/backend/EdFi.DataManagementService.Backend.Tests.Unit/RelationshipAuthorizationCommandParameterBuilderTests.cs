// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationCommandParameterBuilder
{
    private const string BaseParameterName = "ClaimEducationOrganizationIds";

    [TestCase(AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray)]
    [TestCase(AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured)]
    public void It_should_add_one_list_value_for_list_based_authorization_parameterizations(
        AuthorizationClaimEducationOrganizationIdParameterizationKind parameterizationKind
    )
    {
        IReadOnlyList<long> claimEducationOrganizationIds = [111L, 222L];
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal) { ["DocumentId"] = 123L };
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            parameterizationKind,
            BaseParameterName,
            claimEducationOrganizationIds,
            [BaseParameterName]
        );

        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            parameterValues,
            parameterization
        );

        parameterValues["DocumentId"].Should().Be(123L);
        parameterValues[BaseParameterName].Should().BeSameAs(claimEducationOrganizationIds);
        parameterValues.Should().HaveCount(2);
    }

    [Test]
    public void It_should_add_sql_server_scalar_values_in_parameter_name_order()
    {
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar,
            BaseParameterName,
            [111L, 222L, 333L],
            [
                "ClaimEducationOrganizationIds_0",
                "ClaimEducationOrganizationIds_1",
                "ClaimEducationOrganizationIds_2",
            ]
        );
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);

        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            parameterValues,
            parameterization
        );

        parameterValues.Should().HaveCount(3);
        parameterValues["ClaimEducationOrganizationIds_0"].Should().Be(111L);
        parameterValues["ClaimEducationOrganizationIds_1"].Should().Be(222L);
        parameterValues["ClaimEducationOrganizationIds_2"].Should().Be(333L);
    }

    [Test]
    public void It_should_reject_unsupported_authorization_parameterization_kinds()
    {
        var parameterization = new AuthorizationClaimEducationOrganizationIdParameterization(
            (AuthorizationClaimEducationOrganizationIdParameterizationKind)999,
            BaseParameterName,
            [111L],
            [BaseParameterName]
        );
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);

        var act = () =>
            RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
                parameterValues,
                parameterization
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .Where(exception => exception.ParamName == "authorizationClaimParameterization");
    }
}
