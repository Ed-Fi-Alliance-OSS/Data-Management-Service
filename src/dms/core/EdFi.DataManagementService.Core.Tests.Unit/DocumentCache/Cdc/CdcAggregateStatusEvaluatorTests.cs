// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcAggregateStatus")]
public class Given_CdcAggregateStatusEvaluator
{
    private static readonly DateTimeOffset AggregateObservedAt = new(
        2026,
        8,
        18,
        14,
        12,
        0,
        TimeSpan.FromHours(-5)
    );

    [Test]
    public void It_returns_ready_when_all_targets_are_ready_and_orders_targets_by_normalized_identity()
    {
        CdcTargetStatus beta = Status(
            Target("dms-local", "default", "2", "data-store-2"),
            CdcReadiness.Ready,
            CdcBlockingCategory.None
        );
        CdcTargetStatus alpha = Status(
            Target("dms-local", "default", "1", "data-store-1"),
            CdcReadiness.Ready,
            CdcBlockingCategory.None
        );

        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(new(AggregateObservedAt, [beta, alpha]));

        aggregate.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        aggregate.ObservedAt.Should().Be(AggregateObservedAt.ToUniversalTime());
        aggregate.Readiness.Should().Be(CdcReadiness.Ready);
        aggregate.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.None);
        aggregate.Targets.Should().ContainInOrder(alpha, beta);
        aggregate.Targets[0].Should().BeSameAs(alpha);
        aggregate.Targets[1].Should().BeSameAs(beta);
    }

    [Test]
    public void It_returns_not_ready_when_any_target_is_not_ready_and_uses_blocking_precedence()
    {
        CdcTargetStatus lagExceeded = Status(
            Target("dms-local", "default", "1", "data-store-1"),
            CdcReadiness.NotReady,
            CdcBlockingCategory.LagExceeded
        );
        CdcTargetStatus projectionNonOperational = Status(
            Target("dms-local", "default", "2", "data-store-2"),
            CdcReadiness.NotReady,
            CdcBlockingCategory.ProjectionNonOperational
        );

        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(
            new(AggregateObservedAt, [lagExceeded, projectionNonOperational])
        );

        aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
        aggregate.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProjectionNonOperational);
        aggregate.Targets.Should().ContainInOrder(lagExceeded, projectionNonOperational);
    }

    [Test]
    public void It_returns_unknown_when_no_target_is_not_ready_and_at_least_one_target_is_unknown()
    {
        CdcTargetStatus ready = Status(
            Target("dms-local", "default", "1", "data-store-1"),
            CdcReadiness.Ready,
            CdcBlockingCategory.None
        );
        CdcTargetStatus providerHistoryUnknown = Status(
            Target("dms-local", "default", "2", "data-store-2"),
            CdcReadiness.Unknown,
            CdcBlockingCategory.ProviderHistoryUnknown
        );

        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(
            new(AggregateObservedAt, [providerHistoryUnknown, ready])
        );

        aggregate.Readiness.Should().Be(CdcReadiness.Unknown);
        aggregate.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProviderHistoryUnknown);
        aggregate.Targets.Should().ContainInOrder(ready, providerHistoryUnknown);
    }

    [Test]
    public void It_selects_highest_precedence_category_across_not_ready_and_unknown_targets()
    {
        CdcTargetStatus lagExceeded = Status(
            Target("dms-local", "default", "1", "data-store-1"),
            CdcReadiness.NotReady,
            CdcBlockingCategory.LagExceeded
        );
        CdcTargetStatus providerHistoryUnknown = Status(
            Target("dms-local", "default", "2", "data-store-2"),
            CdcReadiness.Unknown,
            CdcBlockingCategory.ProviderHistoryUnknown
        );

        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(
            new(AggregateObservedAt, [providerHistoryUnknown, lagExceeded])
        );

        aggregate.Readiness.Should().Be(CdcReadiness.NotReady);
        aggregate.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.ProviderHistoryUnknown);
    }

    [Test]
    public void It_fails_closed_for_an_empty_target_set()
    {
        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(new(AggregateObservedAt, []));

        aggregate.Readiness.Should().Be(CdcReadiness.Unknown);
        aggregate.PrimaryBlockingCategory.Should().Be(CdcBlockingCategory.StatusObservationUnavailable);
        aggregate.Targets.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_every_target_result_and_per_target_diagnostic_unchanged()
    {
        CdcDiagnostic diagnostic = new(
            CdcDiagnosticCategory.InvalidObservation,
            "$.projection",
            "projection status unavailable"
        );
        CdcTargetStatus target = Status(
            Target("dms-local", "default", "1", "data-store-1"),
            CdcReadiness.Unknown,
            CdcBlockingCategory.StatusObservationUnavailable,
            [diagnostic]
        );

        CdcStatus aggregate = CdcAggregateStatusEvaluator.Evaluate(new(AggregateObservedAt, [target]));

        aggregate.Targets.Should().ContainSingle().Which.Should().BeSameAs(target);
        aggregate.Targets[0].Diagnostics.Should().ContainSingle().Which.Should().BeSameAs(diagnostic);
    }

    private static CdcTargetIdentity Target(
        string deploymentKey,
        string tenantKey,
        string dataStoreId,
        string instanceKey,
        long generation = 1,
        CdcProvider provider = CdcProvider.Postgresql
    ) => new(deploymentKey, tenantKey, dataStoreId, instanceKey, generation, provider);

    private static CdcTargetStatus Status(
        CdcTargetIdentity targetIdentity,
        CdcReadiness readiness,
        CdcBlockingCategory primaryBlockingCategory,
        IReadOnlyList<CdcDiagnostic>? diagnostics = null
    )
    {
        CdcComponent satisfied = CdcComponent.Satisfied(
            CdcTargetStatusFixture.ObservationObservedAt,
            "satisfied"
        );

        return new(
            targetIdentity,
            readiness,
            primaryBlockingCategory,
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            CdcSourceHistoryComponent.FromComponent(
                satisfied,
                CdcSourceHistoryContinuity.Healthy,
                incidentLatched: false
            ),
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            satisfied,
            diagnostics ?? []
        );
    }
}
