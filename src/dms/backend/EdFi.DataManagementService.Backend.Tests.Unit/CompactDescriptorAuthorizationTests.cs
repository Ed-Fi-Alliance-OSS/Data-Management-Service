// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_CompactDescriptor_NamespaceAuthorization_And_CustomViewAuthorization_Values(
    SqlDialect dialect
)
{
    private CompactDescriptorAuthorizationFixture _fixture = null!;
    private IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> _customChecks = null!;
    private IReadOnlyList<NamespaceAuthorizationCheckSpec> _namespaceChecks = null!;
    private ProposedCustomViewRuntimeWork _work = null!;
    private RelationalCommand _customCommand = null!;
    private RelationalCommand _namespaceCommand = null!;
    private NamespacePrefixParameterization Prefixes =>
        NamespacePrefixParameterizationFactory.Create(
            dialect,
            ["uri://Example.org/Kind "],
            "namespacePrefixes"
        );

    [SetUp]
    public void Setup()
    {
        _fixture = new(dialect);
        _customChecks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Subject,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.EdFiKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        _namespaceChecks = (
            (NamespaceAuthorizationPlanOutcome.Plan)
                NamespaceAuthorizationPlanner.Plan(
                    _fixture.Subject,
                    NamespaceAuthorizationOperation.Update,
                    new([], ["uri://Example.org/Kind "])
                )
        ).Checks;
        _work = (
            (ProposedCustomViewExtractionResult.Ready)
                ProposedCustomViewValueExtractor.Extract(
                    [_customChecks[1]],
                    _fixture.RootRow(CompactDescriptorAuthorizationFixture.DescriptorId)
                )
        ).Work;
        _customCommand = ProposedCustomViewAuthorizationCommand
            .Build(_fixture.MappingSet, new(_customChecks, _work.SqlValues))!
            .Command;
        var ready = (
            (ProposedNamespaceValueExtractionResult.Ready)
                ProposedNamespaceValueExtractor.Extract(
                    [_namespaceChecks[1]],
                    _fixture.RootRow(CompactDescriptorAuthorizationFixture.DescriptorId)
                )
        );
        var plan = new NamespaceAuthorizationSqlCompiler(dialect).Compile(
            new(_namespaceChecks, Prefixes, "documentId", "proposedNamespace")
        );
        _namespaceCommand = NamespaceAuthorizationExecutor.BuildCommand(
            plan,
            new(
                _fixture.MappingSet,
                CompactDescriptorAuthorizationFixture.DocumentId,
                ready.ProposedNamespace,
                _namespaceChecks,
                Prefixes,
                ready.ProposedDescriptorId
            )
        );
    }

    [Test]
    public void It_extracts_and_binds_the_actual_compact_key_without_narrowing_the_owning_document_id()
    {
        _work.SqlValues[0].BasisValue.Should().BeOfType<int>().Which.Should().Be(42);
        _customCommand.Parameters.Single().Value.Should().Be(42);
        _namespaceCommand
            .Parameters.Single(p => p.Name == "@proposedNamespace")
            .Value.Should()
            .BeOfType<int>()
            .Which.Should()
            .Be(42);
        _namespaceCommand
            .Parameters.Single(p => p.Name == "@documentId")
            .Value.Should()
            .BeOfType<long>()
            .Which.Should()
            .Be(5000000042L);
        VerifyType(_customCommand.Parameters.Single(), DbType.Int32);
        VerifyType(_namespaceCommand.Parameters.Single(p => p.Name == "@proposedNamespace"), DbType.Int32);
        VerifyType(_namespaceCommand.Parameters.Single(p => p.Name == "@documentId"), DbType.Int64);
    }

    [Test]
    public void It_binds_a_missing_proposed_descriptor_as_a_typed_int_null_for_both_authorizers()
    {
        var work = (
            (ProposedCustomViewExtractionResult.Ready)
                ProposedCustomViewValueExtractor.Extract([_customChecks[1]], _fixture.RootRow(null))
        ).Work;
        var command = ProposedCustomViewAuthorizationCommand
            .Build(_fixture.MappingSet, new(_customChecks, work.SqlValues))!
            .Command;
        command.Parameters.Single().Value.Should().BeNull();
        VerifyType(command.Parameters.Single(), DbType.Int32);
        var nsPlan = new NamespaceAuthorizationSqlCompiler(dialect).Compile(
            new([_namespaceChecks[1]], Prefixes, "documentId", "proposedNamespace")
        );
        var ns = NamespaceAuthorizationExecutor.BuildCommand(
            nsPlan,
            new(_fixture.MappingSet, 0, null, [_namespaceChecks[1]], Prefixes)
        );
        ns.Parameters.Single(p => p.Name == "@proposedNamespace").Value.Should().BeNull();
        VerifyType(ns.Parameters.Single(p => p.Name == "@proposedNamespace"), DbType.Int32);
    }

    [Test]
    public void It_rejects_wide_document_values_in_proposed_descriptor_bindings()
    {
        var row = _fixture.RootRow(CompactDescriptorAuthorizationFixture.DocumentId);
        ProposedCustomViewValueExtractor
            .Extract([_customChecks[1]], row)
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
        ProposedNamespaceValueExtractor
            .Extract([_namespaceChecks[1]], row)
            .Should()
            .BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_keeps_indirect_descriptor_basis_parameters_wide()
    {
        var checks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Intermediate,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.EdFiKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        var command = ProposedCustomViewAuthorizationCommand
            .Build(
                _fixture.MappingSet,
                new(checks, [new(checks[1], CompactDescriptorAuthorizationFixture.DocumentId)])
            )!
            .Command;
        command.Parameters.Single().Value.Should().Be(5000000042L);
        VerifyType(command.Parameters.Single(), DbType.Int64);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_stored_and_proposed_namespace_permission_results(bool proposed)
    {
        var check = _namespaceChecks[proposed ? 1 : 0] with { Index = 0 };
        var executor = new RecordingExecutor(dialect);
        var result = await new NamespaceAuthorizationExecutor(executor).ExecuteAsync(
            new(_fixture.MappingSet, 5000000042L, null, [check], Prefixes, 42)
        );
        result.Should().BeOfType<NamespaceAuthorizationExecutionResult.Authorized>();
        executor
            .Command!.CommandText.Should()
            .Contain(dialect is SqlDialect.Pgsql ? "dn.\"DescriptorId\"" : "dn.[DescriptorId]");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_stored_and_proposed_namespace_denials(bool proposed)
    {
        var check = _namespaceChecks[proposed ? 1 : 0] with { Index = 0 };
        var executor = new RecordingExecutor(dialect, true);
        var payload = $"ns1|{check.Index}|m";
        var result = await new NamespaceAuthorizationExecutor(
            executor,
            new FailureExtractor(dialect, payload)
        ).ExecuteAsync(new(_fixture.MappingSet, 5000000042L, null, [check], Prefixes, 42));
        var failure = result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.NotAuthorized>()
            .Which.Failure;
        failure
            .ValueSource.Should()
            .Be(
                proposed
                    ? Core.External.Backend.NamespaceAuthorizationFailureValueSource.Proposed
                    : Core.External.Backend.NamespaceAuthorizationFailureValueSource.Stored
            );
        failure.ConfiguredNamespacePrefixes.Should().Equal("uri://Example.org/Kind ");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_stored_and_proposed_custom_view_denials(bool proposed)
    {
        var check = _customChecks[proposed ? 1 : 0];
        var executor = new RecordingExecutor(dialect, true);
        var payload = $"cv1|{check.Index}|n";
        if (proposed)
        {
            CustomViewAuthorizationProviderFailureMapper
                .TryMapCustomViewAuthorizationFailure(
                    dialect,
                    new ProviderException(),
                    new FailureExtractor(dialect, payload),
                    _customChecks,
                    out var failure
                )
                .Should()
                .BeTrue();
            failure.Should().NotBeNull();
        }
        else
        {
            var result = await new CustomViewAuthorizationExecutor(
                executor,
                new FailureExtractor(dialect, payload)
            ).ExecuteAsync(new(_fixture.MappingSet, 5000000042L, [check], _customChecks));
            result.Should().BeOfType<CustomViewAuthorizationExecutionResult.NotAuthorized>();
        }
    }

    [Test]
    public void It_extracts_the_correct_key_for_a_same_named_descriptor_type_in_another_project()
    {
        var checks = (
            (SingleRecordCustomViewAuthorizationPlanOutcome.Plan)
                SingleRecordCustomViewAuthorizationPlanner.Plan(
                    _fixture.MappingSet,
                    _fixture.Subject,
                    [
                        CompactDescriptorAuthorizationFixture.Strategy(
                            CompactDescriptorAuthorizationFixture.SampleKind
                        ),
                    ],
                    NamespaceAuthorizationOperation.Update
                )
        ).Checks;
        var work = (
            (ProposedCustomViewExtractionResult.Ready)
                ProposedCustomViewValueExtractor.Extract([checks[1]], _fixture.RootRow(42))
        ).Work;
        var command = ProposedCustomViewAuthorizationCommand
            .Build(_fixture.MappingSet, new(checks, work.SqlValues))!
            .Command;
        command.Parameters.Single().Value.Should().BeOfType<int>().Which.Should().Be(73);
    }

    private void VerifyType(RelationalParameter parameter, DbType expected)
    {
        using var source = NpgsqlDataSource.Create("Host=localhost");
        using var connection = source.CreateConnection();
        using DbCommand command = dialect is SqlDialect.Pgsql ? connection.CreateCommand() : new SqlCommand();
        var native = command.CreateParameter();
        native.Value = parameter.Value ?? DBNull.Value;
        parameter.ConfigureParameter?.Invoke(native);
        native.DbType.Should().Be(expected);
    }

    private sealed class ProviderException() : DbException("authorization denied");

    private sealed class FailureExtractor(SqlDialect dialect, string payload)
        : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception) =>
            new(
                dialect is SqlDialect.Pgsql ? "AUTH1" : null,
                dialect is SqlDialect.Pgsql ? payload : $"AUTH1 - {payload}"
            );
    }

    private sealed class RecordingExecutor(SqlDialect dialect, bool deny = false) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => dialect;
        public RelationalCommand? Command { get; private set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            Command = command;
            if (deny)
            {
                throw new ProviderException();
            }
            await using var reader = new InMemoryRelationalCommandReader([
                InMemoryRelationalResultSet.Create(),
            ]);
            return await readAsync(reader, cancellationToken);
        }
    }
}
