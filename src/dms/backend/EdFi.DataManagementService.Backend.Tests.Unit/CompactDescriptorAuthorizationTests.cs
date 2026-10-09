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
public class Given_CompactDescriptor_CustomViewAuthorization_Values(SqlDialect dialect)
{
    private CompactDescriptorAuthorizationFixture _fixture = null!;
    private IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> _customChecks = null!;
    private ProposedCustomViewRuntimeWork _work = null!;
    private RelationalCommand _customCommand = null!;

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
    }

    [Test]
    public void It_extracts_and_binds_the_actual_compact_descriptor_key()
    {
        _work.SqlValues[0].BasisValue.Should().BeOfType<int>().Which.Should().Be(42);
        _customCommand.Parameters.Single().Value.Should().Be(42);
        VerifyType(_customCommand.Parameters.Single(), DbType.Int32);
    }

    [Test]
    public void It_binds_a_missing_proposed_descriptor_as_a_typed_int_null()
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
    }

    [Test]
    public void It_rejects_wide_document_values_in_proposed_descriptor_bindings()
    {
        var row = _fixture.RootRow(CompactDescriptorAuthorizationFixture.DocumentId);
        ProposedCustomViewValueExtractor
            .Extract([_customChecks[1]], row)
            .Should()
            .BeOfType<ProposedCustomViewExtractionResult.InvalidAuthorizationPlan>();
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
    public async Task It_preserves_stored_and_proposed_custom_view_denials(bool proposed)
    {
        var check = _customChecks[proposed ? 1 : 0];
        var executor = new ThrowingExecutor(dialect);
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

    private sealed class ThrowingExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => dialect;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        ) => throw new ProviderException();
    }
}
