// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Relational implementation of <see cref="IChangeQueryRepository"/>. Reads the newest change
/// version from the dialect-specific GetMaxChangeVersion function.
/// </summary>
public sealed class RelationalChangeQueryRepository(
    IRelationalCommandExecutor commandExecutor,
    IRelationalParameterConfigurator parameterConfigurator
) : IChangeQueryRepository
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    private readonly IRelationalParameterConfigurator _parameterConfigurator =
        parameterConfigurator ?? throw new ArgumentNullException(nameof(parameterConfigurator));

    public Task<long> GetNewestChangeVersion(CancellationToken cancellationToken = default) =>
        _commandExecutor.ExecuteReaderAsync(
            ChangeVersionSqlProvider.NewestChangeVersionCommand(_commandExecutor.Dialect),
            static async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("GetMaxChangeVersion returned no rows.");
                }

                return reader.GetRequiredFieldValue<long>("NewestChangeVersion");
            },
            cancellationToken
        );

    public Task<TrackedChangeQueryResult> QueryTrackedChanges(
        ITrackedChangeQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request is not IRelationalTrackedChangeQueryRequest relationalRequest)
        {
            throw new NotSupportedException(
                "Tracked Change Queries require an IRelationalTrackedChangeQueryRequest."
            );
        }

        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredStrategies =
            ConfiguredAuthorizationStrategyAdapter.Adapt(relationalRequest.AuthorizationStrategyEvaluators);

        ReadChangesAuthorizationPlanOutcome authorizationOutcome = ReadChangesAuthorizationPlanner.Plan(
            relationalRequest.MappingSet,
            relationalRequest.ResourceModel,
            relationalRequest.TrackedChangeTable,
            configuredStrategies,
            relationalRequest.AuthorizationContext
        );

        switch (authorizationOutcome)
        {
            case ReadChangesAuthorizationPlanOutcome.SecurityConfiguration securityConfiguration:
                return Task.FromResult(
                    new TrackedChangeQueryResult(
                        [],
                        null,
                        new ChangeQueryAuthorizationFailure.SecurityConfiguration(
                            securityConfiguration.UnavailableStrategyNames,
                            securityConfiguration.Errors
                        )
                    )
                );
            case ReadChangesAuthorizationPlanOutcome.NamespaceNoPrefixesConfigured noPrefixes:
                return Task.FromResult(
                    new TrackedChangeQueryResult(
                        [],
                        null,
                        new ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured(
                            noPrefixes.StrategyName
                        )
                    )
                );
        }

        if (IsEmptyKeyChangesRequest(relationalRequest))
        {
            return Task.FromResult(
                new TrackedChangeQueryResult(
                    [],
                    relationalRequest.PaginationParameters.TotalCount ? 0L : null
                )
            );
        }

        ReadChangesAuthorizationPlan authorizationPlan = (
            (ReadChangesAuthorizationPlanOutcome.Plan)authorizationOutcome
        ).AuthorizationPlan;
        TrackedChangeAuthorizationSql authorizationSql = TrackedChangeAuthorizationSqlEmitter.Emit(
            authorizationPlan,
            _commandExecutor.Dialect,
            "c",
            _parameterConfigurator
        );

        IReadOnlyList<ChangeQueryResponseField> fields = ChangeQueryResponseFieldMapper.Map(
            relationalRequest.MappingSet,
            relationalRequest.ResourceModel,
            relationalRequest.TrackedChangeTable
        );

        var planner = new TrackedChangeQueryPlanner(_commandExecutor.Dialect);
        TrackedChangeQueryPlan plan = planner.Plan(relationalRequest, fields, authorizationSql);

        if (plan.IsEmpty)
        {
            return Task.FromResult(new TrackedChangeQueryResult([], plan.TotalCount));
        }

        if (
            BuildParameterBudgetAuthorizationFailure(relationalRequest, authorizationPlan, plan) is
            { } failure
        )
        {
            return Task.FromResult(new TrackedChangeQueryResult([], null, failure));
        }

        return _commandExecutor.ExecuteReaderAsync(
            plan.Command!,
            (reader, ct) =>
                TrackedChangeQueryRowReader.ReadAsync(
                    reader,
                    relationalRequest.Operation,
                    fields,
                    plan.IncludesTotalCount,
                    ct
                ),
            cancellationToken
        );
    }

    private static ChangeQueryAuthorizationFailure? BuildParameterBudgetAuthorizationFailure(
        IRelationalTrackedChangeQueryRequest request,
        ReadChangesAuthorizationPlan authorizationPlan,
        TrackedChangeQueryPlan queryPlan
    )
    {
        RelationalCommand command =
            queryPlan.Command
            ?? throw new InvalidOperationException(
                "A non-empty tracked-change query plan must include a relational command."
            );

        int authorizationParameterCount = AuthorizationParameterBudget.CountAuthorizationParameters(
            authorizationPlan.NamespaceParameterization,
            authorizationPlan.ClaimParameterization
        );
        int nonAuthorizationParameterCount = command.Parameters.Count - authorizationParameterCount;

        if (
            !AuthorizationParameterBudget.ExceedsCommandParameterLimit(
                request.MappingSet.Key.Dialect,
                authorizationPlan.NamespaceParameterization,
                authorizationPlan.ClaimParameterization,
                nonAuthorizationParameterCount
            )
        )
        {
            return null;
        }

        return new ChangeQueryAuthorizationFailure.SecurityConfiguration(
            [],
            [
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    authorizationPlan.NamespaceParameterization?.ConfiguredPrefixesInOrder.Count ?? 0,
                    authorizationPlan.ClaimParameterization?.ClaimEducationOrganizationIds.Count ?? 0,
                    nonAuthorizationParameterCount
                ),
            ]
        );
    }

    private static bool IsEmptyKeyChangesRequest(IRelationalTrackedChangeQueryRequest request) =>
        request.Operation is ChangeQueryEndpointOperation.KeyChanges
        && request.TrackedChangeTable.Kind
            is TrackedChangeTableKind.SharedDescriptor
                or TrackedChangeTableKind.ConcreteAbstract;
}
