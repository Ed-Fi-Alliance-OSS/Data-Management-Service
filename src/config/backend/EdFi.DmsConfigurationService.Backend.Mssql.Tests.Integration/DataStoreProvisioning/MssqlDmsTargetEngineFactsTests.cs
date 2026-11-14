// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The catalog facts (6.9c) read from the hand-built SQL Server DMS-shaped database: inventory parity with the
/// delivered PowerShell module's committed canonical text and hash, the compatibility level and physical
/// baseline, exactly the built-in security state, the full validator passing on the clean database, the
/// Populated predicate, and drift: a grant to <c>public</c> or <c>guest</c> turning into
/// <c>SecurityStateViolation</c> while identity, effective schema, and inventory hash stay unchanged; a copied
/// database user and an identity-state table turning into <c>ContentProfileViolation</c>; and a name the row
/// contract cannot carry refusing the read.
/// </summary>
[Category(Category)]
public class MssqlDmsTargetEngineFactsTests
{
    private static readonly string[] _projects = ["edfi"];

    private static async Task<(DmsTargetFacts Facts, DmsIdentityRead Identity)> ReadAsync(
        TestDatabase database
    )
    {
        MssqlDmsTargetEngine engine = Engine();
        DmsSessionOpenResult opened = await engine.OpenSessionAsync(
            database.DatabaseName,
            await database.CatalogInstanceAsync(),
            default
        );
        await using DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session;
        DmsTargetFacts facts = await engine.ReadTargetFactsAsync(session, default);
        DmsIdentityRead identity = await engine.ReadIdentityAsync(session, default);
        return (facts, identity);
    }

    private static DmsTargetValidation Validate(
        DmsTargetFacts facts,
        DmsIdentityRead identity,
        DmsTargetValidationRule rule,
        string templateKind = RestoreManifest.MinimalTemplateKind
    ) =>
        DmsTargetValidator.Validate(
            facts,
            identity,
            MssqlDmsShapedManifest.For(facts, templateKind, _projects),
            _projects,
            rule
        );

    [TestFixture]
    public class Given_the_dms_shaped_database
    {
        private TestDatabase? _database;
        private DmsTargetFacts _facts = null!;
        private DmsIdentityRead _identity = null!;
        private string _canonicalText = string.Empty;
        private string _hash = string.Empty;
        private int _productMajor;
        private int _compatibilityLevel;
        private DmsTargetValidation _staging = null!;
        private DmsTargetValidation _reconcileOwned = null!;
        private DmsTargetValidation _reconcileForeign = null!;
        private DmsTargetValidation _populatedBeforeDocument = null!;
        private DmsTargetFacts _factsWithDocument = null!;
        private DmsTargetValidation _populatedAfterDocument = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("facts");
            (_facts, _identity) = await ReadAsync(_database);
            DmsTargetValidator
                .TryBuildInventory(_facts, out CanonicalInventory? inventory, out string? defect)
                .Should()
                .BeTrue(defect);
            _canonicalText = CanonicalInventoryJson.Serialize(inventory!);
            _hash = CanonicalInventoryHash.Compute(inventory!);
            _productMajor = await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"
            );
            _compatibilityLevel = await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, compatibility_level) FROM sys.databases WHERE name = @name;",
                ("@name", _database.Name)
            );

            _staging = Validate(_facts, _identity, new DmsTargetValidationRule.Staging(Guid.NewGuid()));
            _reconcileOwned = Validate(
                _facts,
                _identity,
                new DmsTargetValidationRule.Reconcile(MssqlDmsShapedManifest.SourceIdentity)
            );
            _reconcileForeign = Validate(
                _facts,
                _identity,
                new DmsTargetValidationRule.Reconcile(Guid.NewGuid())
            );
            _populatedBeforeDocument = Validate(
                _facts,
                _identity,
                new DmsTargetValidationRule.Staging(Guid.NewGuid()),
                RestoreManifest.PopulatedTemplateKind
            );
            await _database.ExecuteAsync(
                "INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId]) VALUES ('2b3c4d5e-6f70-4a8b-9c0d-1e2f3a4b5c6e', 1);"
            );
            (_factsWithDocument, _) = await ReadAsync(_database);
            _populatedAfterDocument = Validate(
                _factsWithDocument,
                _identity,
                new DmsTargetValidationRule.Staging(Guid.NewGuid()),
                RestoreManifest.PopulatedTemplateKind
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reproduces_the_PowerShell_canonical_inventory_text() =>
            _canonicalText.Should().Be(FixtureText("dms-shaped-database.inventory.canonical.json"));

        [Test]
        public void It_reproduces_the_PowerShell_inventory_hash() =>
            _hash.Should().Be(FixtureText("dms-shaped-database.inventory.sha256"));

        [Test]
        public void It_keys_a_system_named_default_by_its_column() =>
            _facts
                .InventoryObjects.Should()
                .Contain(new DmsInventoryObject("dms", "DocumentCache.#system(ComputedAt)", "default"));

        [Test]
        public void It_reads_the_effective_schema_row() =>
            _facts
                .EffectiveSchema.Should()
                .Be(
                    new DmsEffectiveSchemaRead.Found(
                        MssqlDmsShapedManifest.ApiSchemaFormatVersion,
                        MssqlDmsShapedManifest.EffectiveSchemaHash,
                        MssqlDmsShapedManifest.ResourceKeyCount,
                        MssqlDmsShapedManifest.ResourceKeySeedHashB64
                    )
                );

        [Test]
        public void It_reads_the_live_engine_major() => _facts.EngineMajorVersion.Should().Be(_productMajor);

        [Test]
        public void It_reads_the_database_compatibility_level() =>
            _facts.CompatibilityLevel.Should().Be(_compatibilityLevel);

        [Test]
        public void It_lists_the_document_cache_json_column_with_its_declared_length() =>
            _facts
                .DocumentJsonColumns.Should()
                .Equal(new DmsDocumentJsonColumn("dms", "DocumentCache", "nvarchar(max)"));

        [Test]
        public void It_enumerates_the_schemas_including_dbo() =>
            _facts.SchemaNames.Should().Equal("dbo", "dms", "edfi", "tracked_changes_edfi");

        [Test]
        public void It_reports_no_principals_and_no_database_level_objects() =>
            (_facts.Principals.Count, _facts.DatabaseLevelObjects.Count).Should().Be((0, 0));

        [Test]
        public void It_reads_dbo_as_the_only_db_owner_member() =>
            ((DmsSecurityFacts.Mssql)_facts.Security)
                .RoleMembers.Should()
                .Equal(new DmsMssqlRoleMemberRow(16384, 1, "db_owner", "dbo"));

        [Test]
        public void It_reads_exactly_the_built_in_database_scope_grants() =>
            ((DmsSecurityFacts.Mssql)_facts.Security)
                .Permissions.Where(row => row.Class == 0)
                .Select(row => $"{row.PermissionName}|{row.StateDesc}|{row.GranteeId}|{row.Grantee}")
                .Should()
                .BeEquivalentTo(
                    "CONNECT|GRANT|1|dbo",
                    "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION|GRANT|0|public",
                    "VIEW ANY COLUMN MASTER KEY DEFINITION|GRANT|0|public"
                );

        [Test]
        public void It_reads_every_object_grant_as_public_select_on_a_system_object() =>
            ((DmsSecurityFacts.Mssql)_facts.Security)
                .Permissions.Where(row => row.Class != 0)
                .Should()
                .NotBeEmpty()
                .And.OnlyContain(row =>
                    row.Class == 1
                    && row.PermissionName == "SELECT"
                    && row.StateDesc == "GRANT"
                    && row.GranteeId == 0
                    && row.MajorId < 0
                    && row.MinorId == 0
                );

        [Test]
        public void It_reads_the_fixture_identity() =>
            _identity.Should().Be(new DmsIdentityRead.Found(MssqlDmsShapedManifest.SourceIdentity));

        [Test]
        public void It_sees_no_populated_document_before_one_exists() =>
            _facts.HasPopulatedDocument.Should().BeFalse();

        [Test]
        public void It_passes_the_full_staging_validation() =>
            _staging.Should().Be(new DmsTargetValidation.Valid());

        [Test]
        public void It_passes_reconciliation_for_the_owning_identity() =>
            _reconcileOwned.Should().Be(new DmsTargetValidation.Valid());

        [Test]
        public void It_reports_a_foreign_identity_as_unowned_on_reconciliation() =>
            _reconcileForeign
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsTargetValidator.IdentityCheck
                    )
                );

        [Test]
        public void It_fails_the_populated_predicate_without_a_document() =>
            _populatedBeforeDocument
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsTargetValidator.PopulatedPredicateCheck
                    )
                );

        [Test]
        public void It_sees_the_populated_document_once_inserted() =>
            _factsWithDocument.HasPopulatedDocument.Should().BeTrue();

        [Test]
        public void It_passes_the_populated_predicate_with_a_document() =>
            _populatedAfterDocument.Should().Be(new DmsTargetValidation.Valid());
    }

    /// <summary>
    /// F-01: a <c>dms.EffectiveSchema</c> whose row holds a NULL, whose column has another type, or which lacks a
    /// column is a typed invalid read, never a driver exception; the rest of the facts are still read, and a
    /// well-formed row is still found.
    /// </summary>
    [TestFixture]
    public class Given_effective_schema_tables_of_every_shape
    {
        private const string Table = "[dms].[EffectiveSchema]";

        private readonly List<TestDatabase> _databases = [];
        private DmsTargetFacts _wellFormed = null!;
        private DmsTargetFacts _nullHash = null!;
        private DmsTargetFacts _integerCount = null!;
        private DmsTargetFacts _textSeedHash = null!;
        private DmsTargetFacts _seedHashMissing = null!;

        private async Task<DmsTargetFacts> FactsAfterAsync(string purpose, params string[] alterations)
        {
            TestDatabase database = await CreateDmsShapedDatabaseAsync(purpose);
            _databases.Add(database);
            foreach (string alteration in alterations)
            {
                await database.ExecuteAsync(alteration);
            }

            return (await ReadAsync(database)).Facts;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _wellFormed = await FactsAfterAsync("esform");
            _nullHash = await FactsAfterAsync(
                "esnull",
                $"ALTER TABLE {Table} DROP CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash];",
                $"ALTER TABLE {Table} ALTER COLUMN [EffectiveSchemaHash] nvarchar(64) NULL;",
                $"UPDATE {Table} SET [EffectiveSchemaHash] = NULL;"
            );
            _integerCount = await FactsAfterAsync(
                "esint",
                $"ALTER TABLE {Table} ALTER COLUMN [ResourceKeyCount] int NOT NULL;"
            );
            _textSeedHash = await FactsAfterAsync(
                "estext",
                $"ALTER TABLE {Table} ADD [SeedHashText] nvarchar(64) NULL;",
                $"UPDATE {Table} SET [SeedHashText] = CONVERT(nvarchar(64), [ResourceKeySeedHash], 2);",
                $"ALTER TABLE {Table} DROP COLUMN [ResourceKeySeedHash];",
                "EXEC sp_rename N'[dms].[EffectiveSchema].[SeedHashText]', N'ResourceKeySeedHash', N'COLUMN';"
            );
            _seedHashMissing = await FactsAfterAsync(
                "esdrop",
                $"ALTER TABLE {Table} DROP COLUMN [ResourceKeySeedHash];"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (TestDatabase database in _databases)
            {
                await database.DisposeAsync();
            }
        }

        [Test]
        public void It_finds_the_well_formed_row() =>
            _wellFormed.EffectiveSchema.Should().BeOfType<DmsEffectiveSchemaRead.Found>();

        [Test]
        public void It_reports_a_null_value_as_invalid() =>
            _nullHash
                .EffectiveSchema.Should()
                .Be(new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.ValueInvalid));

        [Test]
        public void It_reports_a_count_of_another_type_as_invalid() =>
            _integerCount
                .EffectiveSchema.Should()
                .Be(new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.ValueInvalid));

        [Test]
        public void It_reports_a_seed_hash_of_another_type_as_invalid() =>
            _textSeedHash
                .EffectiveSchema.Should()
                .Be(new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.ValueInvalid));

        [Test]
        public void It_reports_a_missing_column_as_column_missing() =>
            _seedHashMissing
                .EffectiveSchema.Should()
                .Be(new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.ColumnMissing));

        [Test]
        public void It_still_reads_the_rest_of_the_facts() =>
            new[] { _nullHash, _integerCount, _textSeedHash, _seedHashMissing }
                .Should()
                .AllSatisfy(facts => facts.SchemaNames.Should().Equal(_wellFormed.SchemaNames));
    }

    /// <summary>
    /// F-01 (R2-1): the Populated predicate over <c>dms.Document</c> and <c>dms.ResourceKey</c> whose shape SQL Server
    /// refuses when compiling the query (<c>207</c>, <c>206</c>, <c>8116</c>) is reported as malformed predicate
    /// tables instead of a driver exception, and the validator refuses those facts under both template kinds, with an
    /// invalid effective schema still reported first. Missing tables keep the null predicate and are not malformed.
    /// Type mismatches SQL Server resolves by implicit conversion raise nothing and are outside this coverage.
    /// </summary>
    [TestFixture]
    public class Given_populated_predicate_tables_of_every_shape
    {
        private const string DropDocumentKey =
            "DROP INDEX [IX_Document_ResourceKeyId] ON [dms].[Document]; ALTER TABLE [dms].[Document] DROP CONSTRAINT [FK_Document_ResourceKey]; ALTER TABLE [dms].[Document] DROP COLUMN [ResourceKeyId];";

        private readonly List<TestDatabase> _databases = [];
        private DmsTargetFacts _wellFormed = null!;
        private DmsTargetFacts _keyColumnMissing = null!;
        private DmsTargetFacts _nameColumnMissing = null!;
        private DmsTargetFacts _uniqueidentifierKey = null!;
        private DmsTargetFacts _xmlName = null!;
        private DmsTargetFacts _tablesMissing = null!;
        private DmsTargetValidation _minimalNameMissing = null!;
        private DmsTargetValidation _populatedNameMissing = null!;
        private DmsTargetValidation _combined = null!;

        private async Task<(DmsTargetFacts Facts, DmsIdentityRead Identity)> ReadAfterAsync(
            string purpose,
            params string[] alterations
        )
        {
            TestDatabase database = await CreateDmsShapedDatabaseAsync(purpose);
            _databases.Add(database);
            foreach (string alteration in alterations)
            {
                await database.ExecuteAsync(alteration);
            }

            return await ReadAsync(database);
        }

        private DmsTargetValidation Owned(
            DmsTargetFacts facts,
            DmsIdentityRead identity,
            string templateKind
        ) =>
            DmsTargetValidator.Validate(
                facts,
                identity,
                MssqlDmsShapedManifest.For(_wellFormed, templateKind, _projects),
                _projects,
                new DmsTargetValidationRule.Reconcile(MssqlDmsShapedManifest.SourceIdentity)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            (_wellFormed, _) = await ReadAfterAsync("pdform");
            (_keyColumnMissing, _) = await ReadAfterAsync("pdkey", DropDocumentKey);
            (DmsTargetFacts nameMissing, DmsIdentityRead nameMissingIdentity) = await ReadAfterAsync(
                "pdname",
                "ALTER TABLE [dms].[ResourceKey] DROP COLUMN [ResourceName];"
            );
            _nameColumnMissing = nameMissing;
            (_uniqueidentifierKey, _) = await ReadAfterAsync(
                "pdguid",
                DropDocumentKey,
                "ALTER TABLE [dms].[Document] ADD [ResourceKeyId] uniqueidentifier NULL;"
            );
            (_xmlName, _) = await ReadAfterAsync(
                "pdxml",
                "ALTER TABLE [dms].[ResourceKey] ALTER COLUMN [ResourceName] xml NULL;"
            );
            (_tablesMissing, _) = await ReadAfterAsync(
                "pdnone",
                "EXEC sp_rename N'[dms].[Document]', N'DocumentRenamed';"
            );
            (DmsTargetFacts combined, DmsIdentityRead combinedIdentity) = await ReadAfterAsync(
                "pdboth",
                "ALTER TABLE [dms].[ResourceKey] DROP COLUMN [ResourceName];",
                "ALTER TABLE [dms].[EffectiveSchema] DROP CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash];",
                "ALTER TABLE [dms].[EffectiveSchema] ALTER COLUMN [EffectiveSchemaHash] nvarchar(64) NULL;",
                "UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = NULL;"
            );

            _minimalNameMissing = Owned(
                nameMissing,
                nameMissingIdentity,
                RestoreManifest.MinimalTemplateKind
            );
            _populatedNameMissing = Owned(
                nameMissing,
                nameMissingIdentity,
                RestoreManifest.PopulatedTemplateKind
            );
            _combined = Owned(combined, combinedIdentity, RestoreManifest.MinimalTemplateKind);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (TestDatabase database in _databases)
            {
                await database.DisposeAsync();
            }
        }

        [Test]
        public void It_reads_the_predicate_of_well_formed_tables() =>
            (_wellFormed.HasPopulatedDocument is not null, _wellFormed.PopulatedPredicateTablesMalformed)
                .Should()
                .Be((true, false));

        [Test]
        public void It_reports_a_missing_document_key_column_as_malformed() =>
            Predicate(_keyColumnMissing).Should().Be((null, true));

        [Test]
        public void It_reports_a_missing_resource_name_column_as_malformed() =>
            Predicate(_nameColumnMissing).Should().Be((null, true));

        [Test]
        public void It_reports_a_document_key_with_a_clashing_type_as_malformed() =>
            Predicate(_uniqueidentifierKey).Should().Be((null, true));

        [Test]
        public void It_reports_a_resource_name_like_cannot_take_as_malformed() =>
            Predicate(_xmlName).Should().Be((null, true));

        [Test]
        public void It_reports_missing_tables_as_unknown_but_not_malformed() =>
            Predicate(_tablesMissing).Should().Be((null, false));

        [Test]
        public void It_still_reads_the_rest_of_the_facts() =>
            new[] { _keyColumnMissing, _nameColumnMissing, _uniqueidentifierKey, _xmlName }
                .Should()
                .AllSatisfy(facts => facts.EffectiveSchema.Should().Be(_wellFormed.EffectiveSchema));

        [Test]
        public void It_refuses_an_owned_minimal_target_with_malformed_predicate_tables() =>
            _minimalNameMissing
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsTargetValidator.PopulatedPredicateCheck
                    )
                );

        [Test]
        public void It_refuses_an_owned_populated_target_with_malformed_predicate_tables() =>
            _populatedNameMissing
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsTargetValidator.PopulatedPredicateCheck
                    )
                );

        [Test]
        public void It_reports_an_invalid_effective_schema_before_malformed_predicate_tables() =>
            _combined
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsTargetValidator.EffectiveSchemaCheck
                    )
                );

        private static (bool? Has, bool Malformed) Predicate(DmsTargetFacts facts) =>
            (facts.HasPopulatedDocument, facts.PopulatedPredicateTablesMalformed);
    }

    /// <summary>
    /// A lost connection during the Populated predicate is never read as malformed predicate tables: the predicate
    /// query is held behind an exclusive table lock on <c>dms.Document</c>, its session is killed while it waits, and
    /// the facts read raises the connection failure.
    /// </summary>
    [TestFixture]
    public class Given_the_session_is_killed_during_the_populated_predicate
    {
        private TestDatabase? _database;
        private bool _predicateWaitedOnTheLock;
        private string _waitingStatement = "";
        private Exception? _factsRead;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("pdlost");
            await WaitForNoSessionsOnAsync(_database.Name);
            MssqlDmsTargetEngine engine = Engine();
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                _database.DatabaseName,
                await _database.CatalogInstanceAsync(),
                default
            );
            await using DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session;
            int sessionId = (int)session.ServerSession.Id;

            await using SqlConnection locker = await AdminConnection()
                .OpenAsync(_database.Name, "dms1438-test-locker", default);
            await using SqlTransaction transaction = (SqlTransaction)await locker.BeginTransactionAsync();
            await using (
                SqlCommand lockDocument = new(
                    "SELECT COUNT(*) FROM [dms].[Document] WITH (TABLOCKX, HOLDLOCK);",
                    locker,
                    transaction
                )
            )
            {
                await lockDocument.ExecuteScalarAsync();
            }

            Task<DmsTargetFacts> read = engine.ReadTargetFactsAsync(session, default);
            _predicateWaitedOnTheLock = await WaitUntilAsync(
                async () => (await WaitTypeOfAsync(sessionId)).StartsWith("LCK_M_", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30)
            );
            _waitingStatement = await ScalarOnServerAsync<string>(
                "SELECT ISNULL((SELECT t.[text] FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id = @id), N'');",
                ("@id", sessionId)
            );
            await KillAsync(sessionId);
            _factsRead = await Record.ExceptionAsync(() => read);
            await transaction.RollbackAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_held_the_predicate_query_on_the_lock()
        {
            _predicateWaitedOnTheLock.Should().BeTrue();
            _waitingStatement.Should().Contain(MssqlInventoryQueries.HasPopulatedDocument);
        }

        [Test]
        public void It_raises_the_connection_failure_from_the_facts_read() =>
            _factsRead
                .Should()
                .NotBeNull()
                .And.Match<Exception>(exception => MssqlErrorClassifier.IsConnectionFailure(exception));
    }

    /// <summary>
    /// A refusal for lack of a privilege during the Populated predicate stays an access refusal: a login that may read
    /// everything else the facts need, but is denied <c>SELECT</c> on <c>dms.Document</c>, has the facts read refused
    /// with the error number only, never reported as malformed predicate tables; without the denial it reads them.
    /// </summary>
    [TestFixture]
    public class Given_a_login_denied_the_predicate_tables
    {
        private TestDatabase? _database;
        private string? _login;
        private DmsTargetFacts _granted = null!;
        private Exception? _denied;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("pddeny");
            _login = await CreateLoginAsync("pddeny");
            string login = MssqlIdentifiers.Quote(_login);
            await ExecuteOnServerAsync(
                $"GRANT VIEW ANY DEFINITION TO {login}; GRANT VIEW SERVER STATE TO {login};"
            );
            await _database.ExecuteAsync(
                $"CREATE USER {login} FOR LOGIN {login}; GRANT SELECT ON SCHEMA::[dms] TO {login};"
            );
            MssqlDmsTargetEngine engine = EngineAs(_login);
            DmsInstanceIdentity instance = await _database.CatalogInstanceAsync();

            await using (
                DmsSession session = (
                    (DmsSessionOpenResult.Opened)
                        await engine.OpenSessionAsync(_database.DatabaseName, instance, default)
                ).Session
            )
            {
                _granted = await engine.ReadTargetFactsAsync(session, default);
            }

            await _database.ExecuteAsync($"DENY SELECT ON [dms].[Document] TO {login};");
            await using (
                DmsSession session = (
                    (DmsSessionOpenResult.Opened)
                        await engine.OpenSessionAsync(_database.DatabaseName, instance, default)
                ).Session
            )
            {
                _denied = await Record.ExceptionAsync(() => engine.ReadTargetFactsAsync(session, default));
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_reads_the_predicate_tables_while_they_are_granted() =>
            (_granted.HasPopulatedDocument is not null, _granted.PopulatedPredicateTablesMalformed)
                .Should()
                .Be((true, false));

        [Test]
        public void It_reports_the_denied_predicate_as_an_access_refusal() =>
            _denied
                .Should()
                .BeOfType<DmsAccessDeniedException>()
                .Which.Diagnostic.ProviderErrorCode.Should()
                .Be("229");
    }

    [TestFixture]
    public class Given_permission_drift_on_the_dms_shaped_database
    {
        private static string HashOf(DmsTargetFacts facts) =>
            DmsTargetValidator.TryBuildInventory(facts, out CanonicalInventory? inventory, out string? defect)
                ? CanonicalInventoryHash.Compute(inventory)
                : throw new InvalidOperationException(defect);

        private TestDatabase? _database;
        private string _committedHash = string.Empty;
        private DmsTargetFacts _schemaGrant = null!;
        private DmsIdentityRead _schemaGrantIdentity = null!;
        private DmsTargetValidation _schemaGrantVerdict = null!;
        private DmsTargetFacts _guestConnect = null!;
        private DmsTargetValidation _guestConnectVerdict = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("drift");
            _committedHash = FixtureText("dms-shaped-database.inventory.sha256");

            await _database.ExecuteAsync("GRANT SELECT ON SCHEMA::[edfi] TO [public];");
            (_schemaGrant, _schemaGrantIdentity) = await ReadAsync(_database);
            _schemaGrantVerdict = Validate(
                _schemaGrant,
                _schemaGrantIdentity,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );

            await _database.ExecuteAsync(
                "REVOKE SELECT ON SCHEMA::[edfi] FROM [public]; GRANT CONNECT TO [guest];"
            );
            (_guestConnect, DmsIdentityRead guestIdentity) = await ReadAsync(_database);
            _guestConnectVerdict = Validate(
                _guestConnect,
                guestIdentity,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_schema_grant_to_public_as_a_security_state_violation() =>
            _schemaGrantVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsTargetValidator.SecurityStateCheck
                    )
                );

        [Test]
        public void It_sees_the_schema_grant_in_the_permission_rows() =>
            ((DmsSecurityFacts.Mssql)_schemaGrant.Security)
                .Permissions.Should()
                .ContainSingle(row => row.Class == 3)
                .Which.Should()
                .Match<DmsMssqlPermissionRow>(row =>
                    row.PermissionName == "SELECT" && row.GranteeId == 0 && row.Securable == "SCHEMA::edfi"
                );

        [Test]
        public void It_leaves_the_identity_effective_schema_and_inventory_hash_unchanged_by_a_grant() =>
            (
                _schemaGrantIdentity,
                _schemaGrant.EffectiveSchema is DmsEffectiveSchemaRead.Found,
                HashOf(_schemaGrant)
            )
                .Should()
                .Be((new DmsIdentityRead.Found(MssqlDmsShapedManifest.SourceIdentity), true, _committedHash));

        [Test]
        public void It_reports_connect_granted_to_guest_as_a_security_state_violation() =>
            _guestConnectVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsTargetValidator.SecurityStateCheck
                    )
                );

        [Test]
        public void It_sees_the_guest_grant_without_a_principal_or_inventory_change() =>
            (
                ((DmsSecurityFacts.Mssql)_guestConnect.Security).Permissions.Count(row =>
                    row.Class == 0 && row.PermissionName == "CONNECT" && row.GranteeId == 2
                ),
                _guestConnect.Principals.Count,
                HashOf(_guestConnect)
            )
                .Should()
                .Be((1, 0, _committedHash));
    }

    [TestFixture]
    public class Given_content_the_dms_only_profile_refuses
    {
        private TestDatabase? _userDatabase;
        private TestDatabase? _identityStateDatabase;
        private TestDatabase? _unsplittableDatabase;
        private DmsTargetFacts _withUser = null!;
        private DmsTargetValidation _userVerdict = null!;
        private DmsTargetValidation _identityStateVerdict = null!;
        private Exception? _unsplittable;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _userDatabase = await CreateDmsShapedDatabaseAsync("copieduser");
            await _userDatabase.ExecuteAsync("CREATE USER [reporting] WITHOUT LOGIN;");
            (_withUser, DmsIdentityRead userIdentity) = await ReadAsync(_userDatabase);
            _userVerdict = Validate(
                _withUser,
                userIdentity,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );

            _identityStateDatabase = await CreateDmsShapedDatabaseAsync("identitystate");
            await _identityStateDatabase.ExecuteAsync(
                "CREATE TABLE [dms].[OpenIddictTokens] ([Id] bigint NOT NULL, CONSTRAINT [PK_OpenIddictTokens] PRIMARY KEY ([Id]));"
            );
            (DmsTargetFacts identityStateFacts, DmsIdentityRead identityStateIdentity) = await ReadAsync(
                _identityStateDatabase
            );
            _identityStateVerdict = Validate(
                identityStateFacts,
                identityStateIdentity,
                new DmsTargetValidationRule.Reconcile(MssqlDmsShapedManifest.SourceIdentity)
            );

            _unsplittableDatabase = await CreateDmsShapedDatabaseAsync("unsplittable");
            await _unsplittableDatabase.ExecuteAsync("CREATE TABLE [edfi].[a|b] ([Id] int NOT NULL);");
            try
            {
                await ReadAsync(_unsplittableDatabase);
            }
            catch (Exception exception)
            {
                _unsplittable = exception;
            }
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (
                TestDatabase? database in new[]
                {
                    _userDatabase,
                    _identityStateDatabase,
                    _unsplittableDatabase,
                }
            )
            {
                if (database is not null)
                {
                    await database.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_reads_a_copied_database_user_as_a_principal() =>
            _withUser.Principals.Should().Equal("reporting");

        [Test]
        public void It_reports_a_copied_database_user_as_a_content_profile_violation() =>
            _userVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.ContentProfileViolation,
                        DmsTargetValidator.ContentProfileCheck
                    )
                );

        [Test]
        public void It_reports_an_identity_state_table_as_a_content_profile_violation_on_reconciliation() =>
            _identityStateVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.ContentProfileViolation,
                        DmsTargetValidator.ContentProfileCheck
                    )
                );

        [Test]
        public void It_refuses_a_name_the_row_contract_cannot_carry_as_a_content_profile_violation() =>
            _unsplittable
                .Should()
                .BeOfType<DmsProvisioningFailedException>()
                .Which.Failure.Should()
                .Match<DmsProvisioningFailure>(failure =>
                    failure.Code == DmsProvisioningFailureCode.ContentProfileViolation
                    && failure.Diagnostic.Operation == MssqlDmsTargetEngine.CatalogRowShapeStep
                );
    }
}
