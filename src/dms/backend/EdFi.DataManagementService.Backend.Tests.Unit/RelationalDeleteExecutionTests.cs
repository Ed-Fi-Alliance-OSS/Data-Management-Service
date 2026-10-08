// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalDeleteExecution
{
    [Test]
    public async Task It_returns_success_when_the_final_delete_result_set_contains_a_row_after_an_empty_result_set()
    {
        var executor = new RecordingDeleteCommandExecutor([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 123L }),
        ]);

        var result = await RelationalDeleteExecution.TryExecuteAsync(
            executor,
            new RelationalCommand("DELETE first; DELETE second RETURNING \"DocumentId\";"),
            new ConfigurableRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            CreateModelSet(),
            NullLogger.Instance,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("delete-execution-final-result"),
            DeleteTargetKind.Document
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [Test]
    public async Task It_returns_not_exists_when_no_result_set_contains_a_deleted_document_row()
    {
        var executor = new RecordingDeleteCommandExecutor([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(),
        ]);

        var result = await RelationalDeleteExecution.TryExecuteAsync(
            executor,
            new RelationalCommand("DELETE first; DELETE second RETURNING \"DocumentId\";"),
            new ConfigurableRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            CreateModelSet(),
            NullLogger.Instance,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("delete-execution-no-final-result"),
            DeleteTargetKind.Document
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    /// <summary>
    /// One representative behavioural check that a backend site logging a <c>TraceId</c> routes it
    /// through the correlation-ID allowlist and not the strict Method/Path one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roughly thirty backend log statements were moved off
    /// <c>LoggingSanitizer.SanitizeInternalValueForLogging</c> and onto
    /// <c>LoggingSanitizer.SanitizeCorrelationId</c>, and until this test nothing observed the
    /// result of any of them. The only cover they had was
    /// <c>CorrelationIdConstructionSiteGuardTests</c>, whose scan is textual: it verifies a
    /// spelling, not a value, and its own documented blind spot is a site that hands the strict
    /// sanitizer a variable whose name says nothing about correlation - which is exactly the shape
    /// a revert would take if the local were renamed in the same edit. This test observes the
    /// logged characters instead, so it fails on any revert whatever the variable is called.
    /// </para>
    /// <para>
    /// <b>Why this site.</b> <see cref="RelationalDeleteExecution.MapFailure"/> is a pure static:
    /// it reaches its log statement from an exception, a classifier and a model set, with no
    /// database, no session and no transaction, so exercising it costs nothing and needs no new
    /// harness. The other rerouted files - <c>DescriptorReadHandler</c>,
    /// <c>DescriptorWriteHandler</c>, <c>RelationalDocumentStoreRepository</c> - reach their
    /// equivalent statements only through a live command executor. One site is the point: this is
    /// a behavioural anchor for a rule the source scan enforces everywhere else.
    /// </para>
    /// <para>
    /// <b>Why this literal.</b> <c>a+b=c{d}e@f|g</c> is made of exactly the characters the two
    /// allowlists disagree about. The strict allowlist admits only letters, digits, spaces and
    /// <c>_-.:/\</c>, so it would strip every one of <c>+ = { } @ |</c> and leave <c>abcdefg</c>;
    /// the correlation-ID allowlist removes only Unicode categories Cc and Cf and so preserves all
    /// of them. Asserting the exact expected message is what makes the difference detectable -
    /// a substring or a "not empty" check would pass under either sanitizer.
    /// </para>
    /// </remarks>
    [Test]
    public void It_logs_an_upstream_correlation_id_without_stripping_the_punctuation_it_uses()
    {
        // Every character here beyond the letters is one the strict Method/Path allowlist removes
        // and the correlation-ID allowlist keeps. The same literal anchors the frontend and Core
        // parity fixtures.
        const string UpstreamCorrelationId = "a+b=c{d}e@f|g";

        CapturingLogger<Given_RelationalDeleteExecution> logger = new();
        DocumentUuid documentUuid = new(Guid.NewGuid());

        DeleteResult result = RelationalDeleteExecution.MapFailure(
            new StubDbException("Simulated transient delete failure."),
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true },
            A.Fake<IRelationalDeleteConstraintResolver>(),
            CreateModelSet(),
            logger,
            documentUuid,
            new TraceId(UpstreamCorrelationId),
            DeleteTargetKind.Document
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureWriteConflict>(
                "the transient branch is the one that carries the log statement under test, so a "
                    + "different result means the test never reached it and proves nothing"
            );

        // StartWith rather than Be because CapturingLogger appends the exception's ToString to
        // every entry. The portion asserted is the whole formatted message, character for
        // character - under the strict sanitizer it would end in "abcdefg" instead.
        logger
            .Messages.Should()
            .ContainSingle()
            .Which.Should()
            .StartWith(
                $"Transient conflict on document DELETE for {documentUuid.Value} - {UpstreamCorrelationId}",
                "a correlation ID has to reach the log exactly as the client will read it back in "
                    + "the error response body for the same request (FR-LOG-6). Routing it through "
                    + "the strict Method/Path sanitizer strips the punctuation an upstream "
                    + "identifier scheme uses, and the operator searching the logs for the ID the "
                    + "client was given then finds nothing"
            );
    }

    /// <summary>
    /// A provider failure with nothing provider-specific about it. The classifier under test is
    /// configured directly, so no real SQLSTATE is needed to steer the branch.
    /// </summary>
    private sealed class StubDbException(string message) : DbException(message);

    private static DerivedRelationalModelSet CreateModelSet()
    {
        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "School"),
            "1.0.0",
            false
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0",
                "v1",
                "schema-hash",
                1,
                [1, 2, 3],
                [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                [resourceKey]
            ),
            SqlDialect.Pgsql,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
            [],
            [],
            [],
            [],
            []
        );
    }

    private sealed class RecordingDeleteCommandExecutor(IReadOnlyList<InMemoryRelationalResultSet> resultSets)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }
}
