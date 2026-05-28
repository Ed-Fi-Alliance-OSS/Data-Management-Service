// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

public sealed record SingleRecordRelationshipAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    IReadOnlyList<RelationshipAuthorizationCheckSpec> CheckSpecs,
    AuthorizationClaimEducationOrganizationIdParameterization ClaimEducationOrganizationIdParameterization,
    int EmittedAuth1Index
);

public abstract record SingleRecordRelationshipAuthorizationExecutionResult
{
    private SingleRecordRelationshipAuthorizationExecutionResult() { }

    public sealed record Authorized(long ObservedContentVersion)
        : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record NotAuthorized(RelationshipAuthorizationFailure RelationshipFailure)
        : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record StaleTarget() : SingleRecordRelationshipAuthorizationExecutionResult;

    public sealed record InvalidAuthorizationFailure(string FailureMessage)
        : SingleRecordRelationshipAuthorizationExecutionResult;
}

public interface ISingleRecordRelationshipAuthorizationExecutor
{
    Task<SingleRecordRelationshipAuthorizationExecutionResult> ExecuteAsync(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class SingleRecordRelationshipAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationalParameterConfigurator? parameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : ISingleRecordRelationshipAuthorizationExecutor
{
    private const string AuthorizationResultColumn = "AuthorizationResult";
    private const string ContentVersionColumn = "ContentVersion";

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationalParameterConfigurator _parameterConfigurator =
        parameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<SingleRecordRelationshipAuthorizationExecutionResult> ExecuteAsync(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var compiler = new SingleRecordRelationshipAuthorizationSqlCompiler(request.MappingSet.Key.Dialect);
        var sqlPlan = compiler.Compile(
            new SingleRecordRelationshipAuthorizationSqlSpec(
                request.CheckSpecs,
                request.ClaimEducationOrganizationIdParameterization,
                request.EmittedAuth1Index
            )
        );

        try
        {
            return await _commandExecutor
                .ExecuteReaderAsync(
                    BuildCommand(sqlPlan, request, _parameterConfigurator),
                    ReadAuthorizedResultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
            when (RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
                    request.MappingSet.Key.Dialect,
                    ex,
                    _providerFailureExtractor,
                    request.EmittedAuth1Index,
                    request.CheckSpecs,
                    request.ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds,
                    out var relationshipFailure
                )
            )
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized(
                relationshipFailure!
            );
        }
        catch (DbException ex)
            when (RelationshipAuthorizationProviderFailureMapper.IsRelationshipAuthorizationProviderFailure(
                    request.MappingSet.Key.Dialect,
                    ex,
                    _providerFailureExtractor
                )
            )
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                RelationshipAuthorizationProviderFailureMapper.InvalidFailurePayloadSecurityConfigurationError
            );
        }
    }

    private static RelationalCommand BuildCommand(
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(StringComparer.Ordinal)
        {
            [SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName] =
                request.DocumentId,
        };

        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            valuesByParameterName,
            request.ClaimEducationOrganizationIdParameterization
        );

        return new RelationalCommand(
            sqlPlan.AuthorizationSql,
            [
                .. sqlPlan.ParametersInOrder.Select(parameter =>
                    RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                        parameter,
                        valuesByParameterName[parameter.ParameterName],
                        parameterConfigurator
                    )
                ),
            ]
        );
    }

    private static async Task<SingleRecordRelationshipAuthorizationExecutionResult> ReadAuthorizedResultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget();
        }

        var authorizationResult = reader.GetRequiredFieldValue<int>(AuthorizationResultColumn);

        if (authorizationResult != 1)
        {
            return new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                $"Relationship authorization returned unexpected result '{authorizationResult}'."
            );
        }

        return new SingleRecordRelationshipAuthorizationExecutionResult.Authorized(
            reader.GetRequiredFieldValue<long>(ContentVersionColumn)
        );
    }
}

internal static class SingleRecordRelationshipAuthorizationSqlSpecDefaults
{
    public const string DocumentIdParameterName = "DocumentId";
}
