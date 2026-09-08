// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class AuthorizationClaimEducationOrganizationIdSqlHelper
{
    public static IReadOnlyList<QuerySqlParameter> BuildFilterParametersInOrder(
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    ) =>
        authorizationClaimParameterization.Kind switch
        {
            AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    authorizationClaimParameterization.BaseParameterName,
                    QuerySqlParameterBinding.PgsqlArray
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar =>
            [
                .. authorizationClaimParameterization.ParameterNamesInOrder.Select(
                    static parameterName => new QuerySqlParameter(QuerySqlParameterRole.Filter, parameterName)
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured =>
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    authorizationClaimParameterization.BaseParameterName,
                    QuerySqlParameterBinding.CreateMssqlStructured(
                        AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterTypeName,
                        AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                    )
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(authorizationClaimParameterization),
                authorizationClaimParameterization.Kind,
                "Unsupported authorization claim EdOrg parameterization kind."
            ),
        };

    public static void AppendClaimFilterSql(
        SqlWriter writer,
        AuthorizationClaimEducationOrganizationIdParameterization authorizationClaimParameterization
    )
    {
        switch (authorizationClaimParameterization.Kind)
        {
            case AuthorizationClaimEducationOrganizationIdParameterizationKind.PgsqlArray:
                writer.Append(" = ANY(");
                writer.AppendParameter(authorizationClaimParameterization.BaseParameterName);
                writer.Append(")");
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlScalar:
                writer.Append(" IN (");

                for (
                    var parameterIndex = 0;
                    parameterIndex < authorizationClaimParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    if (parameterIndex > 0)
                    {
                        writer.Append(", ");
                    }

                    writer.AppendParameter(
                        authorizationClaimParameterization.ParameterNamesInOrder[parameterIndex]
                    );
                }

                writer.Append(")");
                return;

            case AuthorizationClaimEducationOrganizationIdParameterizationKind.MssqlStructured:
                writer.Append(" IN (SELECT ");
                writer.AppendQuoted(
                    AuthorizationClaimEducationOrganizationIdParameterizationFactory.MssqlStructuredParameterColumnName
                );
                writer.Append(" FROM ");
                writer.AppendParameter(authorizationClaimParameterization.BaseParameterName);
                writer.Append(")");
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(authorizationClaimParameterization),
                    authorizationClaimParameterization.Kind,
                    "Unsupported authorization claim EdOrg parameterization kind."
                );
        }
    }
}
