// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Mssql;

public sealed class MssqlTokenInfoEducationOrganizationLookup(
    IRelationalCommandExecutor commandExecutor,
    IRelationalParameterConfigurator parameterConfigurator
) : IRelationalTokenInfoEducationOrganizationLookup
{
    private const string ClaimEducationOrganizationIdsParameterName = "ClaimEducationOrganizationIds";

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationalParameterConfigurator _parameterConfigurator =
        parameterConfigurator ?? throw new ArgumentNullException(nameof(parameterConfigurator));

    public Task<IEnumerable<TokenInfoEducationOrganization>> GetEducationOrganizations(
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds
    )
    {
        ArgumentNullException.ThrowIfNull(educationOrganizationIds);

        return educationOrganizationIds.Count == 0
            ? Task.FromResult<IEnumerable<TokenInfoEducationOrganization>>([])
            : throw new InvalidOperationException(
                "SQL Server relational token_info education organization lookup requires a MappingSet."
            );
    }

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
            SqlDialect.Mssql,
            claimEducationOrganizationIds,
            ClaimEducationOrganizationIdsParameterName
        );
        var plan = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Mssql).Compile(
            new TokenInfoEducationOrganizationSqlSpec(mappingSet, claimParameterization)
        );
        var command = new RelationalCommand(
            plan.EducationOrganizationSql,
            BuildParameters(plan.ParametersInOrder, claimParameterization)
        );

        return await _commandExecutor.ExecuteReaderAsync(command, ReadRowsAsync).ConfigureAwait(false);
    }

    private IReadOnlyList<RelationalParameter> BuildParameters(
        IReadOnlyList<QuerySqlParameter> parametersInOrder,
        AuthorizationClaimEducationOrganizationIdParameterization claimParameterization
    )
    {
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);
        AuthorizationClaimEducationOrganizationIdParameterValues.AddTo(
            parameterValues,
            claimParameterization
        );

        return
        [
            .. parametersInOrder.Select(parameter =>
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    parameter,
                    parameterValues[parameter.ParameterName],
                    _parameterConfigurator
                )
            ),
        ];
    }

    private static async Task<IReadOnlyList<TokenInfoEducationOrganization>> ReadRowsAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        List<TokenInfoEducationOrganization> rows = [];

        var educationOrganizationIdOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.EducationOrganizationId.Value
        );
        var nameOfInstitutionOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.NameOfInstitution.Value
        );
        var discriminatorOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.Discriminator.Value
        );
        var ancestorDiscriminatorOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.AncestorDiscriminator.Value
        );
        var ancestorEducationOrganizationIdOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.AncestorEducationOrganizationId.Value
        );

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new TokenInfoEducationOrganization(
                    reader.GetFieldValue<long>(educationOrganizationIdOrdinal),
                    reader.GetFieldValue<string>(nameOfInstitutionOrdinal),
                    reader.GetFieldValue<string>(discriminatorOrdinal),
                    reader.GetFieldValue<string>(ancestorDiscriminatorOrdinal),
                    reader.GetFieldValue<long>(ancestorEducationOrganizationIdOrdinal)
                )
            );
        }

        return rows;
    }
}
