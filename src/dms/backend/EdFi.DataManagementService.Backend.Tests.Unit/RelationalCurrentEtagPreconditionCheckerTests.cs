// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCurrentEtagPreconditionChecker
{
    private IRelationalWriteCurrentStateLoader _currentStateLoader = null!;
    private IRelationalReadMaterializer _readMaterializer = null!;
    private IReadableProfileProjector _readableProfileProjector = null!;
    private IRelationalWriteSession _writeSession = null!;
    private RelationalCurrentEtagPreconditionChecker _sut = null!;
    private RelationalCommand _capturedLockCommand = null!;
    private RelationalWriteCurrentStateLoadRequest _capturedCurrentStateLoadRequest = null!;
    private RelationalReadMaterializationRequest _capturedMaterializationRequest = null!;
    private readonly DocumentUuid _documentUuid = new(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
    private const long DocumentId = 345L;
    private const long LockedContentVersion = 91L;

    [SetUp]
    public void Setup()
    {
        _currentStateLoader = A.Fake<IRelationalWriteCurrentStateLoader>();
        _readMaterializer = A.Fake<IRelationalReadMaterializer>();
        _readableProfileProjector = A.Fake<IReadableProfileProjector>();
        _writeSession = A.Fake<IRelationalWriteSession>();
        _sut = new RelationalCurrentEtagPreconditionChecker(
            _currentStateLoader,
            _readMaterializer,
            _readableProfileProjector
        );

        A.CallTo(() => _writeSession.CreateCommand(A<RelationalCommand>._))
            .Invokes(call => _capturedLockCommand = call.GetArgument<RelationalCommand>(0)!)
            .Returns(new ScalarResultDbCommand(LockedContentVersion));

        A.CallTo(() =>
                _currentStateLoader.LoadAsync(
                    A<RelationalWriteCurrentStateLoadRequest>._,
                    _writeSession,
                    A<CancellationToken>._
                )
            )
            .Invokes(call =>
                _capturedCurrentStateLoadRequest = call.GetArgument<RelationalWriteCurrentStateLoadRequest>(
                    0
                )!
            )
            .Returns(CreateCurrentState(LockedContentVersion));

        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call =>
                _capturedMaterializationRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!
            )
            .ReturnsLazily(() => CreateCurrentExternalResponse());
    }

    [Test]
    public async Task It_uses_a_postgresql_row_lock_and_matches_the_current_etag_exactly()
    {
        var request = CreateRequest(SqlDialect.Pgsql, CreateExactIfMatchPrecondition());

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsMatch.Should().BeTrue();
        result
            .CurrentEtag.Should()
            .Be(RelationalApiMetadataFormatter.FormatEtag(CreateCurrentExternalResponse()));
        result.TargetContext.ObservedContentVersion.Should().Be(LockedContentVersion);
        _capturedLockCommand.CommandText.Should().Contain("FOR UPDATE");
        _capturedLockCommand.CommandText.Should().Contain("WHERE document.\"DocumentId\" = @documentId");
        _capturedLockCommand.Parameters.Should().ContainSingle();
        _capturedLockCommand.Parameters[0].Name.Should().Be("@documentId");
        _capturedLockCommand.Parameters[0].Value.Should().Be(DocumentId);
        _capturedCurrentStateLoadRequest
            .TargetContext.ObservedContentVersion.Should()
            .Be(LockedContentVersion);
        _capturedCurrentStateLoadRequest.IncludeDescriptorProjection.Should().BeFalse();
        _capturedMaterializationRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    A<JsonNode>._,
                    A<ContentTypeDefinition>._,
                    A<IReadOnlySet<string>>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_uses_a_sql_server_row_lock_and_reports_mismatch_for_a_different_opaque_value()
    {
        var request = CreateRequest(SqlDialect.Mssql, new WritePrecondition.IfMatch("not-the-current-etag"));

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsMatch.Should().BeFalse();
        _capturedLockCommand.CommandText.Should().Contain("WITH (UPDLOCK, HOLDLOCK, ROWLOCK)");
        _capturedLockCommand.CommandText.Should().Contain("WHERE document.[DocumentId] = @documentId");
        _capturedLockCommand.Parameters.Should().ContainSingle();
        _capturedLockCommand.Parameters[0].Name.Should().Be("@documentId");
        _capturedLockCommand.Parameters[0].Value.Should().Be(DocumentId);
        _capturedCurrentStateLoadRequest
            .TargetContext.ObservedContentVersion.Should()
            .Be(LockedContentVersion);
        _capturedCurrentStateLoadRequest.IncludeDescriptorProjection.Should().BeFalse();
    }

    [Test]
    public async Task It_compares_using_the_profile_projected_surface_when_a_readable_projection_context_is_present()
    {
        var projectionContext = CreateReadableEtagProjectionContext();
        var materializedCurrentResponse = CreateCurrentExternalResponse();
        var projectedCurrentResponse = JsonNode.Parse(
            """
            {
              "schoolId": 255901,
              "_etag": "stale"
            }
            """
        )!;
        var expectedProjectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedCurrentResponse);
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch(expectedProjectedEtag, projectionContext)
        );

        A.CallTo(() => _readMaterializer.Materialize(A<RelationalReadMaterializationRequest>._))
            .Invokes(call =>
                _capturedMaterializationRequest = call.GetArgument<RelationalReadMaterializationRequest>(0)!
            )
            .Returns(materializedCurrentResponse);
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedCurrentResponse,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .Returns(projectedCurrentResponse);

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsMatch.Should().BeTrue();
        result.CurrentEtag.Should().Be(expectedProjectedEtag);
        _capturedCurrentStateLoadRequest.IncludeDescriptorProjection.Should().BeTrue();
        _capturedMaterializationRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
        A.CallTo(() =>
                _readableProfileProjector.Project(
                    materializedCurrentResponse,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    private RelationalCurrentEtagPreconditionCheckRequest CreateRequest(
        SqlDialect dialect,
        WritePrecondition.IfMatch precondition
    )
    {
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = CreateMappingSet(dialect, writePlan, readPlan);

        return new RelationalCurrentEtagPreconditionCheckRequest(
            mappingSet,
            readPlan,
            new RelationalWriteTargetContext.ExistingDocument(DocumentId, _documentUuid, 44L),
            precondition
        );
    }

    private static WritePrecondition.IfMatch CreateExactIfMatchPrecondition()
    {
        var currentEtag = RelationalApiMetadataFormatter.FormatEtag(CreateCurrentExternalResponse());
        return new WritePrecondition.IfMatch(currentEtag);
    }

    private static MappingSet CreateMappingSet(
        SqlDialect dialect,
        ResourceWritePlan writePlan,
        ResourceReadPlan readPlan
    )
    {
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(
            OrchestrationTestHelpers.CreateResourceInfo(),
            writePlan,
            readPlan
        );

        return mappingSet with
        {
            Key = mappingSet.Key with { Dialect = dialect },
            Model = mappingSet.Model with { Dialect = dialect },
        };
    }

    private RelationalWriteCurrentState CreateCurrentState(long contentVersion) =>
        new(
            new DocumentMetadataRow(
                DocumentId,
                _documentUuid.Value,
                contentVersion,
                contentVersion,
                new DateTimeOffset(2026, 4, 11, 17, 30, 45, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 11, 17, 30, 45, TimeSpan.Zero)
            ),
            [],
            []
        );

    private static JsonNode CreateCurrentExternalResponse() =>
        JsonNode.Parse(
            """
            {
              "id": "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb",
              "schoolId": 255901,
              "nameOfInstitution": "Lincoln High",
              "link": {
                "rel": "self",
                "href": "/ed-fi/schools/aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"
              },
              "_etag": "stale",
              "_lastModifiedDate": "2026-04-11T17:30:45Z"
            }
            """
        )!;

    private static ReadableEtagProjectionContext CreateReadableEtagProjectionContext() =>
        new(
            new ContentTypeDefinition(
                MemberSelection.IncludeOnly,
                [new PropertyRule("schoolId")],
                [],
                [],
                []
            ),
            new HashSet<string>(["schoolId"], StringComparer.Ordinal)
        );

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

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
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
