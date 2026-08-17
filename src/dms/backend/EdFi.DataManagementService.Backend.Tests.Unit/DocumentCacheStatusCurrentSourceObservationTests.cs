// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheStatus")]
public class Given_DocumentCacheStatusCurrentSourceObservation
{
    private static readonly DateTimeOffset DurableObservedAt = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OldestWorkFirstEnqueuedAt = DurableObservedAt.AddMinutes(-5);

    [Test]
    public void It_allows_successful_empty_queue_observations_without_oldest_work_facts()
    {
        DocumentCacheStatusCurrentSourceObservationResult result =
            DocumentCacheStatusCurrentSourceObservationResult.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusDurableQueuePresence.Empty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            );

        result.Succeeded.Should().BeTrue();
        result.HasWork.Should().BeFalse();
        result.LifecycleState.Should().Be(DocumentCacheLifecycleState.Tracking);
        result.CacheAheadRecoveryRequired.Should().BeFalse();
        result.DurableObservedAt.Should().Be(DurableObservedAt);
    }

    [Test]
    public void It_allows_successful_nonempty_queue_observations_with_provider_computed_age()
    {
        DocumentCacheStatusCurrentSourceObservationResult result =
            DocumentCacheStatusCurrentSourceObservationResult.Success(
                DocumentCacheLifecycleState.Rebuilding,
                cacheAheadRecoveryRequired: true,
                DocumentCacheStatusDurableQueuePresence.NotEmpty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                DurableObservedAt
            );

        result.HasWork.Should().BeTrue();
        result.OldestWorkFirstEnqueuedAt.Should().Be(OldestWorkFirstEnqueuedAt);
        result.OldestWorkAgeSeconds.Should().Be(300);
    }

    [Test]
    public void It_rejects_empty_queue_observations_with_oldest_work_facts()
    {
        Action create = () =>
            DocumentCacheStatusCurrentSourceObservationResult.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusDurableQueuePresence.Empty,
                OldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds: 300,
                DurableObservedAt
            );

        create.Should().Throw<ArgumentException>().WithMessage("*Empty queue*oldest-work*");
    }

    [Test]
    public void It_rejects_nonempty_queue_observations_without_oldest_work_facts()
    {
        Action create = () =>
            DocumentCacheStatusCurrentSourceObservationResult.Success(
                DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                DocumentCacheStatusDurableQueuePresence.NotEmpty,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DurableObservedAt
            );

        create.Should().Throw<ArgumentException>().WithMessage("*Non-empty queue*oldest-work*");
    }

    [Test]
    public void It_returns_failed_outcomes_without_stale_durable_facts()
    {
        DocumentCacheStatusCurrentSourceObservationResult result =
            DocumentCacheStatusCurrentSourceObservationResult.ProviderTimeout("timed out");

        result.Outcome.Should().Be(DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout);
        result.LifecycleState.Should().BeNull();
        result.CacheAheadRecoveryRequired.Should().BeNull();
        result.QueuePresence.Should().BeNull();
        result.OldestWorkFirstEnqueuedAt.Should().BeNull();
        result.OldestWorkAgeSeconds.Should().BeNull();
        result.DurableObservedAt.Should().BeNull();
    }

    [Test]
    public void It_uses_one_postgresql_status_statement_with_ordered_single_row_work_access()
    {
        string sql = PostgresqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql;
        string normalizedSql = sql.ToUpperInvariant();

        sql.Should().Contain("statement_timestamp()");
        sql.Should().Contain("""FROM "dms"."DocumentCacheState" AS state""");
        sql.Should().Contain("""FROM "dms"."DocumentProjectionWork" AS work""");
        sql.Should().Contain("ORDER BY work.\"FirstEnqueuedAt\", work.\"DocumentId\"");
        sql.Should().Contain("LIMIT 1");
        sql.Should().Contain("EXTRACT(EPOCH FROM");
        normalizedSql.Should().NotContain("COUNT(");
        normalizedSql.Should().NotContain("COUNT (");
        sql.Should().NotContain("""FROM "dms"."Document" """);
        sql.Should().NotContain("""JOIN "dms"."Document" """);
        sql.Should().NotContain("""FROM "dms"."DocumentCache" """);
        sql.Should().NotContain("""JOIN "dms"."DocumentCache" """);
    }

    [Test]
    public void It_renders_postgresql_identifiers_from_the_document_cache_inventory()
    {
        string sql = PostgresqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql;

        sql.Should()
            .Contain(
                SqlIdentifierQuoter.QuoteTableName(
                    SqlDialect.Pgsql,
                    DocumentCacheInventoryDefinition.DocumentCacheState
                )
            );
        sql.Should()
            .Contain(
                SqlIdentifierQuoter.QuoteTableName(
                    SqlDialect.Pgsql,
                    DocumentCacheInventoryDefinition.DocumentProjectionWork
                )
            );

        DbColumnName[] columns =
        [
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId,
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt,
        ];
        foreach (DbColumnName column in columns)
        {
            sql.Should().Contain(SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Pgsql, column));
        }
    }

    [Test]
    public void It_uses_one_sql_server_status_statement_with_ordered_single_row_work_access()
    {
        string sql = MssqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql;
        string normalizedSql = sql.ToUpperInvariant();

        sql.Should().Contain("SYSUTCDATETIME()");
        sql.Should().Contain("FROM [dms].[DocumentCacheState] AS [state]");
        sql.Should().Contain("FROM [dms].[DocumentProjectionWork] AS [work]");
        sql.Should().Contain("SELECT TOP (1) [work].[DocumentId], [work].[FirstEnqueuedAt]");
        sql.Should().Contain("ORDER BY [work].[FirstEnqueuedAt], [work].[DocumentId]");
        sql.Should().Contain("DATEDIFF_BIG(NANOSECOND");
        normalizedSql.Should().NotContain("COUNT(");
        normalizedSql.Should().NotContain("COUNT (");
        sql.Should().NotContain("FROM [dms].[Document] ");
        sql.Should().NotContain("JOIN [dms].[Document] ");
        sql.Should().NotContain("FROM [dms].[DocumentCache] ");
        sql.Should().NotContain("JOIN [dms].[DocumentCache] ");
    }

    [Test]
    public void It_renders_sql_server_identifiers_from_the_document_cache_inventory()
    {
        string sql = MssqlDocumentCacheStatusCurrentSourceObserver.StatusObservationSql;

        sql.Should()
            .Contain(
                SqlIdentifierQuoter.QuoteTableName(
                    SqlDialect.Mssql,
                    DocumentCacheInventoryDefinition.DocumentCacheState
                )
            );
        sql.Should()
            .Contain(
                SqlIdentifierQuoter.QuoteTableName(
                    SqlDialect.Mssql,
                    DocumentCacheInventoryDefinition.DocumentProjectionWork
                )
            );

        DbColumnName[] columns =
        [
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired,
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.DocumentId,
            DocumentCacheInventoryDefinition.DocumentProjectionWorkColumns.FirstEnqueuedAt,
        ];
        foreach (DbColumnName column in columns)
        {
            sql.Should().Contain(SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, column));
        }
    }
}
