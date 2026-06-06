// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

public sealed class PostgresqlTokenInfoEducationOrganizationLookup(IRelationalCommandExecutor commandExecutor)
    : IRelationalTokenInfoEducationOrganizationLookup
{
    private const string ClaimEducationOrganizationIdsParameterName = "ClaimEducationOrganizationIds";
    private static readonly NpgsqlDbType _claimEducationOrganizationIdsParameterDbType = (NpgsqlDbType)(
        (int)NpgsqlDbType.Array | (int)NpgsqlDbType.Bigint
    );

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public async Task<IEnumerable<TokenInfoEducationOrganization>> GetEducationOrganizations(
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds,
        MappingSet mappingSet
    )
    {
        ArgumentNullException.ThrowIfNull(educationOrganizationIds);
        ArgumentNullException.ThrowIfNull(mappingSet);

        if (educationOrganizationIds.Count == 0)
        {
            return [];
        }

        var claimEducationOrganizationIds = educationOrganizationIds
            .Select(static educationOrganizationId => educationOrganizationId.Value)
            .ToArray();
        var claimParameterization = AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
            SqlDialect.Pgsql,
            claimEducationOrganizationIds,
            ClaimEducationOrganizationIdsParameterName
        );
        var plan = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Pgsql).Compile(
            new TokenInfoEducationOrganizationSqlSpec(mappingSet, claimParameterization)
        );
        var command = new RelationalCommand(
            plan.EducationOrganizationSql,
            BuildParameters(plan.ParametersInOrder, claimParameterization)
        );

        return await _commandExecutor
            .ExecuteReaderAsync(command, TokenInfoEducationOrganizationRowReader.ReadAsync)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<RelationalParameter> BuildParameters(
        IReadOnlyList<QuerySqlParameter> parametersInOrder,
        AuthorizationClaimEducationOrganizationIdParameterization claimParameterization
    )
    {
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal)
        {
            [claimParameterization.BaseParameterName] = claimParameterization.ClaimEducationOrganizationIds,
        };

        return
        [
            .. parametersInOrder.Select(parameter =>
                parameter.Binding.Kind is QuerySqlParameterBindingKind.PgsqlArray
                    ? new RelationalParameter(
                        $"@{parameter.ParameterName}",
                        RequireClaimEducationOrganizationIds(
                            parameterValues[parameter.ParameterName],
                            parameter.ParameterName
                        ),
                        ConfigureClaimEducationOrganizationIdsParameter
                    )
                    : throw new InvalidOperationException(
                        $"PostgreSQL token_info lookup does not support parameter binding kind '{parameter.Binding.Kind}'."
                    )
            ),
        ];
    }

    private static long[] RequireClaimEducationOrganizationIds(object? value, string parameterName)
    {
        if (value is IReadOnlyList<long> values)
        {
            return [.. values];
        }

        throw new InvalidOperationException(
            $"PostgreSQL token_info parameter '{parameterName}' requires an IReadOnlyList<long> runtime value."
        );
    }

    private static void ConfigureClaimEducationOrganizationIdsParameter(DbParameter parameter)
    {
        if (parameter is not NpgsqlParameter npgsqlParameter)
        {
            throw new InvalidOperationException(
                "PostgreSQL token_info parameter configuration requires an NpgsqlParameter instance."
            );
        }

        npgsqlParameter.NpgsqlDbType = _claimEducationOrganizationIdsParameterDbType;
    }
}
