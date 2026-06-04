// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespaceAuthorizationExecutor
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbTableName _rootTable = new(_edfiSchema, "GradebookEntry");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    private static readonly string[] _twoPrefixes = ["uri://ed-fi.org/", "uri://gbisd.edu/"];

    private static NamespaceAuthorizationCheckSpec StoredCheck(int index = 0) =>
        new(index, NamespaceAuthorizationCheckValueSource.Stored, _rootTable, _namespaceColumn);

    private static NamespaceAuthorizationCheckSpec ProposedCheck(int index = 0) =>
        new(index, NamespaceAuthorizationCheckValueSource.Proposed, _rootTable, _namespaceColumn);

    [Test]
    public async Task It_authorizes_a_stored_check_and_binds_postgresql_prefixes_as_a_string_array()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])]
        );
        var sut = new NamespaceAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 345L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result.Should().BeOfType<NamespaceAuthorizationExecutionResult.Authorized>();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@documentId", "@namespacePrefixes");
        commandExecutor.Commands[0].Parameters[0].Value.Should().Be(345L);
        commandExecutor
            .Commands[0]
            .Parameters[1]
            .Value.Should()
            .BeAssignableTo<string[]>()
            .Which.Should()
            .Equal("uri://ed-fi.org/%", "uri://gbisd.edu/%");
    }

    [Test]
    public async Task It_binds_sql_server_prefixes_as_one_scalar_parameter_per_prefix()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Mssql,
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])]
        );
        var sut = new NamespaceAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Mssql),
                DocumentId: 346L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result.Should().BeOfType<NamespaceAuthorizationExecutionResult.Authorized>();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@documentId", "@namespacePrefixes_0", "@namespacePrefixes_1");
        commandExecutor.Commands[0].Parameters[1].Value.Should().Be("uri://ed-fi.org/%");
        commandExecutor.Commands[0].Parameters[2].Value.Should().Be("uri://gbisd.edu/%");
    }

    [Test]
    public async Task It_binds_the_proposed_namespace_parameter_for_a_proposed_check()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])]
        );
        var sut = new NamespaceAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 0L,
                ProposedNamespace: "uri://ed-fi.org/Thing",
                [ProposedCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result.Should().BeOfType<NamespaceAuthorizationExecutionResult.Authorized>();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@proposedNamespace", "@namespacePrefixes");
        commandExecutor.Commands[0].Parameters[0].Value.Should().Be("uri://ed-fi.org/Thing");
    }

    [Test]
    public async Task It_maps_a_postgresql_namespace_auth1_failure_to_a_namespace_denial()
    {
        var payloadText = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 347L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        var notAuthorized = result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.NotAuthorized>()
            .Subject;
        notAuthorized.Failure.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        notAuthorized.Failure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
        notAuthorized.Failure.EmittedAuth1Index.Should().Be(0);
        notAuthorized.Failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        // ProblemDetails must present the caller's raw prefixes, not the escaped SQL LIKE patterns.
        notAuthorized
            .Failure.ConfiguredNamespacePrefixes.Should()
            .Equal("uri://ed-fi.org/", "uri://gbisd.edu/");
    }

    [Test]
    public async Task It_maps_failures_with_raw_prefixes_even_when_prefixes_contain_like_metacharacters()
    {
        var payloadText = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 347L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    ["uri://a_b/", "uri://c%d/"],
                    "namespacePrefixes"
                )
            )
        );

        var notAuthorized = result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.NotAuthorized>()
            .Subject;
        // The raw prefixes flow to ProblemDetails verbatim; the backslash-escaped SQL patterns
        // (uri://a\_b/%, uri://c\%d/%) never reach the user-facing message.
        notAuthorized.Failure.ConfiguredNamespacePrefixes.Should().Equal("uri://a_b/", "uri://c%d/");
    }

    [Test]
    public async Task It_maps_a_proposed_missing_failure_for_a_proposed_check()
    {
        var payloadText = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Mssql,
            exceptionToThrow: new StubDbException("AUTH1 - ns1|0|r")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(null, "AUTH1 - " + payloadText)
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Mssql),
                DocumentId: 0L,
                ProposedNamespace: null,
                [ProposedCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        var notAuthorized = result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.NotAuthorized>()
            .Subject;
        notAuthorized
            .Failure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.ProposedNamespaceMissing);
        notAuthorized.Failure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Proposed);
    }

    [Test]
    public async Task It_fails_closed_when_a_namespace_auth1_payload_cannot_be_mapped()
    {
        // ns1 payload whose index is out of range for the single planned check.
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                "ns1|9|m"
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 348L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        var invalidFailure = result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Subject;
        invalidFailure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new SecurityConfigurationFailureDiagnostic(
                    ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed,
                    ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.NamespaceBased],
                    PhysicalPath: "edfi.GradebookEntry.Namespace"
                )
            );
    }

    [Test]
    public async Task It_maps_a_stale_stored_target_auth1_failure_to_a_stale_target_result()
    {
        // 'ns1|0|s' is the stale stored-target kind: the row was deleted between the unlocked target
        // lookup and the stored check, so the executor surfaces StaleTarget instead of a denial.
        var payloadText = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing
            )
        );
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 351L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result.Should().BeOfType<NamespaceAuthorizationExecutionResult.StaleTarget>();
    }

    [Test]
    public async Task It_fails_closed_when_a_stale_payload_index_is_out_of_range()
    {
        // 'ns1|9|s' has no matching planned check; the stale kind must not bypass the invalid-metadata
        // fail-closed path just because it decodes to StoredTargetMissing.
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                "ns1|9|s"
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 352L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidStaleTargetPayload);
    }

    [Test]
    public async Task It_fails_closed_when_a_stale_payload_targets_a_proposed_check_index()
    {
        // 'ns1|1|s' points at the proposed check of an update plan. The compiler only ever raises the
        // stale kind from a stored check, so a stale kind on a proposed index is malformed and must fail
        // closed as invalid metadata rather than become a stale-target result.
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                "ns1|1|s"
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 353L,
                ProposedNamespace: "uri://ed-fi.org/Thing",
                [StoredCheck(0), ProposedCheck(1)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidStaleTargetPayload);
    }

    [TestCase("ns1|bad|m")] // malformed index
    [TestCase("ns1|0|x")] // unknown failure kind
    [TestCase("v2|0|m")] // unknown AUTH1 discriminator
    public async Task It_fails_closed_for_malformed_or_unknown_auth1_payloads(string rawPayload)
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                rawPayload
            )
        );

        var result = await sut.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                DocumentId: 350L,
                ProposedNamespace: null,
                [StoredCheck(0)],
                NamespacePrefixParameterizationFactory.Create(
                    SqlDialect.Pgsql,
                    _twoPrefixes,
                    "namespacePrefixes"
                )
            )
        );

        result
            .Should()
            .BeOfType<NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceInvalidAuth1Payload);
    }

    [Test]
    public async Task It_rethrows_a_relationship_auth1_payload_instead_of_treating_it_as_a_namespace_failure()
    {
        var commandExecutor = new RecordingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new StubDbException("PostgreSQL provider exception")
        );
        var sut = new NamespaceAuthorizationExecutor(
            commandExecutor,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                "1|0|1|0:0:n"
            )
        );

        var act = async () =>
            await sut.ExecuteAsync(
                new NamespaceAuthorizationExecutionRequest(
                    CreateMappingSet(SqlDialect.Pgsql),
                    DocumentId: 349L,
                    ProposedNamespace: null,
                    [StoredCheck(0)],
                    NamespacePrefixParameterizationFactory.Create(
                        SqlDialect.Pgsql,
                        _twoPrefixes,
                        "namespacePrefixes"
                    )
                )
            );

        // A relationship payload surfacing in a namespace-only execution is unexpected; the
        // executor does not swallow it as a namespace failure.
        await act.Should().ThrowAsync<DbException>();
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect) =>
        new(
            new MappingSetKey("schema-hash", dialect, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo("5.2.0", "v1", "schema-hash", 1, [], [], []),
                dialect,
                [],
                [],
                [],
                [],
                [],
                []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class StubRelationshipAuthorizationProviderFailureExtractor(
        string? providerErrorCode,
        string providerMessage
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }

    private sealed class RecordingRelationalCommandExecutor(
        SqlDialect dialect,
        IReadOnlyList<InMemoryRelationalCommandExecution>? executions = null,
        DbException? exceptionToThrow = null
    ) : IRelationalCommandExecutor
    {
        private readonly Queue<InMemoryRelationalCommandExecution> _executions = new(executions ?? []);
        private readonly DbException? _exceptionToThrow = exceptionToThrow;

        public SqlDialect Dialect { get; } = dialect;

        public List<RelationalCommand> Commands { get; } = [];

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(readAsync);

            Commands.Add(command);

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }

            if (!_executions.TryDequeue(out var execution))
            {
                throw new AssertionException(
                    "No in-memory namespace authorization execution was configured for this call."
                );
            }

            await using var reader = new InMemoryRelationalCommandReader(execution.ResultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }
}
