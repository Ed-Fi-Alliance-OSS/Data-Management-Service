// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentProjectionWorkPaging")]
public class Given_DocumentProjectionWorkPaging
{
    private static readonly DateTimeOffset FirstEnqueuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterEnqueuedAt = FirstEnqueuedAt.AddSeconds(1);

    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation TrackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    [Test]
    public async Task It_reads_from_a_null_cursor_and_advances_to_the_last_seen_work_row()
    {
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [
                Page(
                    WorkItem(101, requiredContentVersion: 10, FirstEnqueuedAt),
                    WorkItem(102, requiredContentVersion: 11, LaterEnqueuedAt)
                ),
            ]
        );
        DocumentCacheProjectionDrainPageProcessor sut = CreateProcessor(pager);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql
        );

        DocumentCacheProjectionDrainPageResult result = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );

        result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        result.ProcessedItemCount.Should().Be(2);
        targetContext.Cursor.LastDocumentId.Should().Be(102);
        targetContext.Cursor.LastFirstEnqueuedAt.Should().Be(LaterEnqueuedAt);
        pager
            .Calls.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new PagingCall(HasCursor: false, null, null, 3));
    }

    [Test]
    public async Task It_wraps_by_clearing_a_non_null_cursor_after_an_empty_page()
    {
        ScriptedWorkPager pager = new(
            RelationalProviderToken.Postgresql,
            [Page(), Page(WorkItem(101, requiredContentVersion: 10, FirstEnqueuedAt))]
        );
        DocumentCacheProjectionDrainPageProcessor sut = CreateProcessor(pager);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql
        );
        targetContext.Cursor.Advance(LaterEnqueuedAt, 102);

        DocumentCacheProjectionDrainPageResult result = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );

        result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
        result.ProcessedItemCount.Should().Be(1);
        targetContext.Cursor.LastDocumentId.Should().Be(101);
        targetContext.Cursor.LastFirstEnqueuedAt.Should().Be(FirstEnqueuedAt);
        pager
            .Calls.Should()
            .Equal(
                new PagingCall(HasCursor: true, LaterEnqueuedAt, 102, 3),
                new PagingCall(HasCursor: false, null, null, 3)
            );
    }

    [Test]
    public async Task It_reports_no_eligible_work_when_the_wrapped_page_is_also_empty()
    {
        ScriptedWorkPager pager = new(RelationalProviderToken.Postgresql, [Page(), Page()]);
        DocumentCacheProjectionDrainPageProcessor sut = CreateProcessor(pager);
        DocumentCacheProjectionTargetRuntimeContext targetContext = RuntimeContext(
            RelationalProviderToken.Postgresql
        );
        targetContext.Cursor.Advance(LaterEnqueuedAt, 102);

        DocumentCacheProjectionDrainPageResult result = await sut.ProcessPageAsync(
            new DocumentCacheProjectionDrainPageRequest(
                targetContext,
                DocumentCacheProjectionDrainInvocationKind.Ordinary
            )
        );

        result.Should().BeSameAs(DocumentCacheProjectionDrainPageResult.NoEligibleWork);
        targetContext.Cursor.HasValue.Should().BeFalse();
        pager
            .Calls.Should()
            .Equal(
                new PagingCall(HasCursor: true, LaterEnqueuedAt, 102, 3),
                new PagingCall(HasCursor: false, null, null, 3)
            );
    }

    [Test]
    public void It_uses_provider_specific_keyset_sql_without_work_row_locks_or_source_joins()
    {
        PostgresqlDocumentProjectionWorkPager
            .CursorPageSql.Should()
            .Contain(
                """WHERE (work."FirstEnqueuedAt", work."DocumentId") > (@lastFirstEnqueuedAt, @lastDocumentId)"""
            );
        PostgresqlDocumentProjectionWorkPager.CursorPageSql.Should().Contain("LIMIT @pageSize");
        PostgresqlDocumentProjectionWorkPager.InitialPageSql.Should().Contain("LIMIT @pageSize");

        MssqlDocumentProjectionWorkPager
            .CursorPageSql.Should()
            .Contain("[work].[FirstEnqueuedAt] > @lastFirstEnqueuedAt");
        MssqlDocumentProjectionWorkPager
            .CursorPageSql.Should()
            .Contain(
                "([work].[FirstEnqueuedAt] = @lastFirstEnqueuedAt AND [work].[DocumentId] > @lastDocumentId)"
            );
        MssqlDocumentProjectionWorkPager
            .CursorPageSql.Should()
            .Contain("OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY");
        MssqlDocumentProjectionWorkPager
            .InitialPageSql.Should()
            .Contain("OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY");

        string combinedSql =
            PostgresqlDocumentProjectionWorkPager.InitialPageSql
            + PostgresqlDocumentProjectionWorkPager.CursorPageSql
            + MssqlDocumentProjectionWorkPager.InitialPageSql
            + MssqlDocumentProjectionWorkPager.CursorPageSql;
        string normalizedSql = combinedSql.ToUpperInvariant();
        normalizedSql.Should().NotContain(" JOIN ");
        normalizedSql.Should().NotContain("DOCUMENTCACHE");
        normalizedSql.Should().NotContain(" FOR UPDATE");
        normalizedSql.Should().NotContain(" UPDLOCK");
        normalizedSql.Should().NotContain(" HOLDLOCK");
    }

    private static DocumentCacheProjectionDrainPageProcessor CreateProcessor(
        IDocumentProjectionWorkPager pager
    ) =>
        new(
            pager,
            new AcknowledgingItemProcessor(),
            NullLogger<DocumentCacheProjectionDrainPageProcessor>.Instance,
            new FixedTimeProvider(FirstEnqueuedAt)
        );

    private static DocumentProjectionWorkPage Page(params DocumentProjectionWorkPageItem[] items) =>
        new(items, pageSize: 3);

    private static DocumentProjectionWorkPageItem WorkItem(
        long documentId,
        long requiredContentVersion,
        DateTimeOffset firstEnqueuedAt
    ) => new(documentId, requiredContentVersion, firstEnqueuedAt, firstEnqueuedAt.AddSeconds(5));

    private static DocumentCacheProjectionTargetRuntimeContext RuntimeContext(
        RelationalProviderToken providerToken
    )
    {
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create("Tenant-A", 1);
        DocumentCacheTargetExecutionContext executionContext = new(
            targetKey,
            new DocumentCacheTargetContextGeneration(1),
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 3,
                projectorMaxConcurrentTargets: 2,
                projectorFailureBackoff: TimeSpan.FromSeconds(10),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, providerToken.Value),
            new DocumentCacheTargetConnectionInput(providerToken, "connection"),
            Fingerprint,
            TrackingLifecycle,
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

        return new DocumentCacheProjectionTargetRuntimeContext(
            executionContext,
            new DocumentCacheProjectionTargetProviderAdapters(
                providerToken,
                MaterializationTargetContext(targetKey, providerToken),
                new StubDocumentCacheMaterializer(),
                new StubDocumentCacheWriter()
            ),
            new NoOpObservationSink()
        );
    }

    private static DocumentCacheMaterializationTargetContext MaterializationTargetContext(
        DocumentCacheTargetKey targetKey,
        RelationalProviderToken providerToken
    ) =>
        new(
            new DocumentCacheProjectionTargetKey(targetKey.TenantKey, new DataStoreId(targetKey.DataStoreId)),
            MappingSet(providerToken),
            DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
            "connection"
        );

    private static MappingSet MappingSet(RelationalProviderToken providerToken)
    {
        SqlDialect dialect =
            providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;
        EffectiveSchemaInfo effectiveSchema = new(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "v2",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                dialect,
                effectiveSchema.RelationalMappingVersion
            ),
            new DerivedRelationalModelSet(effectiveSchema, dialect, [], [], [], [], [], []),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private sealed class AcknowledgingItemProcessor : IDocumentCacheProjectionItemProcessor
    {
        public Task<DocumentCacheProjectionItemProcessResult> ProcessItemAsync(
            DocumentCacheProjectionItemProcessRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.TargetContext.FailureBackoffState.ClearFailure(request.WorkItem.DocumentId);
            return Task.FromResult(DocumentCacheProjectionItemProcessResult.Continue);
        }
    }

    private sealed class ScriptedWorkPager(
        RelationalProviderToken providerToken,
        IEnumerable<DocumentProjectionWorkPage> pages
    ) : IDocumentProjectionWorkPager
    {
        private readonly Queue<DocumentProjectionWorkPage> _pages = new(pages);

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public List<PagingCall> Calls { get; } = [];

        public Task<DocumentProjectionWorkPage> ReadPageAsync(
            DocumentProjectionWorkPageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                new PagingCall(
                    request.Cursor.HasValue,
                    request.Cursor.LastFirstEnqueuedAt,
                    request.Cursor.LastDocumentId,
                    request.PageSize
                )
            );

            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed record PagingCall(
        bool HasCursor,
        DateTimeOffset? LastFirstEnqueuedAt,
        long? LastDocumentId,
        int PageSize
    );

    private sealed class StubDocumentCacheMaterializer : IDocumentCacheMaterializer
    {
        public Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        ) => throw new NotImplementedException();
    }

    private sealed class StubDocumentCacheWriter : IDocumentCacheWriter
    {
        public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request) =>
            throw new NotImplementedException();
    }

    private sealed class NoOpObservationSink : IDocumentCacheProjectionObservationSink
    {
        public void ObserveTarget(DocumentCacheProjectionTargetHealthSnapshot snapshot) => _ = snapshot;

        public void EndTargetContext(
            DocumentCacheProjectionTargetContextKey contextKey,
            DocumentCacheProjectionTargetEndReason endReason,
            DateTimeOffset? endedAt = null
        ) => _ = (contextKey, endReason, endedAt);

        public void ObserveAdministrativeCommand(
            DocumentCacheAdministrativeCommandObservationSnapshot snapshot
        ) => _ = snapshot;

        public void EndAdministrativeCommand(DocumentCacheAdministrativeCommandExecutionId executionId) =>
            _ = executionId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
