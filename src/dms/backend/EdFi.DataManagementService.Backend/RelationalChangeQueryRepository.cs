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

    public async Task<TrackedChangeQueryResult> QueryTrackedChanges(
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
                return new TrackedChangeQueryResult(
                    [],
                    null,
                    new ChangeQueryAuthorizationFailure.SecurityConfiguration(
                        securityConfiguration.UnavailableStrategyNames,
                        securityConfiguration.Errors
                    )
                );
            case ReadChangesAuthorizationPlanOutcome.NamespaceNoPrefixesConfigured noPrefixes:
                return new TrackedChangeQueryResult(
                    [],
                    null,
                    new ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured(noPrefixes.StrategyName)
                );
            case ReadChangesAuthorizationPlanOutcome.CustomViewSecurityConfiguration customViewFailure:
                // Custom views are AND filters in CMS order: validate the views that planned and are configured
                // ahead of the earliest planning failure first, so an earlier missing or non-conforming view
                // surfaces its own error instead of being masked by this later planning failure.
                await ValidateCustomViewsAsync(
                        relationalRequest,
                        CustomViewAuthorizationTerminalOrdering.ChecksBeforeTerminal(
                            customViewFailure.PlannedChecks,
                            RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                                customViewFailure.Failures
                            )
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return new TrackedChangeQueryResult(
                    [],
                    null,
                    RelationalReadGuardrails.BuildChangeQueryCustomViewSecurityConfigurationFailure(
                        customViewFailure.Failures
                    )
                );
        }

        ReadChangesAuthorizationPlan authorizationPlan = (
            (ReadChangesAuthorizationPlanOutcome.Plan)authorizationOutcome
        ).AuthorizationPlan;

        // Validate every configured custom view before any terminal, including the row-free key-change
        // shortcut below: a misconfigured view must produce its urn:ed-fi:api:system 500 rather than a
        // silent empty 200.
        await ValidateCustomViewsAsync(
                relationalRequest,
                authorizationPlan.CustomViewChecks,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (IsEmptyKeyChangesRequest(relationalRequest))
        {
            return new TrackedChangeQueryResult(
                [],
                relationalRequest.PaginationParameters.TotalCount ? 0L : null
            );
        }

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
            return new TrackedChangeQueryResult([], plan.TotalCount);
        }

        if (
            BuildParameterBudgetAuthorizationFailure(relationalRequest, authorizationPlan, plan) is
            { } failure
        )
        {
            return new TrackedChangeQueryResult([], null, failure);
        }

        return await _commandExecutor
            .ExecuteReaderAsync(
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
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the resolved custom views of this request with the same per-request validator the live read
    /// paths use, so a missing view or one without a <c>bigint DocumentId</c> column raises
    /// <see cref="CustomViewAuthorizationValidationException"/> (the <c>urn:ed-fi:api:system</c> 500) before any
    /// SQL that selects through it is emitted. Tombstone probe arms read DMS-owned tables and need no validation.
    /// </summary>
    private Task ValidateCustomViewsAsync(
        IRelationalTrackedChangeQueryRequest request,
        IReadOnlyList<ReadChangesCustomViewCheckSpec> customViewChecks,
        CancellationToken cancellationToken
    )
    {
        if (customViewChecks.Count == 0)
        {
            return Task.CompletedTask;
        }

        return CustomViewAuthorizationValidator.ValidateAsync(
            _commandExecutor,
            _commandExecutor.Dialect,
            ReadChangesCustomViewValidationAdapter.Adapt(
                request.ResourceModel.RelationalModel.Root.Table,
                customViewChecks
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
        // Everything the command binds beyond the namespace and claim lists — paging, the change-version
        // window, the deletes query's own descriptor discriminators, and the custom-view descriptor
        // discriminators (two per descriptor identity part or descriptor basis; custom views bind no claim
        // parameters) — spends the same ceiling and is counted here.
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
