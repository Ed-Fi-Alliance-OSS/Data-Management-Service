// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterTelemetry
{
    private static readonly DocumentCacheProjectionTargetKey TargetKey = new("tenant-a", new DataStoreId(7));
    private const string TargetLabel = "t1_5da94bdd25fe3bd6fe2e4b0e";

    [Test]
    public void It_records_one_counter_measurement_for_each_bounded_writer_outcome()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();

        foreach (DocumentCacheWriterOutcome outcome in Enum.GetValues<DocumentCacheWriterOutcome>())
        {
            telemetry.RecordOutcome(
                DocumentCacheWriterMetricContext.ForCacheWriter(
                    RelationalProviderToken.Postgresql,
                    TargetKey,
                    DocumentCacheWriterPurpose.DurableWorkProjection,
                    DocumentCacheLifecycleState.Tracking,
                    outcome
                )
            );
        }

        MetricMeasurement[] records = collector.MeasurementsFor(
            DocumentCacheWriterTelemetry.OutcomeCounterName
        );

        records.Should().HaveCount(Enum.GetValues<DocumentCacheWriterOutcome>().Length);
        records.Select(record => record.LongValue).Should().OnlyContain(value => value == 1);
        records
            .Select(record => record.Tags["outcome"])
            .Should()
            .BeEquivalentTo(Enum.GetNames<DocumentCacheWriterOutcome>());
        records.Should().OnlyContain(record => (string)record.Tags["provider"]! == "postgresql");
        records.Should().OnlyContain(record => (string)record.Tags["target"]! == TargetLabel);
        records
            .Should()
            .OnlyContain(record =>
                (string)record.Tags["purpose"]! == nameof(DocumentCacheWriterPurpose.DurableWorkProjection)
            );
        records.Should().OnlyContain(record => (string)record.Tags["lifecycle"]! == "Tracking");
    }

    [Test]
    public void It_records_transaction_phase_retry_and_same_document_histograms()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheWriterMetricContext context = DocumentCacheWriterMetricContext.ForCacheWriter(
            RelationalProviderToken.Postgresql,
            TargetKey,
            DocumentCacheWriterPurpose.DirectFill,
            DocumentCacheLifecycleState.Rebuilding,
            DocumentCacheWriterOutcome.CandidateWrittenAcknowledged
        );

        telemetry.RecordTransactionDuration(context, TimeSpan.FromMilliseconds(10));
        telemetry.RecordCacheDmlDuration(context, TimeSpan.FromMilliseconds(11));
        telemetry.RecordAcknowledgementDuration(context, TimeSpan.FromMilliseconds(12));
        telemetry.RecordRetry(context, TimeSpan.FromMilliseconds(13), attemptCount: 3);
        telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.Acknowledgement,
            TimeSpan.FromMilliseconds(14)
        );

        collector.MeasurementsFor(DocumentCacheWriterTelemetry.TransactionDurationName).Should().HaveCount(1);
        collector.MeasurementsFor(DocumentCacheWriterTelemetry.CacheDmlDurationName).Should().HaveCount(1);
        collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.AcknowledgementDurationName)
            .Should()
            .HaveCount(1);
        collector.MeasurementsFor(DocumentCacheWriterTelemetry.RetryDurationName).Should().HaveCount(1);
        collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.RetryAttemptsName)
            .Should()
            .ContainSingle()
            .Which.IntValue.Should()
            .Be(3);

        MetricMeasurement sameDocumentWait = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName)
            .Should()
            .ContainSingle()
            .Which;
        sameDocumentWait.DoubleValue.Should().Be(14);
        sameDocumentWait.Tags["participant"].Should().Be("CacheWriter");
        sameDocumentWait.Tags["phase"].Should().Be("Acknowledgement");
        sameDocumentWait.Tags["purpose"].Should().Be(nameof(DocumentCacheWriterPurpose.DirectFill));
    }

    [Test]
    public void It_records_canonical_writer_waits_in_the_same_sanitized_metric_family()
    {
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordSameDocumentWait(
            DocumentCacheWriterMetricContext.ForCanonicalWriter(
                RelationalProviderToken.SqlServer,
                DocumentCacheWriterTelemetryLabel.CanonicalWrite,
                DocumentCacheWriterTelemetryLabel.AppliedWrite
            ),
            DocumentCacheWriterContentionParticipant.CanonicalWriter,
            DocumentCacheWriterContentionPhase.CanonicalPersist,
            TimeSpan.FromMilliseconds(15)
        );

        MetricMeasurement sameDocumentWait = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName)
            .Should()
            .ContainSingle()
            .Which;
        sameDocumentWait.Tags["provider"].Should().Be("sqlserver");
        sameDocumentWait.Tags["target"].Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        sameDocumentWait.Tags["purpose"].Should().Be(DocumentCacheWriterTelemetryLabel.CanonicalWrite);
        sameDocumentWait.Tags["lifecycle"].Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        sameDocumentWait.Tags["outcome"].Should().Be(DocumentCacheWriterTelemetryLabel.AppliedWrite);
        sameDocumentWait.Tags["participant"].Should().Be("CanonicalWriter");
        sameDocumentWait.Tags["phase"].Should().Be("CanonicalPersist");
    }

    [Test]
    public void It_builds_canonical_writer_context_from_dialect_with_unknown_target()
    {
        DocumentCacheWriterMetricContext context = DocumentCacheWriterMetricContext.ForCanonicalWriter(
            SqlDialect.Mssql,
            DocumentCacheWriterTelemetryLabel.CanonicalWrite,
            DocumentCacheWriterTelemetryLabel.AppliedWrite
        );

        var tags = context.ToTags();

        tags.First(tag => tag.Key == "provider").Value.Should().Be("sqlserver");
        tags.First(tag => tag.Key == "target").Value.Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        tags.First(tag => tag.Key == "purpose")
            .Value.Should()
            .Be(DocumentCacheWriterTelemetryLabel.CanonicalWrite);
        tags.First(tag => tag.Key == "outcome")
            .Value.Should()
            .Be(DocumentCacheWriterTelemetryLabel.AppliedWrite);
    }

    [Test]
    public void It_sanitizes_and_bounds_labels_without_document_identifiers_payloads_or_resource_labels()
    {
        const string sensitiveDocumentUuid = "11111111-1111-1111-1111-111111111111";
        using MetricCollector collector = new();
        DocumentCacheWriterTelemetry telemetry = collector.CreateTelemetry();
        DocumentCacheProjectionTargetKey noisyTargetKey = new(
            "tenant-a\n{unsafe-template}" + new string('x', 160),
            new DataStoreId(7)
        );

        telemetry.RecordOutcome(
            DocumentCacheWriterMetricContext.ForCacheWriter(
                RelationalProviderToken.Postgresql,
                noisyTargetKey,
                DocumentCacheWriterPurpose.DurableWorkProjection,
                lifecycleState: null,
                DocumentCacheWriterOutcome.RetryBudgetExhausted
            )
        );

        MetricMeasurement record = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.OutcomeCounterName)
            .Should()
            .ContainSingle()
            .Which;

        record
            .Tags.Values.OfType<string>()
            .Should()
            .OnlyContain(label => label.Length <= 128 && !label.Contains('\n'));
        string joinedLabels = string.Join("|", record.Tags.Values.OfType<string>());
        joinedLabels.Should().NotContain("{");
        joinedLabels.Should().NotContain("}");
        joinedLabels.Should().NotContain(sensitiveDocumentUuid);
        joinedLabels.Should().NotContain("DocumentId");
        joinedLabels.Should().NotContain("DocumentUuid");
        joinedLabels.Should().NotContain("DocumentJson");
        joinedLabels.Should().NotContain("authorization-token");
        joinedLabels.Should().NotContain("ResourceName");
    }

    [TestCase(DescriptorTelemetryWritePath.PostInsert)]
    [TestCase(DescriptorTelemetryWritePath.PostAsUpdate)]
    [TestCase(DescriptorTelemetryWritePath.PutUpdate)]
    public async Task It_records_descriptor_applied_writes_in_the_same_canonical_writer_wait_family(
        DescriptorTelemetryWritePath writePath
    )
    {
        using MetricCollector collector = new();
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateDescriptorWriteHandler(
            targetLookupService,
            sessionFactory,
            collector.CreateTelemetry()
        );
        var mappingSet = CreateDescriptorMappingSet(SqlDialect.Pgsql);

        switch (writePath)
        {
            case DescriptorTelemetryWritePath.PostInsert:
                targetLookupService.PostResult = new RelationalWriteTargetLookupResult.CreateNew(
                    documentUuid
                );
                sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(42L)]);

                await sut.HandlePostAsync(CreatePostDescriptorWriteRequest(mappingSet, documentUuid))
                    .ConfigureAwait(false);
                break;

            case DescriptorTelemetryWritePath.PostAsUpdate:
                targetLookupService.PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    44L
                );
                sessionFactory.Session.ScalarResults.Enqueue(44L);
                sessionFactory.Session.Executor.ResultSets.Enqueue([
                    CreatePersistedDescriptorResultSet(description: "Previous"),
                ]);
                sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

                await sut.HandlePostAsync(CreatePostDescriptorWriteRequest(mappingSet, documentUuid))
                    .ConfigureAwait(false);
                break;

            case DescriptorTelemetryWritePath.PutUpdate:
                targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                    345L,
                    documentUuid,
                    44L
                );
                sessionFactory.Session.ScalarResults.Enqueue(44L);
                sessionFactory.Session.Executor.ResultSets.Enqueue([
                    CreatePersistedDescriptorResultSet(description: "Previous"),
                ]);
                sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

                await sut.HandlePutAsync(
                        CreatePutDescriptorWriteRequest(
                            mappingSet,
                            documentUuid,
                            description: "Updated Description"
                        )
                    )
                    .ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(writePath), writePath, null);
        }

        MetricMeasurement sameDocumentWait = collector
            .MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName)
            .Should()
            .ContainSingle()
            .Which;
        sameDocumentWait.Tags["provider"].Should().Be("postgresql");
        sameDocumentWait.Tags["target"].Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        sameDocumentWait.Tags["purpose"].Should().Be(DocumentCacheWriterTelemetryLabel.CanonicalWrite);
        sameDocumentWait.Tags["lifecycle"].Should().Be(DocumentCacheWriterTelemetryLabel.Unknown);
        sameDocumentWait.Tags["outcome"].Should().Be(DocumentCacheWriterTelemetryLabel.AppliedWrite);
        sameDocumentWait.Tags["participant"].Should().Be("CanonicalWriter");
        sameDocumentWait.Tags["phase"].Should().Be("CanonicalPersist");
        sameDocumentWait.DoubleValue.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task It_does_not_record_descriptor_canonical_writer_wait_for_no_op_put_rollbacks()
    {
        using MetricCollector collector = new();
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorResultSet()]);
        var sut = CreateDescriptorWriteHandler(
            targetLookupService,
            sessionFactory,
            collector.CreateTelemetry()
        );

        await sut.HandlePutAsync(
                CreatePutDescriptorWriteRequest(CreateDescriptorMappingSet(SqlDialect.Pgsql), documentUuid)
            )
            .ConfigureAwait(false);

        collector.MeasurementsFor(DocumentCacheWriterTelemetry.SameDocumentWaitName).Should().BeEmpty();
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    public enum DescriptorTelemetryWritePath
    {
        PostInsert,
        PostAsUpdate,
        PutUpdate,
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"DocumentCacheWriterTelemetryTests.{Guid.NewGuid()}");
        private readonly MeterListener _listener = new();
        private readonly List<MetricMeasurement> _measurements = [];

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: measurement,
                            IntValue: null,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<int>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: null,
                            IntValue: measurement,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            LongValue: null,
                            IntValue: null,
                            Tags: CopyTags(tags),
                            DoubleValue: measurement
                        )
                    )
            );
            _listener.Start();
        }

        public DocumentCacheWriterTelemetry CreateTelemetry() => new(_meter);

        public MetricMeasurement[] MeasurementsFor(string instrumentName) =>
            [.. _measurements.Where(measurement => measurement.InstrumentName == instrumentName)];

        public void Dispose()
        {
            _listener.Dispose();
            _meter.Dispose();
        }

        private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> result = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
        }
    }

    private static DescriptorWriteHandler CreateDescriptorWriteHandler(
        IRelationalWriteTargetLookupService targetLookupService,
        IRelationalWriteSessionFactory writeSessionFactory,
        IDocumentCacheWriterTelemetry telemetry
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            writeSessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance,
            new ServedEtagComposer(),
            documentCacheWriterTelemetry: telemetry
        );
    }

    private static DescriptorWriteRequest CreatePostDescriptorWriteRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            DescriptorResource,
            CreateDescriptorRequestBody(description),
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-telemetry")
        );
    }

    private static DescriptorWriteRequest CreatePutDescriptorWriteRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            DescriptorResource,
            CreateDescriptorRequestBody(description),
            documentUuid,
            null,
            new TraceId("descriptor-put-telemetry")
        );
    }

    private static JsonNode CreateDescriptorRequestBody(string description)
    {
        return JsonNode.Parse(
            $$"""
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter",
              "description": "{{description}}",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;
    }

    private static InMemoryRelationalResultSet CreateContentVersionResultSet(long contentVersion) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?> { ["ContentVersion"] = contentVersion }
        );

    private static InMemoryRelationalResultSet CreatePersistedDescriptorResultSet(
        string description = "Charter"
    ) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = description,
                ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                ["EffectiveEndDate"] = null,
            }
        );

    private static MappingSet CreateDescriptorMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, DescriptorResource, "1.0.0", true);
        var rootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolTypeDescriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_SchoolTypeDescriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    private sealed class RecordingRelationalCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingRelationalWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public RecordingRelationalWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingRelationalWriteSession()
            : this(SqlDialect.Pgsql) { }

        public RecordingRelationalWriteSession(SqlDialect dialect)
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = new RecordingRelationalCommandExecutor(dialect);
        }

        public DbConnection Connection => _connection;

        public DbTransaction Transaction => _transaction;

        public RecordingRelationalCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            var dbCommand = new RecordingDbCommand(new DataTable().CreateDataReader())
            {
                CommandText = command.CommandText,
                ScalarResult = ScalarResults.Count == 0 ? null : ScalarResults.Dequeue(),
            };

            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                parameter.ConfigureParameter?.Invoke(dbParameter);
                dbCommand.Parameters.Add((RecordingDbParameter)dbParameter);
            }

            return dbCommand;
        }

        public IRelationalCommandExecutor CreateCommandExecutor() => Executor;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCallCount++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubRelationalWriteTargetLookupService : IRelationalWriteTargetLookupService
    {
        public RelationalWriteTargetLookupResult PostResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public RelationalWriteTargetLookupResult PutResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PostResult);
        }

        public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            DocumentUuid documentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PutResult);
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        long? LongValue,
        int? IntValue,
        Dictionary<string, object?> Tags,
        double? DoubleValue = null
    );
}
