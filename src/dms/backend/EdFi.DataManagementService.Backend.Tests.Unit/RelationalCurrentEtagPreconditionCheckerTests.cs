// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalCurrentEtagPreconditionChecker
{
    private IRelationalWriteCurrentStateLoader _currentStateLoader = null!;
    private IRelationalWriteSession _writeSession = null!;
    private RelationalCurrentEtagPreconditionChecker _sut = null!;
    private RelationalCommand _capturedLockCommand = null!;
    private RelationalWriteCurrentStateLoadRequest _capturedCurrentStateLoadRequest = null!;
    private readonly DocumentUuid _documentUuid = new(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
    private const long DocumentId = 345L;
    private const long LockedContentVersion = 91L;

    [SetUp]
    public void Setup()
    {
        _currentStateLoader = A.Fake<IRelationalWriteCurrentStateLoader>();
        _writeSession = A.Fake<IRelationalWriteSession>();
        _sut = new RelationalCurrentEtagPreconditionChecker(
            _currentStateLoader,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            NullLogger<RelationalCurrentEtagPreconditionChecker>.Instance
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
    }

    [Test]
    public async Task It_uses_a_postgresql_row_lock_and_matches_the_current_composed_etag()
    {
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch(CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion))
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
        result.CurrentEtag.Should().Be(CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion));
        result.TargetContext.ObservedContentVersion.Should().Be(LockedContentVersion);
        _capturedLockCommand.CommandText.Should().Contain("FOR UPDATE");
        _capturedLockCommand.CommandText.Should().Contain("WHERE document.\"DocumentId\" = @documentId");
        _capturedLockCommand.Parameters.Should().ContainSingle();
        _capturedLockCommand.Parameters[0].Name.Should().Be("@documentId");
        _capturedLockCommand.Parameters[0].Value.Should().Be(DocumentId);
        _capturedCurrentStateLoadRequest.TargetContext.ObservedContentVersion.Should().Be(44L);
    }

    [Test]
    public async Task It_uses_a_sql_server_row_lock_and_reports_mismatch_for_a_different_opaque_value()
    {
        var request = CreateRequest(SqlDialect.Mssql, new WritePrecondition.IfMatch("not-the-current-etag"));

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeFalse();
        _capturedLockCommand.CommandText.Should().Contain("WITH (UPDLOCK, HOLDLOCK, ROWLOCK)");
        _capturedLockCommand.CommandText.Should().Contain("WHERE document.[DocumentId] = @documentId");
        _capturedLockCommand.Parameters.Should().ContainSingle();
        _capturedLockCommand.Parameters[0].Name.Should().Be("@documentId");
        _capturedLockCommand.Parameters[0].Value.Should().Be(DocumentId);
        _capturedCurrentStateLoadRequest.TargetContext.ObservedContentVersion.Should().Be(44L);
    }

    [Test]
    public async Task It_ignores_link_and_format_differences_in_the_if_match_value()
    {
        // Client presents an etag captured under links-off / (hypothetical) XML; the checker composes
        // the current tag under links-on / JSON. The state-significant projection drops format and
        // linkFlag, so the precondition still matches.
        var linkAndFormatDivergentEtag = EtagComposer.Compose(
            LockedContentVersion,
            new VariantKey($"{SchemaEpoch(SqlDialect.Pgsql)}.x._.n")
        );
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch(linkAndFormatDivergentEtag)
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_mismatch_when_the_content_version_differs()
    {
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch(CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion - 1))
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_mismatch_when_only_the_schema_epoch_differs()
    {
        // Same ContentVersion, same format/profile/link, but a different schema epoch. schemaEpoch IS
        // state-significant for If-Match (only format/profileCode/linkFlag are projected out), so this
        // must 412. Guards against a refactor accidentally dropping schemaEpoch from the comparison.
        var differentEpochEtag = EtagComposer.Compose(
            LockedContentVersion,
            new VariantKey($"ffffffff.j._.l")
        );
        var request = CreateRequest(SqlDialect.Pgsql, new WritePrecondition.IfMatch(differentEpochEtag));

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_matches_when_the_if_match_value_was_obtained_under_a_profile()
    {
        // Amended 2026-07-04: profileCode is not state-significant for If-Match. An etag captured under
        // a readable profile matches the current tag (composed with no profile) when ContentVersion and
        // schemaEpoch agree — restoring legacy ODS/API cross-profile parity.
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch(
                CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion, profileName: "ReadableProfile")
            )
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
    }

    [Test]
    public async Task It_matches_a_wildcard_precondition_when_the_document_exists()
    {
        // RFC 7232 If-Match: * matches whenever the target exists, regardless of the current etag.
        // A deliberately non-matching Value proves the match comes from the wildcard flag, not the value.
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfMatch("not-the-current-etag", IsWildcard: true)
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
    }

    [Test]
    public async Task It_is_not_satisfied_by_an_if_none_match_wildcard_when_the_document_exists()
    {
        // If-None-Match: * asserts the target does NOT exist. Reaching the checker means the row is
        // locked and present, so the precondition is not satisfied (the write must 412).
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_is_not_satisfied_by_an_if_none_match_tag_that_matches_the_current_projection()
    {
        // If-None-Match with a tag whose state-significant projection matches the current representation
        // means the client's cached copy is current, so the conditional write must fail (412).
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfNoneMatch(CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion))
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_is_satisfied_by_an_if_none_match_tag_that_does_not_match_the_current_projection()
    {
        // A non-matching If-None-Match tag means the client's copy is stale, so the write proceeds.
        var request = CreateRequest(
            SqlDialect.Pgsql,
            new WritePrecondition.IfNoneMatch(CurrentComposedEtag(SqlDialect.Pgsql, LockedContentVersion - 1))
        );

        var result = await _sut.CheckAsync(request, _writeSession);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
    }

    private RelationalCurrentEtagPreconditionCheckRequest CreateRequest(
        SqlDialect dialect,
        WritePrecondition precondition
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

    // Recomposes the etag the checker is expected to produce for the current row: same schema epoch,
    // JSON format, links-on, and (by default) no profile. linkFlag/format/profile are all projected out
    // of the If-Match comparison (amended 2026-07-04); profileName still varies the composed served tag
    // but does not affect the match.
    private static string CurrentComposedEtag(
        SqlDialect dialect,
        long contentVersion,
        string? profileName = null
    ) =>
        EtagComposer.Compose(
            contentVersion,
            VariantKeyFactory.Create(
                BuildMappingSet(dialect).Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(profileName),
                linksEnabled: true
            )
        );

    private static string SchemaEpoch(SqlDialect dialect) =>
        BuildMappingSet(dialect).Key.EffectiveSchemaHash.ToLowerInvariant()[..8];

    private static MappingSet BuildMappingSet(SqlDialect dialect)
    {
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        return CreateMappingSet(dialect, writePlan, readPlan);
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
