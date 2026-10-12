// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class DocumentCacheOwnerCoherenceScenario
{
    public static async Task ExecuteAsync(
        DbConnection connection,
        IRelationalCommandExecutor executor,
        SqlDialect dialect,
        bool descriptor,
        bool initiallyMissing,
        string finalState
    )
    {
        string Q(string name) => dialect == SqlDialect.Pgsql ? $"\"{name}\"" : $"[{name}]";
        string document = $"{Q("dms")}.{Q("Document")}";
        string owner = descriptor ? $"{Q("dms")}.{Q("Descriptor")}" : $"{Q("edfi")}.{Q("School")}";
        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        if (descriptor)
        {
            await Execute(
                $"""
                CREATE TABLE {owner} (
                    {Q("DescriptorId")} int PRIMARY KEY,
                    {Q("DocumentId")} bigint NOT NULL UNIQUE,
                    {Q("ResourceKeyId")} smallint NOT NULL,
                    {Q("ContentLastModifiedAt")} {(
                    dialect == SqlDialect.Pgsql ? "timestamptz" : "datetimeoffset"
                )} NOT NULL,
                    {Q("Namespace")} varchar(255) NOT NULL,
                    {Q("CodeValue")} varchar(50) NOT NULL,
                    {Q("ShortDescription")} varchar(75) NOT NULL,
                    {Q("Description")} varchar(1024) NULL,
                    {Q("EffectiveBeginDate")} date NULL,
                    {Q("EffectiveEndDate")} date NULL
                );
                INSERT INTO {owner} VALUES
                    (17, 980101, 13, '2026-07-30T14:15:16+00:00', 'uri://ed-fi.org/SchoolTypeDescriptor', 'Target', 'Target', NULL, NULL, NULL),
                    (18, 980102, 13, '2020-01-01T00:00:00+00:00', 'uri://ed-fi.org/SchoolTypeDescriptor', 'Neighbor', 'Neighbor', NULL, NULL, NULL);
                UPDATE {document} SET {Q("ResourceKeyId")} = 13 WHERE {Q("DocumentId")} = 980101;
                """
            );
        }

        string deleteOwner = $"DELETE FROM {owner} WHERE {Q("DocumentId")} = 980101;";
        if (initiallyMissing)
        {
            await Execute(deleteOwner);
        }

        var mappingSet = descriptor
            ? DocumentCacheMaterializerDescriptorMappingSet.Create(dialect)
            : DocumentCacheMaterializerCoherenceMappingSet.Create(dialect);
        var store = new AmbientDocumentCacheMaterializationDataStore(executor, new CapturedHydrator());
        var gate = new RereadGate(new DocumentCacheSourceMetadataReader(store));
        var materializer = new DocumentCacheMaterializer(
            gate,
            new DocumentCacheDescriptorHydrator(store),
            store,
            new UnreachableMaterializer(),
            new ServedEtagComposer()
        );
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var request = new DocumentCacheMaterializationRequest(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            980101,
            222,
            DocumentCacheMaterializationPurpose.Fixture,
            watchdog.Token
        );
        Task<DocumentCacheMaterializationResult> pending = materializer.MaterializeAsync(request);
        try
        {
            Task arrived = await Task.WhenAny(gate.Arrived.Task, pending).WaitAsync(watchdog.Token);
            if (arrived == pending)
            {
                await pending;
            }
            arrived.Should().BeSameAs(gate.Arrived.Task, "materialization must reach its coherence reread");
            if (!initiallyMissing)
            {
                await Execute(deleteOwner);
            }
            if (finalState == "Deleted")
            {
                await Execute($"DELETE FROM {document} WHERE {Q("DocumentId")} = 980101;");
            }
            else if (finalState == "Changed")
            {
                await Execute(
                    $"UPDATE {document} SET {Q("ContentVersion")} = 223 WHERE {Q("DocumentId")} = 980101;"
                );
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        if (finalState == "Stable")
        {
            Func<Task> act = async () => await pending;
            var failure = (
                await act.Should().ThrowAsync<DocumentCacheProjectionProcessingException>()
            ).Subject.Single();
            failure
                .Reason.Should()
                .Be(DocumentCacheProjectionProcessingFailureReason.StableSourceBodyMissing);
            failure.FailureMetadata.DocumentId.Should().Be(980101);
            failure.FailureMetadata.ResourceKeyId.Should().Be(descriptor ? (short)13 : (short)11);
        }
        else
        {
            (await pending)
                .Should()
                .BeSameAs(
                    finalState == "Deleted"
                        ? DocumentCacheMaterializationResult.MissingSource.Instance
                        : DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance
                );
        }

        if (descriptor)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {Q("CodeValue")} FROM {owner} WHERE {Q("DescriptorId")} = 18 AND {Q("ContentLastModifiedAt")} = '2020-01-01T00:00:00+00:00';";
            (await command.ExecuteScalarAsync()).Should().Be("Neighbor");
        }
    }

    private sealed class RereadGate(IDocumentCacheSourceMetadataReader inner)
        : IDocumentCacheSourceMetadataReader
    {
        public TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(request, cancellationToken);

        public async Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Arrived.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadCurrentAsync(request, cancellationToken);
        }
    }

    private sealed class CapturedHydrator : IDocumentHydrator
    {
        public Task<HydratedPage> HydrateAsync(
            ResourceReadPlan plan,
            PageKeysetSpec keyset,
            HydrationExecutionOptions executionOptions,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HydratedPage(
                    null,
                    [],
                    [new HydratedTableRows(plan.Model.Root, [new object?[] { 980101L }])],
                    []
                )
            );
    }

    private sealed class UnreachableMaterializer : IRelationalReadMaterializer
    {
        public JsonNode Materialize(RelationalReadMaterializationRequest request) =>
            throw new InvalidOperationException(
                "An incoherent source must never reach candidate construction."
            );

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        ) => throw new NotSupportedException();

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) =>
            throw new NotSupportedException();
    }
}
