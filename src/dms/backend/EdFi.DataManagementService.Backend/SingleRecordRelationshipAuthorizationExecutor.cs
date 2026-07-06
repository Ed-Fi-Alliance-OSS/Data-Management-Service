// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    public sealed record InvalidAuthorizationFailure(
        string FailureMessage,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : SingleRecordRelationshipAuthorizationExecutionResult;
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
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
    ILogger? logger = null
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
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<SingleRecordRelationshipAuthorizationExecutionResult> ExecuteAsync(
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sqlPlan = SingleRecordRelationshipAuthorizationSqlCompiler.CompileCached(
            request.MappingSet,
            new SingleRecordRelationshipAuthorizationSqlSpec(
                request.CheckSpecs,
                request.ClaimEducationOrganizationIdParameterization,
                request.EmittedAuth1Index
            )
        );
        if (sqlPlan.ProposedValueParametersInOrder.Count > 0)
        {
            throw new InvalidOperationException(
                "Single-record relationship authorization executor cannot execute proposed-value checks without extracted runtime values."
            );
        }

        try
        {
            var result = await _commandExecutor
                .ExecuteReaderAsync(
                    BuildCommand(sqlPlan, request, _parameterConfigurator),
                    ReadAuthorizedResultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (
                result is SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure
                {
                    Diagnostics: null,
                } invalidFailure
            )
            {
                return invalidFailure with
                {
                    Diagnostics =
                        AuthorizationSecurityConfigurationDiagnostics.ForRelationshipInvalidAuthorizationResult(
                            request.CheckSpecs
                        ),
                };
            }

            return result;
        }
        catch (DbException ex)
        {
            if (
                RelationshipAuthorizationProviderFailureMapper.TryMapRelationshipAuthorizationFailure(
                    request.MappingSet.Key.Dialect,
                    ex,
                    _providerFailureExtractor,
                    request.EmittedAuth1Index,
                    request.CheckSpecs,
                    request.ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds,
                    out var relationshipFailure,
                    out var invalidFailureDiagnostic
                )
            )
            {
                return new SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized(
                    relationshipFailure!
                );
            }

            if (invalidFailureDiagnostic is not null)
            {
                RelationshipAuthorizationProviderFailureMapper.LogInvalidFailurePayload(
                    _logger,
                    invalidFailureDiagnostic
                );

                return new SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure(
                    RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError,
                    AuthorizationSecurityConfigurationDiagnostics.ForRelationshipAuthorizationAuth1(
                        invalidFailureDiagnostic,
                        request.CheckSpecs
                    )
                );
            }

            throw;
        }
    }

    private static RelationalCommand BuildCommand(
        SingleRecordRelationshipAuthorizationSqlPlan sqlPlan,
        SingleRecordRelationshipAuthorizationExecutionRequest request,
        IRelationalParameterConfigurator parameterConfigurator
    )
    {
        Dictionary<string, object?> valuesByParameterName = new(
            sqlPlan.ParametersInOrder.Count,
            StringComparer.Ordinal
        )
        {
            [SingleRecordRelationshipAuthorizationSqlSpecDefaults.DocumentIdParameterName] =
                request.DocumentId,
        };

        RelationshipAuthorizationCommandParameterBuilder.AddAuthorizationParameterValues(
            valuesByParameterName,
            request.ClaimEducationOrganizationIdParameterization
        );

        List<RelationalParameter> parameters = new(sqlPlan.ParametersInOrder.Count);

        foreach (var parameter in sqlPlan.ParametersInOrder)
        {
            parameters.Add(
                RelationshipAuthorizationCommandParameterBuilder.BuildParameter(
                    parameter,
                    valuesByParameterName[parameter.ParameterName],
                    parameterConfigurator
                )
            );
        }

        return new RelationalCommand(sqlPlan.AuthorizationSql, parameters);
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
