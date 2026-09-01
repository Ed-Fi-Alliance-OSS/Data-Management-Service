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
public class Given_OwnershipAuthorizationExecutor
{
    [Test]
    public async Task It_authorizes_and_binds_postgresql_tokens_as_one_short_array()
    {
        var commandExecutor = OwnershipAuthTestDoubles.CleanRun(SqlDialect.Pgsql);
        var sut = new OwnershipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql, documentId: 345L, ownershipTokenIds: [3, 5, 7])
        );

        result.Should().BeOfType<OwnershipAuthorizationExecutionResult.Authorized>();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@documentId", "@ownershipTokenIds");
        commandExecutor.Commands[0].Parameters[0].Value.Should().Be(345L);
        // A short[], not the long[] the relationship claim-EdOrg array path binds: Npgsql infers smallint[]
        // from it, so = ANY(...) compares smallint to smallint and the index on the ownership column holds.
        commandExecutor
            .Commands[0]
            .Parameters[1]
            .Value.Should()
            .BeOfType<short[]>()
            .Which.Should()
            .Equal((short)3, (short)5, (short)7);
    }

    [Test]
    public async Task It_binds_sql_server_tokens_as_one_smallint_scalar_per_token()
    {
        var commandExecutor = OwnershipAuthTestDoubles.CleanRun(SqlDialect.Mssql);
        var sut = new OwnershipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            OwnershipAuthTestDoubles.Request(SqlDialect.Mssql, documentId: 346L, ownershipTokenIds: [7, 3, 3])
        );

        result.Should().BeOfType<OwnershipAuthorizationExecutionResult.Authorized>();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@documentId", "@ownershipTokenIds_0", "@ownershipTokenIds_1");
        // Deduplicated and ascending, and each value is a short so the provider infers smallint rather than
        // widening the comparison against the smallint ownership column.
        commandExecutor.Commands[0].Parameters[1].Value.Should().BeOfType<short>().And.Be((short)3);
        commandExecutor.Commands[0].Parameters[2].Value.Should().BeOfType<short>().And.Be((short)7);
    }

    /// <summary>
    /// A client with no ownership tokens still runs the check — that is what keeps §2.14 distinguishable
    /// from §2.13 — but binds only the document id. Binding an empty token parameter would leave the command
    /// declaring a parameter the constant-false predicate never references.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_binds_no_token_parameter_when_the_client_holds_no_tokens(SqlDialect dialect)
    {
        var commandExecutor = OwnershipAuthTestDoubles.CleanRun(dialect);
        var sut = new OwnershipAuthorizationExecutor(commandExecutor);

        var result = await sut.ExecuteAsync(
            OwnershipAuthTestDoubles.Request(dialect, documentId: 347L, ownershipTokenIds: [])
        );

        result.Should().BeOfType<OwnershipAuthorizationExecutionResult.Authorized>();
        commandExecutor
            .Commands[0]
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .Equal("@documentId");
        commandExecutor.Commands[0].CommandText.Should().Contain("1 = 0");
    }

    [Test]
    public async Task It_maps_a_postgresql_mismatch_payload_to_an_ownership_denial()
    {
        var sut = OwnershipAuthTestDoubles.FailingExecutor(
            SqlDialect.Pgsql,
            OwnershipAuthTestDoubles.EncodePayload(
                1,
                OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch
            )
        );

        var result = await sut.ExecuteAsync(
            OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql, rawConfiguredIndex: 1)
        );

        var failure = result
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.NotAuthorized>()
            .Which.Failure;
        failure.FailureKind.Should().Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
        failure.ConfiguredStrategyIndex.Should().Be(1);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The SQL Server transport differs — the payload arrives inside the provider message rather than as a
    /// SqlState — so both are proven, and the uninitialized kind maps to its own §2.14 failure rather than
    /// collapsing into the mismatch case.
    /// </summary>
    [Test]
    public async Task It_maps_a_sql_server_uninitialized_payload_to_an_ownership_denial()
    {
        var sut = OwnershipAuthTestDoubles.FailingExecutor(
            SqlDialect.Mssql,
            OwnershipAuthTestDoubles.EncodePayload(
                0,
                OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized
            )
        );

        var result = await sut.ExecuteAsync(OwnershipAuthTestDoubles.Request(SqlDialect.Mssql));

        result
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.NotAuthorized>()
            .Which.Failure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_maps_a_stale_target_payload_to_a_stale_target_result(SqlDialect dialect)
    {
        var sut = OwnershipAuthTestDoubles.FailingExecutor(
            dialect,
            OwnershipAuthTestDoubles.EncodePayload(
                0,
                OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing
            )
        );

        var result = await sut.ExecuteAsync(OwnershipAuthTestDoubles.Request(dialect));

        result.Should().BeOfType<OwnershipAuthorizationExecutionResult.StaleTarget>();
    }

    /// <summary>
    /// A payload whose configured index is not the planned check's cannot have come from this request's
    /// check, so it fails closed as a security-configuration 500 and is never reported as a denial. Doing
    /// otherwise would attribute a 403 to a configured position that did not deny the request.
    /// </summary>
    [Test]
    public async Task It_maps_a_configured_index_mismatch_to_a_security_configuration_failure()
    {
        var sut = OwnershipAuthTestDoubles.FailingExecutor(
            SqlDialect.Pgsql,
            OwnershipAuthTestDoubles.EncodePayload(
                2,
                OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch
            )
        );

        var result = await sut.ExecuteAsync(
            OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql, rawConfiguredIndex: 1)
        );

        result.Should().NotBeOfType<OwnershipAuthorizationExecutionResult.NotAuthorized>();
        var invalid = result
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which;
        invalid
            .FailureMessage.Should()
            .Be(OwnershipAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata);
        invalid
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new SecurityConfigurationFailureDiagnostic(
                    ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed,
                    ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.OwnershipBased],
                    ConfiguredStrategyIndexes: [1]
                )
            );
    }

    /// <summary>
    /// An <c>own1|</c>-prefixed payload that cannot be decoded is still ours: the ownership check is the only
    /// thing that emits that discriminator, so no other family may answer for it. It fails closed as a
    /// security-configuration 500 because nothing decodable remains to attribute a denial with.
    /// </summary>
    [TestCase("own1|")]
    [TestCase("own1|x|m")]
    [TestCase("own1|0|zzz")]
    [TestCase("own1|-1|m")]
    [TestCase("own1|0|m|extra")]
    public async Task It_claims_a_malformed_ownership_payload_as_a_security_configuration_failure(
        string malformedPayload
    )
    {
        var sut = OwnershipAuthTestDoubles.FailingExecutor(SqlDialect.Pgsql, malformedPayload);

        var result = await sut.ExecuteAsync(OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql));

        result
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed);
    }

    /// <summary>
    /// A payload belonging to another AUTH1 family is not this executor's to answer, so the provider
    /// exception propagates for the family that owns the discriminator to classify. The relationship case is
    /// the one that matters most: its <c>1|</c> discriminator is a substring of <c>own1|</c>, and only
    /// because the dispatcher anchors the match at the start of the payload do the two stay distinct.
    /// </summary>
    [Test]
    public async Task It_does_not_claim_a_payload_from_another_authorization_family()
    {
        string[] foreignPayloads =
        [
            RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
                new RelationshipAuthorizationAuth1FailurePayload(
                    0,
                    [
                        new RelationshipAuthorizationAuth1SubjectFailure(
                            0,
                            0,
                            RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                        ),
                    ]
                )
            ),
            NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                new NamespaceAuthorizationAuth1FailurePayload(
                    0,
                    NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                )
            ),
            CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(
                new CustomViewAuthorizationAuth1FailurePayload(
                    0,
                    CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
                )
            ),
            // Undecodable and not ours either: the discriminator decides, so this must not be claimed
            // simply because nothing else could parse it.
            "zzz|0|m",
        ];

        foreach (var foreignPayload in foreignPayloads)
        {
            var sut = OwnershipAuthTestDoubles.FailingExecutor(SqlDialect.Pgsql, foreignPayload);

            Func<Task> act = () => sut.ExecuteAsync(OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql));

            await act.Should()
                .ThrowAsync<OwnershipAuthStubDbException>(
                    $"payload '{foreignPayload}' belongs to another family"
                );
        }
    }

    [Test]
    public async Task It_does_not_claim_a_provider_failure_that_carries_no_auth1_payload()
    {
        var commandExecutor = new OwnershipAuthRecordingCommandExecutor(
            SqlDialect.Pgsql,
            exceptionToThrow: new OwnershipAuthStubDbException(
                "duplicate key value violates unique constraint"
            )
        );
        var sut = new OwnershipAuthorizationExecutor(
            commandExecutor,
            new OwnershipAuthStubProviderFailureExtractor(
                "23505",
                "duplicate key value violates unique constraint"
            )
        );

        Func<Task> act = () => sut.ExecuteAsync(OwnershipAuthTestDoubles.Request(SqlDialect.Pgsql));

        await act.Should().ThrowAsync<OwnershipAuthStubDbException>();
    }

    [Test]
    public async Task It_rejects_a_null_request()
    {
        var sut = new OwnershipAuthorizationExecutor(OwnershipAuthTestDoubles.CleanRun(SqlDialect.Pgsql));

        Func<Task> act = () => sut.ExecuteAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_OwnershipAuthorizationProviderFailureMapper
{
    /// <summary>
    /// An ownership payload raised against a request that planned no ownership check at all. The executor
    /// cannot reach this — it always carries a check — but a co-batched caller can pass no planned index, and
    /// the payload must then fail closed as a 500 rather than be attributed to a strategy the plan never had.
    /// </summary>
    [Test]
    public void It_maps_an_ownership_payload_with_no_planned_check_to_a_security_configuration_failure()
    {
        var mapped = Map(
            OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch,
            plannedConfiguredStrategyIndex: null
        );

        var invalid = mapped
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which;
        invalid
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new SecurityConfigurationFailureDiagnostic(
                    ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.OwnershipAuth1PayloadMappingFailed,
                    ConfiguredStrategyNames: [AuthorizationStrategyNameConstants.OwnershipBased],
                    // No planned index to report, so none is claimed.
                    ConfiguredStrategyIndexes: []
                )
            );
    }

    /// <summary>
    /// A stale-target payload with no planned check reports its own diagnostic kind: the retry path emitted
    /// a check the plan does not contain, which is a different fault from a payload whose index is not ours.
    /// </summary>
    [Test]
    public void It_reports_a_stale_target_payload_with_no_planned_check_under_its_own_diagnostic_kind()
    {
        var mapped = Map(
            OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing,
            plannedConfiguredStrategyIndex: null
        );

        mapped
            .Should()
            .BeOfType<OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.OwnershipInvalidStaleTargetPayload);
    }

    /// <summary>
    /// A namespace payload arriving while no ownership check was planned is refused rather than reported as
    /// an ownership security-configuration failure. Without this, consulting ownership first on a co-batched
    /// command would convert every namespace denial into an ownership 500.
    /// </summary>
    [Test]
    public void It_refuses_a_foreign_payload_even_when_no_ownership_check_was_planned()
    {
        var claimed = OwnershipAuthorizationProviderFailureMapper.TryMapOwnershipAuthorizationFailure(
            SqlDialect.Pgsql,
            new OwnershipAuthStubDbException("PostgreSQL provider exception"),
            new OwnershipAuthStubProviderFailureExtractor(
                OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
                    new NamespaceAuthorizationAuth1FailurePayload(
                        0,
                        NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
                    )
                )
            ),
            plannedConfiguredStrategyIndex: null,
            out var result
        );

        claimed.Should().BeFalse();
        result.Should().BeNull();
    }

    private static OwnershipAuthorizationExecutionResult Map(
        OwnershipAuthorizationAuth1FailureKind failureKind,
        int? plannedConfiguredStrategyIndex
    )
    {
        var claimed = OwnershipAuthorizationProviderFailureMapper.TryMapOwnershipAuthorizationFailure(
            SqlDialect.Pgsql,
            new OwnershipAuthStubDbException("PostgreSQL provider exception"),
            new OwnershipAuthStubProviderFailureExtractor(
                OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                OwnershipAuthTestDoubles.EncodePayload(0, failureKind)
            ),
            plannedConfiguredStrategyIndex,
            out var result
        );

        claimed.Should().BeTrue();
        return result!;
    }
}

internal static class OwnershipAuthTestDoubles
{
    public static string EncodePayload(
        int configuredStrategyIndex,
        OwnershipAuthorizationAuth1FailureKind failureKind
    ) =>
        OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
            new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind)
        );

    public static OwnershipAuthRecordingCommandExecutor CleanRun(SqlDialect dialect) =>
        new(dialect, [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])]);

    public static OwnershipAuthorizationExecutor FailingExecutor(SqlDialect dialect, string payloadText) =>
        new(
            new OwnershipAuthRecordingCommandExecutor(
                dialect,
                exceptionToThrow: new OwnershipAuthStubDbException("provider exception")
            ),
            new OwnershipAuthStubProviderFailureExtractor(
                OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                BuildProviderMessage(dialect, payloadText)
            )
        );

    public static OwnershipAuthorizationExecutionRequest Request(
        SqlDialect dialect,
        long documentId = 100L,
        short[]? ownershipTokenIds = null,
        int rawConfiguredIndex = 0
    ) =>
        new(
            CreateMappingSet(dialect),
            documentId,
            new OwnershipAuthorizationCheckSpec(rawConfiguredIndex),
            OwnershipTokenParameterizationFactory.Create(
                dialect,
                ownershipTokenIds ?? [11],
                "ownershipTokenIds"
            )
        );

    /// <remarks>
    /// PostgreSQL carries the payload as the SqlState with the payload as the message; SQL Server has no
    /// custom SqlState and carries it inside the message as <c>AUTH1 - payload</c>. Building the message
    /// per dialect is what makes the dialect-specific transport, not just the codec, part of the test.
    /// </remarks>
    private static string BuildProviderMessage(SqlDialect dialect, string payloadText) =>
        dialect is SqlDialect.Mssql
            ? $"{OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode} - {payloadText}"
            : payloadText;

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
}

internal sealed class OwnershipAuthStubDbException(string message) : DbException(message);

internal sealed class OwnershipAuthStubProviderFailureExtractor(
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

internal sealed class OwnershipAuthRecordingCommandExecutor(
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
                "No in-memory ownership authorization execution was configured for this call."
            );
        }

        await using var reader = new InMemoryRelationalCommandReader(execution.ResultSets);
        return await readAsync(reader, cancellationToken).ConfigureAwait(false);
    }
}
