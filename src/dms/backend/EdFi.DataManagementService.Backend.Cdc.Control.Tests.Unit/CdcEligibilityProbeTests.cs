// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The read-only pre-binding eligibility gate. It reports what one consistent read observed rather
/// than a verdict — the retry classifier decides what an occupied or non-disabled database means —
/// and evidence it could not obtain is reported as unknown with blocking row presence, never as an
/// empty database.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcEligibilityProbe")]
public class Given_CdcEligibilityProbeObservationMapping
{
    private const string OperationId = "operation-1";
    private const string SetupControllerRunId = "run-1";
    private const string ProofId = "proof-1";
    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const string ConsistencyToken = "1234:1240:";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DurableObservedAt = ObservedAt.AddMilliseconds(-25);

    [Test]
    public void It_reports_an_empty_new_database_as_eligible_evidence()
    {
        InitialCdcEligibilityObservation observation = Map(Reading());

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(CdcLifecycleState.Disabled);
        observation.CacheAheadState.Should().Be(CdcCacheAheadState.Clear);
        observation.CanonicalRowsPresent.Should().BeFalse();
        observation.CacheRowsPresent.Should().BeFalse();
        observation.WorkRowsPresent.Should().BeFalse();
        observation.ConsistencyScope.Should().Be(CdcConsistencyScope.SingleProviderTransaction);
        observation.ProviderConsistencyToken.Should().Be(ConsistencyToken);
        observation.DurableObservedAt.Should().Be(DurableObservedAt);
        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value);
        observation.Diagnostics.Should().BeEmpty();
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_reports_a_canonical_row_without_reclassifying_it()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(canonicalRowsPresent: true));

        using var _ = new AssertionScope();
        observation.CanonicalRowsPresent.Should().BeTrue();
        observation.CacheRowsPresent.Should().BeFalse();
        observation.WorkRowsPresent.Should().BeFalse();
        observation.LifecycleState.Should().Be(CdcLifecycleState.Disabled);

        // Row presence is a legitimate observation that the retry classifier rejects on; the probe
        // reports it faithfully rather than degrading the rest of the evidence.
        Diagnostic(Validate(observation), "$.canonicalRowsPresent").Should().NotBeNull();
    }

    [Test]
    public void It_reports_a_cache_row_without_reclassifying_it()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(cacheRowsPresent: true));

        using var _ = new AssertionScope();
        observation.CacheRowsPresent.Should().BeTrue();
        observation.CanonicalRowsPresent.Should().BeFalse();
        Diagnostic(Validate(observation), "$.cacheRowsPresent").Should().NotBeNull();
    }

    [Test]
    public void It_reports_a_work_row_without_reclassifying_it()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(workRowsPresent: true));

        using var _ = new AssertionScope();
        observation.WorkRowsPresent.Should().BeTrue();
        observation.CanonicalRowsPresent.Should().BeFalse();
        Diagnostic(Validate(observation), "$.workRowsPresent").Should().NotBeNull();
    }

    [TestCase("Disabled", CdcLifecycleState.Disabled)]
    [TestCase("Resetting", CdcLifecycleState.Resetting)]
    [TestCase("Rebuilding", CdcLifecycleState.Rebuilding)]
    [TestCase("Tracking", CdcLifecycleState.Tracking)]
    public void It_maps_each_durable_lifecycle_state(string lifecycleToken, CdcLifecycleState expected)
    {
        InitialCdcEligibilityObservation observation = Map(Reading(lifecycleStateToken: lifecycleToken));

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(expected);
        observation.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_reports_a_set_cache_ahead_latch()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(cacheAheadRecoveryRequired: true));

        using var _ = new AssertionScope();
        observation.CacheAheadState.Should().Be(CdcCacheAheadState.RecoveryRequired);
        observation.Diagnostics.Should().BeEmpty();

        // A published latch is authoritative evidence, so the observation itself stays contract-valid
        // and the classifier is the one that refuses to bind.
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [TestCase(null)]
    [TestCase("Draining")]
    public void It_reports_an_unreadable_lifecycle_state_as_unknown(string? lifecycleToken)
    {
        InitialCdcEligibilityObservation observation = Map(Reading(lifecycleStateToken: lifecycleToken));

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(CdcLifecycleState.Unknown);
        Code(observation, "eligibilityLifecycleUnreadable").Should().NotBeNull();

        // An unknown lifecycle is not authoritative evidence, so the observation cannot pass its own
        // contract and enablement stays closed.
        Validate(observation).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_an_unreadable_cache_ahead_latch_as_unknown()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(cacheAheadRecoveryRequired: null));

        using var _ = new AssertionScope();
        observation.CacheAheadState.Should().Be(CdcCacheAheadState.Unknown);
        Code(observation, "eligibilityCacheAheadLatchUnreadable").Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_an_absent_data_store_identity_without_a_fingerprint()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(sourceIdentity: null));

        using var _ = new AssertionScope();
        observation.PhysicalSourceFingerprint.Should().BeNull();
        observation.LifecycleState.Should().Be(CdcLifecycleState.Disabled);
        CdcDiagnostic? diagnostic = Code(observation, "eligibilityPhysicalSourceUnusable");
        diagnostic.Should().NotBeNull();
        diagnostic!.ArtifactName.Should().Be(DataStoreIdentityTableDefinition.TableDisplayName);
        diagnostic.Observed.Should().Be("absent");
    }

    [TestCase("not-a-uuid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void It_reports_an_unusable_source_identity_without_a_fingerprint(string sourceIdentity)
    {
        InitialCdcEligibilityObservation observation = Map(Reading(sourceIdentity: sourceIdentity));

        using var _ = new AssertionScope();
        observation.PhysicalSourceFingerprint.Should().BeNull();
        Code(observation, "eligibilityPhysicalSourceUnusable")!.Observed.Should().Be("malformed");

        // The rejected identity itself is never carried onto the observation.
        observation
            .Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Observed != null
                && diagnostic.Observed.Contains(sourceIdentity, StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_accepts_a_source_identity_the_provider_reports_in_upper_case()
    {
        InitialCdcEligibilityObservation observation = Map(
            Reading(sourceIdentity: SourceIdentity.ToUpperInvariant())
        );

        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value);
    }

    [Test]
    public void It_computes_the_fingerprint_for_the_target_provider()
    {
        InitialCdcEligibilityObservation observation = Map(Reading(), Ddl.CdcProvider.SqlServer);

        using var _ = new AssertionScope();
        observation.Provider.Should().Be(CdcProvider.SqlServer);
        observation
            .PhysicalSourceFingerprint.Should()
            .Be(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.SqlServer).Value);
        observation
            .PhysicalSourceFingerprint.Should()
            .NotBe(CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value);
    }

    [TestCase(CdcEligibilityReadOutcome.SchemaIncomplete, "eligibilitySchemaIncomplete")]
    [TestCase(CdcEligibilityReadOutcome.Unreadable, "eligibilityStateUnreadable")]
    public void It_reports_an_unreadable_database_as_blocking_rather_than_empty(
        CdcEligibilityReadOutcome outcome,
        string diagnosticCode
    )
    {
        InitialCdcEligibilityObservation observation = Map(new(outcome, null, "summary"));

        using var _ = new AssertionScope();
        observation.LifecycleState.Should().Be(CdcLifecycleState.Unknown);
        observation.CacheAheadState.Should().Be(CdcCacheAheadState.Unknown);

        // Row presence has no unknown in the shared contract, so an unreadable database reports the
        // value that blocks rather than the one that would let enablement proceed.
        observation.CanonicalRowsPresent.Should().BeTrue();
        observation.CacheRowsPresent.Should().BeTrue();
        observation.WorkRowsPresent.Should().BeTrue();
        observation.PhysicalSourceFingerprint.Should().BeNull();
        Code(observation, diagnosticCode).Should().NotBeNull();
        Validate(observation).Succeeded.Should().BeFalse();
    }

    [Test]
    public void It_reports_an_unreadable_database_with_an_unusable_consistency_token()
    {
        InitialCdcEligibilityObservation observation = Map(
            new(CdcEligibilityReadOutcome.Unreadable, null, "summary")
        );

        Diagnostic(Validate(observation), "$.providerConsistencyToken").Should().NotBeNull();
    }

    [Test]
    public void It_reports_a_successful_outcome_that_carries_no_reading_as_unreadable()
    {
        InitialCdcEligibilityObservation observation = Map(
            new(CdcEligibilityReadOutcome.Succeeded, null, null)
        );

        observation.LifecycleState.Should().Be(CdcLifecycleState.Unknown);
    }

    [Test]
    public void It_carries_the_provisioning_proof_correlation_onto_the_observation()
    {
        InitialCdcEligibilityObservation observation = Map(Reading());

        using var _ = new AssertionScope();
        observation.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity(Ddl.CdcProvider.Postgresql));
        observation.Provider.Should().Be(CdcProvider.Postgresql);
        observation.SetupControllerRunId.Should().Be(SetupControllerRunId);
        observation.WriteAdmissionProofId.Should().Be(ProofId);
    }

    /// <summary>
    /// The shared contract requires the durable read not to be later than the observation that reports
    /// it, so a provider clock marginally ahead of the control plane must not turn a good read into a
    /// malformed observation.
    /// </summary>
    [Test]
    public void It_never_reports_durable_state_observed_after_the_observation_itself()
    {
        InitialCdcEligibilityObservation observation = Map(
            Reading(durableObservedAt: ObservedAt.AddSeconds(2))
        );

        using var _ = new AssertionScope();
        observation.DurableObservedAt.Should().Be(ObservedAt.AddSeconds(2));
        observation.ObservedAt.Should().Be(ObservedAt.AddSeconds(2));
        Validate(observation).Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_a_missing_read_result()
    {
        Action mapping = () =>
            CdcEligibilityObservationMapper.Map(
                Context(Ddl.CdcProvider.Postgresql),
                Proof(Ddl.CdcProvider.Postgresql),
                null!,
                ObservedAt
            );

        mapping.Should().Throw<ArgumentNullException>();
    }

    private static InitialCdcEligibilityObservation Map(
        CdcEligibilityReadResult read,
        Ddl.CdcProvider provider = Ddl.CdcProvider.Postgresql
    ) => CdcEligibilityObservationMapper.Map(Context(provider), Proof(provider), read, ObservedAt);

    private static CdcEligibilityReadResult Reading(
        string? lifecycleStateToken = "Disabled",
        bool? cacheAheadRecoveryRequired = false,
        bool canonicalRowsPresent = false,
        bool cacheRowsPresent = false,
        bool workRowsPresent = false,
        string? sourceIdentity = SourceIdentity,
        DateTimeOffset? durableObservedAt = null
    ) =>
        new(
            CdcEligibilityReadOutcome.Succeeded,
            new(
                durableObservedAt ?? DurableObservedAt,
                ConsistencyToken,
                lifecycleStateToken,
                cacheAheadRecoveryRequired,
                canonicalRowsPresent,
                cacheRowsPresent,
                workRowsPresent,
                sourceIdentity
            ),
            null
        );

    private static CdcContractValidationResult Validate(InitialCdcEligibilityObservation observation) =>
        InitialCdcEligibilityObservationValidator.Validate(
            observation,
            Proof(ToDdlProvider(observation.Provider)),
            new(
                OperationId,
                observation.TargetIdentity,
                PhysicalSourceFingerprint: null,
                observation.ObservedAt.AddMinutes(1)
            )
        );

    private static CdcObservationContext Context(Ddl.CdcProvider provider) =>
        new(OperationId, TargetIdentity(provider), PhysicalSourceFingerprint: null);

    private static InitialCdcProvisioningProof Proof(Ddl.CdcProvider provider) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            ProofId,
            OperationId,
            TargetIdentity(provider),
            TargetIdentity(provider).Provider,
            SetupControllerRunId,
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            ObservedAt.AddMinutes(-1)
        );

    private static CdcTargetIdentity TargetIdentity(Ddl.CdcProvider provider) =>
        CdcControlTemplateTestData.BuildTargetIdentity(provider);

    private static Ddl.CdcProvider ToDdlProvider(CdcProvider provider) =>
        provider == CdcProvider.Postgresql ? Ddl.CdcProvider.Postgresql : Ddl.CdcProvider.SqlServer;

    private static CdcDiagnostic? Code(InitialCdcEligibilityObservation observation, string code) =>
        observation.Diagnostics.SingleOrDefault(diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal)
        );

    private static CdcDiagnostic? Diagnostic(CdcContractValidationResult result, string path) =>
        result.Diagnostics.FirstOrDefault(diagnostic =>
            string.Equals(diagnostic.Path, path, StringComparison.Ordinal)
        );
}

/// <summary>
/// The statements the eligibility gate issues. The gate's two structural guarantees — that it takes
/// no administrative mutex and mutates nothing — are properties of these statements, so they are
/// asserted directly rather than inferred from the code that runs them.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcEligibilityProbe")]
public class Given_CdcEligibilitySql
{
    private static readonly string[] MutationTokens =
    [
        "INSERT",
        "UPDATE",
        "DELETE",
        "MERGE",
        "TRUNCATE",
        "CREATE",
        "ALTER",
        "DROP",
    ];

    private static readonly string[] LockTokens =
    [
        "pg_advisory",
        "sp_getapplock",
        "LOCK TABLE",
        "TABLOCK",
        "HOLDLOCK",
        "UPDLOCK",
        "XLOCK",
        "FOR UPDATE",
        "FOR SHARE",
    ];

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_never_mutates_and_never_takes_a_lock(SqlDialect dialect)
    {
        string[] statements =
        [
            CdcEligibilitySql.RenderReadOnlyTransactionCommandText(dialect) ?? string.Empty,
            CdcEligibilitySql.RenderTableExistenceCommandText(dialect),
            CdcEligibilitySql.RenderEvidenceCommandText(dialect, includeDataStoreIdentity: true),
            CdcEligibilitySql.RenderEvidenceCommandText(dialect, includeDataStoreIdentity: false),
        ];

        using var _ = new AssertionScope();
        foreach (string statement in statements)
        {
            foreach (string mutationToken in MutationTokens)
            {
                statement
                    .Contains(mutationToken, StringComparison.OrdinalIgnoreCase)
                    .Should()
                    .BeFalse("the eligibility gate runs before binding creation and mutates nothing");
            }

            foreach (string lockToken in LockTokens)
            {
                statement
                    .Contains(lockToken, StringComparison.OrdinalIgnoreCase)
                    .Should()
                    .BeFalse("the eligibility gate must not take the administrative mutex or block a writer");
            }
        }
    }

    [Test]
    public void It_declares_the_postgresql_transaction_read_only()
    {
        CdcEligibilitySql
            .RenderReadOnlyTransactionCommandText(SqlDialect.Pgsql)
            .Should()
            .Be("SET TRANSACTION READ ONLY;");
    }

    /// <summary>
    /// SQL Server has no read-only transaction mode, so the guarantee there rests on the statements
    /// themselves, which the mutation assertion above covers.
    /// </summary>
    [Test]
    public void It_declares_no_read_only_transaction_mode_for_sql_server()
    {
        CdcEligibilitySql.RenderReadOnlyTransactionCommandText(SqlDialect.Mssql).Should().BeNull();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_reads_every_fact_in_one_statement(SqlDialect dialect)
    {
        string statement = CdcEligibilitySql.RenderEvidenceCommandText(
            dialect,
            includeDataStoreIdentity: true
        );

        using var _ = new AssertionScope();
        statement.Split(';', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
        statement.Should().Contain(CdcEligibilitySql.LifecycleStateColumnName);
        statement.Should().Contain(CdcEligibilitySql.CacheAheadRecoveryRequiredColumnName);
        statement.Should().Contain(CdcEligibilitySql.CanonicalRowsPresentColumnName);
        statement.Should().Contain(CdcEligibilitySql.CacheRowsPresentColumnName);
        statement.Should().Contain(CdcEligibilitySql.WorkRowsPresentColumnName);
        statement.Should().Contain(CdcEligibilitySql.SourceIdentityColumnName);
        statement.Should().Contain(CdcEligibilitySql.DurableObservedAtColumnName);
        statement.Should().Contain(CdcEligibilitySql.ProviderConsistencyTokenColumnName);
        statement.Should().Contain(DocumentCacheInventoryDefinition.Document.Name);
        statement.Should().Contain(DocumentCacheInventoryDefinition.DocumentCache.Name);
        statement.Should().Contain(DocumentCacheInventoryDefinition.DocumentProjectionWork.Name);
        statement.Should().Contain(DocumentCacheInventoryDefinition.DocumentCacheState.Name);
        statement.Should().Contain(DataStoreIdentityTableDefinition.Table.Name);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_omits_an_absent_identity_table_from_the_evidence_statement(SqlDialect dialect)
    {
        string statement = CdcEligibilitySql.RenderEvidenceCommandText(
            dialect,
            includeDataStoreIdentity: false
        );

        using var _ = new AssertionScope();
        statement
            .Should()
            .NotContain($"{DataStoreIdentityTableDefinition.Table.Name}\" WHERE")
            .And.NotContain($"{DataStoreIdentityTableDefinition.Table.Name}] WHERE");
        statement.Should().Contain(CdcEligibilitySql.SourceIdentityColumnName);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_probes_every_table_the_gate_may_read(SqlDialect dialect)
    {
        string statement = CdcEligibilitySql.RenderTableExistenceCommandText(dialect);

        using var _ = new AssertionScope();
        statement.Should().Contain($"'{DocumentCacheInventoryDefinition.Document.Name}'");
        statement.Should().Contain($"'{DocumentCacheInventoryDefinition.DocumentCache.Name}'");
        statement.Should().Contain($"'{DocumentCacheInventoryDefinition.DocumentProjectionWork.Name}'");
        statement.Should().Contain($"'{DocumentCacheInventoryDefinition.DocumentCacheState.Name}'");
        statement.Should().Contain($"'{DataStoreIdentityTableDefinition.Table.Name}'");
        statement.Should().Contain($"'{DocumentCacheInventoryDefinition.DmsSchema.Value}'");
    }

    [TestCaseSource(nameof(RequiredTableCases))]
    public void It_reports_a_missing_document_cache_table(string tableName)
    {
        HashSet<string> presentTables = new(AllTables(), StringComparer.Ordinal);
        presentTables.Remove(tableName);

        CdcEligibilitySql.MissingRequiredTable(presentTables).Should().Contain(tableName);
    }

    private static IEnumerable<TestCaseData> RequiredTableCases()
    {
        yield return new TestCaseData(DocumentCacheInventoryDefinition.DocumentCacheState.Name);
        yield return new TestCaseData(DocumentCacheInventoryDefinition.Document.Name);
        yield return new TestCaseData(DocumentCacheInventoryDefinition.DocumentCache.Name);
        yield return new TestCaseData(DocumentCacheInventoryDefinition.DocumentProjectionWork.Name);
    }

    /// <summary>
    /// An absent identity table leaves the physical source unidentified, which the observation reports;
    /// it does not make the database unreadable.
    /// </summary>
    [Test]
    public void It_does_not_require_the_data_store_identity_table()
    {
        HashSet<string> presentTables = new(AllTables(), StringComparer.Ordinal);
        presentTables.Remove(DataStoreIdentityTableDefinition.Table.Name);

        CdcEligibilitySql.MissingRequiredTable(presentTables).Should().BeNull();
    }

    [Test]
    public void It_reports_no_missing_table_for_a_provisioned_database()
    {
        CdcEligibilitySql
            .MissingRequiredTable(new HashSet<string>(AllTables(), StringComparer.Ordinal))
            .Should()
            .BeNull();
    }

    private static IEnumerable<string> AllTables() =>
        [
            DocumentCacheInventoryDefinition.DocumentCacheState.Name,
            DocumentCacheInventoryDefinition.Document.Name,
            DocumentCacheInventoryDefinition.DocumentCache.Name,
            DocumentCacheInventoryDefinition.DocumentProjectionWork.Name,
            DataStoreIdentityTableDefinition.Table.Name,
        ];
}
