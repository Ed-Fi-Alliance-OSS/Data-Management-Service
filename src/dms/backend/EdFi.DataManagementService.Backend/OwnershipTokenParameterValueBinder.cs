// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Binds runtime ownership-token parameter values into a planner parameter-value dictionary. PostgreSQL
/// stores the whole token list under the base parameter name; SQL Server stores each token under its
/// allocated scalar parameter name in declaration order.
/// </summary>
/// <remarks>
/// <para>
/// The bound values are <see cref="short"/> — ownership tokens are <c>smallint</c> — never the
/// <see cref="long"/> shape the relationship claim-EdOrg array path binds. On PostgreSQL that means the
/// array parameter is a <c>short[]</c>, from which Npgsql infers <c>smallint[]</c>, so
/// <c>= ANY(@ownershipTokenIds)</c> compares <c>smallint</c> to <c>smallint</c>. Binding a wider type would
/// force the provider to widen <c>dms.Document.CreatedByOwnershipTokenId</c> for the comparison and give up
/// the index on it.
/// </para>
/// <para>
/// No explicit <c>DbType</c> is declared, unlike the nullable create-stamping parameter. Every bound token
/// is a non-null <see cref="short"/>, so both providers infer <c>smallint</c> from the runtime value; the
/// stamping parameter needs the declaration only because a null binds as <see cref="DBNull"/>, which
/// carries no type of its own.
/// </para>
/// </remarks>
internal static class OwnershipTokenParameterValueBinder
{
    public static void Bind(
        IDictionary<string, object?> parameterValues,
        OwnershipTokenParameterization? ownershipTokenParameterization
    )
    {
        ArgumentNullException.ThrowIfNull(parameterValues);

        if (ownershipTokenParameterization is null)
        {
            return;
        }

        if (ownershipTokenParameterization.MatchesNoToken)
        {
            // An empty token list renders the membership predicate as a constant false, which references no
            // parameter. Binding a value here would leave the command declaring a parameter its SQL never
            // mentions — rejected by RelationalCompositeStatementRewriter as a dangling parameter.
            return;
        }

        switch (ownershipTokenParameterization.Kind)
        {
            case OwnershipTokenParameterizationKind.PgsqlArray:
                parameterValues[ownershipTokenParameterization.BaseParameterName] =
                    ownershipTokenParameterization.TokensInOrder;
                return;

            case OwnershipTokenParameterizationKind.MssqlScalar:
                for (
                    var parameterIndex = 0;
                    parameterIndex < ownershipTokenParameterization.ParameterNamesInOrder.Count;
                    parameterIndex++
                )
                {
                    parameterValues[ownershipTokenParameterization.ParameterNamesInOrder[parameterIndex]] =
                        ownershipTokenParameterization.TokensInOrder[parameterIndex];
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(ownershipTokenParameterization),
                    ownershipTokenParameterization.Kind,
                    "Unsupported ownership token parameterization kind."
                );
        }
    }
}
