// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheReadLookup")]
public class Given_DocumentCacheReadLookup
{
    private const short ResourceKeyId = 1;

    private static readonly Guid DocumentGuid = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
    private static readonly DateTimeOffset LastModifiedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly QualifiedResourceName Resource = new("Ed-Fi", "Student");
    private static readonly MappingSet MappingSet = RelationalAccessTestData.CreateMappingSet(Resource);
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("TenantA", 7);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    [Test]
    public void It_classifies_a_fresh_cache_hit()
    {
        DocumentCacheReadAccelerationCandidate candidate = Candidate();

        DocumentCacheReadBatchLookupResult result = Classify(candidate, Observation(candidate));

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.IsFreshHit.Should().BeTrue();
        var hit = result
            .Documents.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>()
            .Subject;
        hit.Candidate.Should().Be(candidate);
        hit.DocumentJson.Should().Be("""{"id":"cached"}""");
        hit.StreamEtag.Should().Be("stream-91");
        hit.CacheLastModifiedAt.Should().Be(candidate.ContentLastModifiedAt);
    }

    [Test]
    public void It_classifies_a_fresh_batch_hit_in_candidate_order()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );

        DocumentCacheReadBatchLookupResult result = Classify(
            [first, second],
            [Observation(first, ordinal: 0), Observation(second, ordinal: 1)]
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.IsFreshHit.Should().BeTrue();
        result.Documents.Select(static document => document.Candidate).Should().Equal(first, second);
        result.Documents.Should().AllBeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>();
    }

    [Test]
    public void It_classifies_a_batch_as_non_fresh_when_one_document_is_stale()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );

        DocumentCacheReadBatchLookupResult result = Classify(
            [first, second],
            [
                Observation(first, ordinal: 0),
                Observation(second, ordinal: 1) with
                {
                    CacheContentVersion = second.ContentVersion - 1,
                },
            ]
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.StaleCacheRow);
        result.IsFreshHit.Should().BeFalse();
        result.Documents[0].Should().BeOfType<DocumentCacheReadDocumentLookupResult.FreshHit>();
        var fallback = result
            .Documents[1]
            .Should()
            .BeOfType<DocumentCacheReadDocumentLookupResult.Fallback>()
            .Subject;
        fallback.Outcome.Should().Be(DocumentCacheReadLookupOutcome.StaleCacheRow);
        fallback.Candidate.Should().Be(second);
    }

    [Test]
    public void It_classifies_bounded_document_fallback_outcomes()
    {
        foreach (DocumentFallbackScenario scenario in DocumentFallbackScenarios())
        {
            using var scope = new AssertionScope(scenario.Name);
            DocumentCacheReadAccelerationCandidate candidate = Candidate();

            DocumentCacheReadBatchLookupResult result = Classify(
                candidate,
                scenario.Mutate(Observation(candidate))
            );

            result.Outcome.Should().Be(scenario.ExpectedOutcome);
            result.IsFreshHit.Should().BeFalse();
            var fallback = result
                .Documents.Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<DocumentCacheReadDocumentLookupResult.Fallback>()
                .Subject;
            fallback.Outcome.Should().Be(scenario.ExpectedOutcome);
            fallback.Candidate.Should().Be(candidate);
            fallback.Message.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Test]
    public void It_returns_an_invariant_fallback_when_the_candidate_resource_key_is_not_in_the_mapping_set()
    {
        DocumentCacheReadAccelerationCandidate baseCandidate = Candidate();
        DocumentCacheReadAccelerationCandidate candidate = baseCandidate with { ResourceKeyId = 99 };
        DocumentCacheReadLookupObservation observation = Observation(baseCandidate) with
        {
            ExpectedResourceKeyId = candidate.ResourceKeyId,
            SourceResourceKeyId = candidate.ResourceKeyId,
        };

        DocumentCacheReadBatchLookupResult result = Classify(candidate, observation);

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public void It_rejects_lookup_rows_that_do_not_match_the_requested_candidate_batch()
    {
        DocumentCacheReadAccelerationCandidate candidate = Candidate();
        DocumentCacheReadLookupObservation mismatched = Observation(candidate) with
        {
            ExpectedContentVersion = candidate.ContentVersion + 1,
        };

        Action act = () => Classify(candidate, mismatched);

        act.Should()
            .Throw<DocumentCacheReadLookupInvariantException>()
            .WithMessage("DocumentCache read lookup returned rows that do not match*");
    }

    [Test]
    public void It_builds_one_postgresql_lookup_command_for_the_candidate_batch()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );

        RelationalCommand command = DocumentCacheReadLookupSql.BuildCommand(
            SqlDialect.Pgsql,
            [first, second]
        );

        command.CommandText.Should().Contain("\"dms\".\"DocumentCache\"");
        command.CommandText.Should().Contain("CAST(@documentUuid0 AS uuid)");
        command.CommandText.Should().Contain("ORDER BY requested.\"Ordinal\"");
        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal(
                "@ordinal0",
                "@documentId0",
                "@documentUuid0",
                "@resourceKeyId0",
                "@contentVersion0",
                "@ordinal1",
                "@documentId1",
                "@documentUuid1",
                "@resourceKeyId1",
                "@contentVersion1"
            );
        command
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(
                0,
                first.DocumentId,
                first.DocumentUuid.Value,
                first.ResourceKeyId,
                first.ContentVersion,
                1,
                second.DocumentId,
                second.DocumentUuid.Value,
                second.ResourceKeyId,
                second.ContentVersion
            );
    }

    [Test]
    public void It_builds_one_mssql_lookup_command_for_the_candidate_batch()
    {
        RelationalCommand command = DocumentCacheReadLookupSql.BuildCommand(SqlDialect.Mssql, [Candidate()]);

        command.CommandText.Should().Contain("[dms].[DocumentCache]");
        command.CommandText.Should().Contain("CAST(@documentUuid0 AS uniqueidentifier)");
        command.CommandText.Should().Contain("ORDER BY [requested].[Ordinal]");
        command.Parameters.Should().HaveCount(5);
    }

    [Test]
    public async Task It_returns_target_ineligible_without_opening_the_cache_lookup()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new InvalidOperationException("Cache lookup should not execute.")
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(inventoryStatus: DocumentCacheInventoryStatus.Missing)
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.ProjectionTargetIneligible);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
        adapter.ExecuteAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_returns_an_empty_batch_without_opening_the_cache_lookup()
    {
        var adapter = new ObservationLookupAdapter(RelationalProviderToken.Postgresql, []);

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, []),
            ExecutionContext()
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.FreshHit);
        result.Documents.Should().BeEmpty();
        adapter.ExecuteAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_shapes_query_results_only_when_the_full_batch_is_fresh()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );
        var cachedResult = new QueryResult.QuerySuccess(
            [JsonNode.Parse("""{"id":"cached"}""")!],
            TotalCount: 2
        );
        var responseShaper = new RecordingResponseShaper
        {
            QueryResult = DocumentCacheReadLookupResult<QueryResult>.Hit(cachedResult),
        };
        var adapter = new ObservationLookupAdapter(
            RelationalProviderToken.Postgresql,
            [Observation(first, ordinal: 0), Observation(second, ordinal: 1)],
            responseShaper
        );

        DocumentCacheReadLookupResult<QueryResult> result = await adapter.TryQueryAsync(
            QueryRequest(new DocumentCacheReadAccelerationCandidatePage([first, second], 2, 346)),
            ExecutionContext()
        );

        result.CachedResult.Should().BeSameAs(cachedResult);
        adapter.ExecuteAttempts.Should().Be(1);
        responseShaper.QueryShapeAttempts.Should().Be(1);
        responseShaper
            .LastHitPage!.Documents.Select(static document => document.Candidate)
            .Should()
            .Equal(first, second);
    }

    [Test]
    public async Task It_falls_back_for_the_whole_query_page_when_any_batch_document_is_not_fresh()
    {
        DocumentCacheReadAccelerationCandidate first = Candidate(documentId: 345, contentVersion: 91);
        DocumentCacheReadAccelerationCandidate second = Candidate(
            documentId: 346,
            contentVersion: 92,
            documentUuid: Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
        );
        var responseShaper = new RecordingResponseShaper();
        var adapter = new ObservationLookupAdapter(
            RelationalProviderToken.Postgresql,
            [
                Observation(first, ordinal: 0),
                Observation(second, ordinal: 1) with
                {
                    CacheContentVersion = second.ContentVersion - 1,
                },
            ],
            responseShaper
        );

        DocumentCacheReadLookupResult<QueryResult> result = await adapter.TryQueryAsync(
            QueryRequest(new DocumentCacheReadAccelerationCandidatePage([first, second], 2, 346)),
            ExecutionContext()
        );

        result.CachedResult.Should().BeNull();
        result.FallbackReason.Should().Be(DocumentCacheReadAccelerationFallbackReason.CacheLookupStale);
        result.DirectFillCandidates.Should().Equal(second);
        adapter.ExecuteAttempts.Should().Be(1);
        responseShaper.QueryShapeAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_returns_provider_prerequisite_ineligible_without_opening_the_cache_lookup()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.SqlServer,
            new InvalidOperationException("Cache lookup should not execute.")
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(
                providerToken: RelationalProviderToken.SqlServer,
                sqlServerPrerequisites: FailedSqlServerPrerequisites()
            )
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.ProviderPrerequisiteIneligible);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
        adapter.ExecuteAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_provider_classified_availability_failures()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new TimeoutException("timeout"),
            classifyAsCacheUnavailable: true
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext()
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
        adapter.ExecuteAttempts.Should().Be(1);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_postgresql_connection_string_parse_failures()
    {
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var adapter = new PostgresqlDocumentCacheReadLookupAdapter(
            dataSourceCache,
            new PostgresqlRelationalWriteExceptionClassifier(),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(connectionString: "UnknownKeyword=not-supported")
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_postgresql_connection_construction_failures()
    {
        var adapter = new PostgresqlDocumentCacheReadLookupAdapter(
            (_, _) => Task.FromException<NpgsqlConnection>(new ArgumentException("bad target")),
            new PostgresqlRelationalWriteExceptionClassifier(),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext()
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_postgresql_connection_open_failures()
    {
        var adapter = new PostgresqlDocumentCacheReadLookupAdapter(
            (_, _) => Task.FromException<NpgsqlConnection>(new NpgsqlException("open failed")),
            new PostgresqlRelationalWriteExceptionClassifier(),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext()
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_propagates_postgresql_connection_acquisition_programming_exceptions()
    {
        var adapter = new PostgresqlDocumentCacheReadLookupAdapter(
            (_, _) => Task.FromException<NpgsqlConnection>(new InvalidOperationException("programming")),
            new PostgresqlRelationalWriteExceptionClassifier(),
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<PostgresqlDocumentCacheReadLookupAdapter>.Instance
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext()
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("programming");
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_mssql_connection_string_parse_failures()
    {
        var adapter = new MssqlDocumentCacheReadLookupAdapter(
            new MssqlRelationalWriteExceptionClassifier(),
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(
                providerToken: RelationalProviderToken.SqlServer,
                sqlServerPrerequisites: SatisfiedSqlServerPrerequisites(),
                connectionString: "UnknownKeyword=not-supported"
            )
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_mssql_connection_construction_failures()
    {
        var adapter = new MssqlDocumentCacheReadLookupAdapter(
            _ => throw new ArgumentException("bad target"),
            new MssqlRelationalWriteExceptionClassifier(),
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(
                providerToken: RelationalProviderToken.SqlServer,
                sqlServerPrerequisites: SatisfiedSqlServerPrerequisites()
            )
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_returns_cache_unavailable_for_mssql_connection_open_failures()
    {
        var adapter = new MssqlDocumentCacheReadLookupAdapter(
            _ => new ThrowingOpenDbConnection(new TestDbException("open failed")),
            new MssqlRelationalWriteExceptionClassifier(),
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheReadLookupAdapter>.Instance
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext(
                providerToken: RelationalProviderToken.SqlServer,
                sqlServerPrerequisites: SatisfiedSqlServerPrerequisites()
            )
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.CacheUnavailable);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
    }

    [Test]
    public async Task It_propagates_mssql_connection_acquisition_programming_exceptions()
    {
        var adapter = new MssqlDocumentCacheReadLookupAdapter(
            _ => throw new InvalidOperationException("programming"),
            new MssqlRelationalWriteExceptionClassifier(),
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            NullLogger<MssqlDocumentCacheReadLookupAdapter>.Instance
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext(
                    providerToken: RelationalProviderToken.SqlServer,
                    sqlServerPrerequisites: SatisfiedSqlServerPrerequisites()
                )
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("programming");
    }

    [Test]
    public async Task It_returns_deterministic_invariant_for_result_shape_failures()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new DocumentCacheReadLookupInvariantException("missing column")
        );

        DocumentCacheReadBatchLookupResult result = await adapter.LookupBatchAsync(
            new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
            ExecutionContext()
        );

        result.Outcome.Should().Be(DocumentCacheReadLookupOutcome.DeterministicInvariantFailure);
        result.Documents.Should().ContainSingle().Which.Outcome.Should().Be(result.Outcome);
        adapter.ExecuteAttempts.Should().Be(1);
    }

    [Test]
    public async Task It_propagates_target_binding_bugs()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new InvalidOperationException("Cache lookup should not execute.")
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext(providerToken: RelationalProviderToken.SqlServer)
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot bind target*");
        adapter.ExecuteAttempts.Should().Be(0);
    }

    [Test]
    public async Task It_propagates_caller_cancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new OperationCanceledException(cancellationSource.Token),
            classifyAsCacheUnavailable: true
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext(),
                cancellationSource.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_propagates_request_abort_disposal()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new ObjectDisposedException("request"),
            classifyAsCacheUnavailable: true
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext()
            );

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task It_propagates_unclassified_programming_exceptions()
    {
        var adapter = new ThrowingLookupAdapter(
            RelationalProviderToken.Postgresql,
            new InvalidOperationException("programming failure")
        );

        Func<Task> act = async () =>
            await adapter.LookupBatchAsync(
                new DocumentCacheReadBatchLookupRequest(MappingSet, [Candidate()]),
                ExecutionContext()
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("programming failure");
    }

    private static IEnumerable<DocumentFallbackScenario> DocumentFallbackScenarios()
    {
        yield return new(
            "Missing lifecycle",
            observation =>
                observation with
                {
                    LifecycleRowCount = 0,
                    LifecycleState = null,
                    CacheAheadRecoveryRequired = null,
                },
            DocumentCacheReadLookupOutcome.MissingLifecycleState
        );
        yield return new(
            "Invalid lifecycle",
            observation => observation with { LifecycleRowCount = 1, LifecycleState = "Invalid" },
            DocumentCacheReadLookupOutcome.InvalidLifecycleState
        );
        yield return new(
            "Disabled lifecycle",
            observation => observation with { LifecycleState = "Disabled" },
            DocumentCacheReadLookupOutcome.LifecycleDisabled
        );
        yield return new(
            "Resetting lifecycle",
            observation => observation with { LifecycleState = "Resetting" },
            DocumentCacheReadLookupOutcome.LifecycleResetting
        );
        yield return new(
            "Rebuilding lifecycle",
            observation => observation with { LifecycleState = "Rebuilding" },
            DocumentCacheReadLookupOutcome.LifecycleRebuilding
        );
        yield return new(
            "Cache-ahead latch",
            observation => observation with { CacheAheadRecoveryRequired = true },
            DocumentCacheReadLookupOutcome.CacheAheadRecoveryRequired
        );
        yield return new(
            "Missing source row",
            observation => observation with { SourceDocumentId = null },
            DocumentCacheReadLookupOutcome.MissingSourceRow
        );
        yield return new(
            "Source row identity mismatch",
            observation => observation with { SourceDocumentId = 999 },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Source drift",
            observation => observation with { SourceContentVersion = observation.SourceContentVersion + 1 },
            DocumentCacheReadLookupOutcome.SourceDrift
        );
        yield return new(
            "Missing cache row",
            observation => observation with { CacheDocumentId = null },
            DocumentCacheReadLookupOutcome.MissingCacheRow
        );
        yield return new(
            "Cache row identity mismatch",
            observation => observation with { CacheDocumentId = 999 },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Cache row resource mismatch",
            observation => observation with { CacheResourceName = "School" },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Stale cache row",
            observation => observation with { CacheContentVersion = observation.CacheContentVersion - 1 },
            DocumentCacheReadLookupOutcome.StaleCacheRow
        );
        yield return new(
            "Cache row ahead of source",
            observation => observation with { CacheContentVersion = observation.CacheContentVersion + 1 },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Missing stream etag",
            observation => observation with { StreamEtag = "" },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Missing cached JSON",
            observation => observation with { DocumentJson = "" },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
        yield return new(
            "Last modified mismatch",
            observation =>
                observation with
                {
                    CacheLastModifiedAt = observation.CacheLastModifiedAt!.Value.AddTicks(1),
                },
            DocumentCacheReadLookupOutcome.DeterministicInvariantFailure
        );
    }

    private static DocumentCacheReadAccelerationQueryRequest QueryRequest(
        DocumentCacheReadAccelerationCandidatePage candidatePage
    ) =>
        new(
            "TenantA",
            MappingSet,
            Resource,
            DocumentCacheReadAccelerationResourceKind.Resource,
            DocumentCacheReadAccelerationLookupReadiness.AuthorizedCandidate,
            (_, _) => Task.FromResult<QueryResult>(new QueryResult.QueryFailureKnownError("fallback")),
            candidatePage
        );

    private static DocumentCacheReadBatchLookupResult Classify(
        DocumentCacheReadAccelerationCandidate candidate,
        DocumentCacheReadLookupObservation observation
    ) => Classify([candidate], [observation]);

    private static DocumentCacheReadBatchLookupResult Classify(
        IReadOnlyList<DocumentCacheReadAccelerationCandidate> candidates,
        IReadOnlyList<DocumentCacheReadLookupObservation> observations
    ) =>
        DocumentCacheReadLookupClassifier.Classify(
            new DocumentCacheReadBatchLookupRequest(MappingSet, candidates),
            observations
        );

    private static DocumentCacheReadAccelerationCandidate Candidate(
        long documentId = 345,
        long contentVersion = 91,
        Guid? documentUuid = null
    ) =>
        new(
            documentId,
            new DocumentUuid(documentUuid ?? DocumentGuid),
            ResourceKeyId,
            contentVersion,
            LastModifiedAt.AddSeconds(documentId)
        );

    private static DocumentCacheReadLookupObservation Observation(
        DocumentCacheReadAccelerationCandidate candidate,
        int ordinal = 0
    )
    {
        ResourceKeyEntry resourceKey = MappingSet.ResourceKeyById[candidate.ResourceKeyId];

        return new DocumentCacheReadLookupObservation(
            Ordinal: ordinal,
            RequestedDocumentId: candidate.DocumentId,
            ExpectedDocumentUuid: candidate.DocumentUuid.Value,
            ExpectedResourceKeyId: candidate.ResourceKeyId,
            ExpectedContentVersion: candidate.ContentVersion,
            LifecycleRowCount: 1,
            LifecycleState: "Tracking",
            CacheAheadRecoveryRequired: false,
            SourceDocumentId: candidate.DocumentId,
            SourceDocumentUuid: candidate.DocumentUuid.Value,
            SourceResourceKeyId: candidate.ResourceKeyId,
            SourceContentVersion: candidate.ContentVersion,
            SourceContentLastModifiedAt: candidate.ContentLastModifiedAt,
            CacheDocumentId: candidate.DocumentId,
            CacheDocumentUuid: candidate.DocumentUuid.Value,
            CacheProjectName: resourceKey.Resource.ProjectName,
            CacheResourceName: resourceKey.Resource.ResourceName,
            CacheResourceVersion: resourceKey.ResourceVersion,
            CacheContentVersion: candidate.ContentVersion,
            StreamEtag: $"stream-{candidate.ContentVersion}",
            CacheLastModifiedAt: candidate.ContentLastModifiedAt,
            DocumentJson: """{"id":"cached"}"""
        );
    }

    private static DocumentCacheTargetExecutionContext ExecutionContext(
        RelationalProviderToken? providerToken = null,
        DocumentCacheInventoryStatus inventoryStatus = DocumentCacheInventoryStatus.Satisfied,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites = null,
        string connectionString = "connection"
    )
    {
        RelationalProviderToken resolvedProviderToken = providerToken ?? RelationalProviderToken.Postgresql;

        return new DocumentCacheTargetExecutionContext(
            TargetKey,
            new DocumentCacheTargetContextGeneration(1),
            EffectiveSettings(),
            new DocumentCacheTargetDataStoreMetadata(TargetKey.DataStoreId, resolvedProviderToken.Value),
            new DocumentCacheTargetConnectionInput(resolvedProviderToken, connectionString),
            Fingerprint,
            new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, false),
            new DocumentCacheInventoryValidationResult(inventoryStatus, "Inventory."),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Trigger."
            ),
            sqlServerPrerequisites ?? DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
    }

    private static DocumentCacheTargetEffectiveSettings EffectiveSettings() =>
        new(
            readAccelerationEnabled: true,
            directFillTimeout: TimeSpan.FromMilliseconds(250),
            projectorPollInterval: TimeSpan.FromSeconds(5),
            projectorPageSize: 3,
            projectorMaxConcurrentTargets: 2,
            projectorFailureBackoff: TimeSpan.FromSeconds(10),
            projectorBaselineHighWaterMark: 1000,
            administrationWorkflowTimeout: TimeSpan.FromHours(24)
        );

    private static DocumentCacheSqlServerPrerequisiteDetails FailedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                "RCSI disabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Nested triggers enabled."
            )
        );

    private static DocumentCacheSqlServerPrerequisiteDetails SatisfiedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "RCSI enabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Nested triggers enabled."
            )
        );

    private sealed record DocumentFallbackScenario(
        string Name,
        Func<DocumentCacheReadLookupObservation, DocumentCacheReadLookupObservation> Mutate,
        DocumentCacheReadLookupOutcome ExpectedOutcome
    )
    {
        public override string ToString() => Name;
    }

    private sealed class ObservationLookupAdapter(
        RelationalProviderToken providerToken,
        IReadOnlyList<DocumentCacheReadLookupObservation> observations,
        IDocumentCacheReadResponseShaper? responseShaper = null
    ) : DocumentCacheReadLookupAdapterBase(responseShaper)
    {
        private readonly RelationalProviderToken _providerToken = providerToken;
        private readonly IReadOnlyList<DocumentCacheReadLookupObservation> _observations = observations;

        public int ExecuteAttempts { get; private set; }

        protected override SqlDialect Dialect =>
            _providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;

        protected override RelationalProviderToken ProviderToken => _providerToken;

        protected override Task<TResult> ExecuteReaderAsync<TResult>(
            DocumentCacheTargetExecutionContext targetContext,
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ExecuteAttempts++;

            if (_observations is TResult typedObservations)
            {
                return Task.FromResult(typedObservations);
            }

            throw new InvalidOperationException(
                $"Unexpected DocumentCache read lookup test result type '{typeof(TResult).Name}'."
            );
        }

        protected override bool IsCacheUnavailable(Exception exception) => false;
    }

    private sealed class RecordingResponseShaper : IDocumentCacheReadResponseShaper
    {
        public int QueryShapeAttempts { get; private set; }

        public DocumentCacheReadBatchLookupResult? LastHitPage { get; private set; }

        public DocumentCacheReadLookupResult<QueryResult> QueryResult { get; init; } =
            DocumentCacheReadLookupResult<QueryResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheHitResponseShapingUnavailable
            );

        public DocumentCacheReadLookupResult<GetResult> ShapeGetById(
            DocumentCacheReadAccelerationGetByIdRequest request,
            DocumentCacheReadDocumentLookupResult.FreshHit hit
        ) =>
            DocumentCacheReadLookupResult<GetResult>.Fallback(
                DocumentCacheReadAccelerationFallbackReason.CacheHitResponseShapingUnavailable
            );

        public DocumentCacheReadLookupResult<QueryResult> ShapeQuery(
            DocumentCacheReadAccelerationQueryRequest request,
            DocumentCacheReadBatchLookupResult hitPage
        )
        {
            QueryShapeAttempts++;
            LastHitPage = hitPage;
            return QueryResult;
        }
    }

    private sealed class ThrowingLookupAdapter(
        RelationalProviderToken providerToken,
        Exception exception,
        bool classifyAsCacheUnavailable = false
    ) : DocumentCacheReadLookupAdapterBase
    {
        private readonly RelationalProviderToken _providerToken = providerToken;
        private readonly Exception _exception = exception;
        private readonly bool _classifyAsCacheUnavailable = classifyAsCacheUnavailable;

        public int ExecuteAttempts { get; private set; }

        protected override SqlDialect Dialect =>
            _providerToken == RelationalProviderToken.SqlServer ? SqlDialect.Mssql : SqlDialect.Pgsql;

        protected override RelationalProviderToken ProviderToken => _providerToken;

        protected override Task<TResult> ExecuteReaderAsync<TResult>(
            DocumentCacheTargetExecutionContext targetContext,
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ExecuteAttempts++;
            return Task.FromException<TResult>(_exception);
        }

        protected override bool IsCacheUnavailable(Exception exception) => _classifyAsCacheUnavailable;
    }

    private sealed class ThrowingOpenDbConnection(Exception exception) : DbConnection
    {
        private readonly Exception _exception = exception;

        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "";

        public override string DataSource => "";

        public override string ServerVersion => "";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() => throw _exception;

        public override Task OpenAsync(CancellationToken cancellationToken) => Task.FromException(_exception);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class TestDbException(string message) : DbException(message);
}
