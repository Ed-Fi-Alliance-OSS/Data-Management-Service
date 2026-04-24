// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Handler_Delete
{
    private static readonly QualifiedResourceName _descriptorResource = new(
        "Ed-Fi",
        "EducationOrganizationCategoryDescriptor"
    );

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_delete_failure_reference_with_the_resolved_resource_name_when_the_resolver_finds_the_owning_resource(
        SqlDialect dialect
    )
    {
        const string constraintName = "FK_School_EdOrgCategoryDescriptor";
        var referencingResource = new QualifiedResourceName("Ed-Fi", "School");
        var fixture = new Fixture(dialect);
        fixture.Classifier.IsForeignKeyViolationToReturn = true;
        fixture.Classifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(constraintName);
        A.CallTo(() =>
                fixture.Resolver.TryResolveReferencingResource(fixture.MappingSet.Model, constraintName)
            )
            .Returns(referencingResource);

        var result = await fixture.Sut.HandleDeleteAsync(
            fixture.MappingSet,
            _descriptorResource,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("descriptor-delete-trace")
        );

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
        // Match on the exact MappingSet.Model reference — a narrowing of the any-matcher that
        // catches a regression where the handler stops forwarding mappingSet.Model to the
        // resolver (e.g., accidentally wires null or a stale model set from elsewhere). The
        // fake would otherwise accept any DerivedRelationalModelSet and hide the wire-through
        // bug all the way to the end-to-end suite.
        A.CallTo(() =>
                fixture.Resolver.TryResolveReferencingResource(fixture.MappingSet.Model, constraintName)
            )
            .MustHaveHappenedOnceExactly();
        // Match on the log payload so the assertion fails if the FK-resolution Debug log is
        // removed or demoted — an unrelated "Deleting descriptor document..." Debug log is
        // always emitted before the FK path runs, so a bare `r.Level == Debug` check would pass
        // even without the new line.
        fixture
            .Logger.Records.Should()
            .ContainSingle(r =>
                r.Level == LogLevel.Debug
                && r.Message.Contains(constraintName, StringComparison.Ordinal)
                && r.Message.Contains(referencingResource.ResourceName, StringComparison.Ordinal)
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_an_empty_reference_failure_and_logs_information_when_the_classifier_cannot_extract_a_constraint_name(
        SqlDialect dialect
    )
    {
        var fixture = new Fixture(dialect);
        fixture.Classifier.IsForeignKeyViolationToReturn = true;
        fixture.Classifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        var result = await fixture.Sut.HandleDeleteAsync(
            fixture.MappingSet,
            _descriptorResource,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("descriptor-delete-trace")
        );

        result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
        A.CallTo(() =>
                fixture.Resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, A<string>._)
            )
            .MustNotHaveHappened();
        fixture.Logger.Records.Should().Contain(r => r.Level == LogLevel.Information);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_an_empty_reference_failure_and_logs_warning_when_the_resolver_cannot_map_the_constraint_name(
        SqlDialect dialect
    )
    {
        const string constraintName = "FK_Unknown_To_Model";
        var fixture = new Fixture(dialect);
        fixture.Classifier.IsForeignKeyViolationToReturn = true;
        fixture.Classifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(constraintName);
        A.CallTo(() =>
                fixture.Resolver.TryResolveReferencingResource(fixture.MappingSet.Model, constraintName)
            )
            .Returns((QualifiedResourceName?)null);

        var result = await fixture.Sut.HandleDeleteAsync(
            fixture.MappingSet,
            _descriptorResource,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("descriptor-delete-trace")
        );

        result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
        A.CallTo(() =>
                fixture.Resolver.TryResolveReferencingResource(fixture.MappingSet.Model, constraintName)
            )
            .MustHaveHappenedOnceExactly();
        fixture.Logger.Records.Should().Contain(r => r.Level == LogLevel.Warning);
    }

    private sealed class Fixture
    {
        public Fixture(SqlDialect dialect)
        {
            Classifier = new ConfigurableRelationalWriteExceptionClassifier();
            Resolver = A.Fake<IRelationalDeleteConstraintResolver>();
            Logger = new RecordingLogger<DescriptorWriteHandler>();
            MappingSet = CreateMappingSet(dialect);
            var commandExecutor = new ThrowingRelationalCommandExecutor(
                dialect,
                new StubDbException("FK constraint violation")
            );
            var targetLookupService = A.Fake<IRelationalWriteTargetLookupService>();
            Sut = new DescriptorWriteHandler(
                targetLookupService,
                commandExecutor,
                Classifier,
                Resolver,
                Logger
            );
        }

        public ConfigurableRelationalWriteExceptionClassifier Classifier { get; }

        public IRelationalDeleteConstraintResolver Resolver { get; }

        public RecordingLogger<DescriptorWriteHandler> Logger { get; }

        public MappingSet MappingSet { get; }

        public DescriptorWriteHandler Sut { get; }

        private static MappingSet CreateMappingSet(SqlDialect dialect)
        {
            var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
            var rootTable = new DbTableModel(
                new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationCategoryDescriptor"),
                new JsonPathExpression("$", []),
                new TableKey(
                    "PK_EducationOrganizationCategoryDescriptor",
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
            );
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
                        new ConcreteResourceModel(
                            resourceKey,
                            ResourceStorageKind.SharedDescriptorTable,
                            resourceModel
                        ),
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
    }

    private sealed class ConfigurableRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public bool IsForeignKeyViolationToReturn { get; set; }

        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            classification = ClassificationToReturn;
            return classification is not null;
        }

        public bool IsForeignKeyViolation(DbException exception) => IsForeignKeyViolationToReturn;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool IsTransientFailure(DbException exception) => false;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Records.Add(new LogRecord(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

    private sealed class ThrowingRelationalCommandExecutor(SqlDialect dialect, DbException exception)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }

    private sealed class StubDbException(string message) : DbException(message);
}
