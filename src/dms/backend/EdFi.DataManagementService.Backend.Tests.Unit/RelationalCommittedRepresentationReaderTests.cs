// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCommittedRepresentationReader
{
    private const long StampedContentVersion = 91L;

    [Test]
    public async Task It_composes_the_committed_etag_from_the_stamped_content_version()
    {
        var sut = new RelationalCommittedRepresentationReader(
            new EtagComposer(),
            Options.Create(new ResourceLinksOptions())
        );
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const long documentId = 345L;
        var request = CreateRequest(mappingSet, writePlan, readPlan, documentUuid, documentId);
        var persistedTarget = new RelationalWritePersistResult(documentId, documentUuid);
        var writeSession = CreateWriteSession(StampedContentVersion);

        var result = await sut.ReadAsync(request, persistedTarget, writeSession);

        // No profile ("_"), links enabled ("l"), JSON ("j"), and the mapping set's schema epoch.
        var expectedEtag = new EtagComposer().Compose(
            StampedContentVersion,
            VariantKeyFactory.Create(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(null),
                linksEnabled: true
            )
        );
        result.Should().BeOfType<JsonObject>();
        result["_etag"]!.GetValue<string>().Should().Be(expectedEtag);
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        ResourceReadPlan readPlan,
        DocumentUuid documentUuid,
        long documentId
    ) =>
        new(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(documentUuid),
            writePlan,
            readPlan,
            JsonNode.Parse("""{"schoolId":255901}""")!,
            false,
            new TraceId("committed-readback-trace"),
            new ReferenceResolverRequest(
                MappingSet: mappingSet,
                RequestResource: writePlan.Model.Resource,
                DocumentReferences: [],
                DescriptorReferences: []
            ),
            new RelationalWriteTargetContext.ExistingDocument(documentId, documentUuid, 44L),
            writePrecondition: new WritePrecondition.None()
        );

    private static IRelationalWriteSession CreateWriteSession(long contentVersion)
    {
        var writeSession = A.Fake<IRelationalWriteSession>();
        A.CallTo(() => writeSession.Connection).Returns(A.Fake<DbConnection>());
        A.CallTo(() => writeSession.Transaction).Returns(A.Fake<DbTransaction>());
        A.CallTo(() => writeSession.CreateCommand(A<RelationalCommand>._))
            .Returns(new ScalarResultDbCommand(contentVersion));
        return writeSession;
    }

    private sealed class ScalarResultDbCommand(object? scalarResult) : DbCommand
    {
        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new StubDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => scalarResult;

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new StubDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(scalarResult);
        }
    }

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot => this;

        public override int Add(object value) => 0;

        public override void AddRange(Array values) { }

        public override void Clear() { }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) { }

        public override System.Collections.IEnumerator GetEnumerator() =>
            Array.Empty<object>().GetEnumerator();

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new IndexOutOfRangeException();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) { }

        public override void Remove(object value) { }

        public override void RemoveAt(int index) { }

        public override void RemoveAt(string parameterName) { }

        protected override void SetParameter(int index, DbParameter value) { }

        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class StubDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() { }
    }
}
