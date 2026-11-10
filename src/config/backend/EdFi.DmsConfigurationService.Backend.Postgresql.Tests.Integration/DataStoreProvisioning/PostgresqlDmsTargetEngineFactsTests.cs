// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The catalog facts (6.9c) read from the hand-built DMS-shaped database: inventory parity with the
/// delivered PowerShell module's committed canonical text and hash, the ownership and privilege rows the
/// delivered queries return, the full validator passing on the clean database, the Populated predicate,
/// and permission drift (an extra grant, an owner change) turning into <c>SecurityStateViolation</c> while
/// identity, effective schema, and inventory hash stay unchanged.
/// </summary>
[Category(Category)]
public class PostgresqlDmsTargetEngineFactsTests
{
    private static readonly string[] _projects = ["edfi"];

    private static async Task<(DmsTargetFacts Facts, DmsIdentityRead Identity)> ReadAsync(
        TestDatabase database
    )
    {
        PostgresqlDmsTargetEngine engine = Engine();
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

    [TestFixture]
    public class Given_the_dms_shaped_database
    {
        private TestDatabase? _database;
        private DmsTargetFacts _facts = null!;
        private DmsIdentityRead _identity = null!;
        private string _canonicalText = string.Empty;
        private string _hash = string.Empty;
        private string _committedText = string.Empty;
        private string _committedHash = string.Empty;
        private int _serverMajor;
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
            _committedText = FixtureText("dms-shaped-database.inventory.canonical.json");
            _committedHash = FixtureText("dms-shaped-database.inventory.sha256");
            _serverMajor = await ScalarOnServerAsync<int>(
                "SELECT current_setting('server_version_num')::int / 10000;"
            );

            RestoreManifest minimal = DmsShapedManifest.For(
                _facts,
                RestoreManifest.MinimalTemplateKind,
                _projects
            );
            _staging = DmsTargetValidator.Validate(
                _facts,
                _identity,
                minimal,
                _projects,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );
            _reconcileOwned = DmsTargetValidator.Validate(
                _facts,
                _identity,
                minimal,
                _projects,
                new DmsTargetValidationRule.Reconcile(DmsShapedManifest.SourceIdentity)
            );
            _reconcileForeign = DmsTargetValidator.Validate(
                _facts,
                _identity,
                minimal,
                _projects,
                new DmsTargetValidationRule.Reconcile(Guid.NewGuid())
            );

            RestoreManifest populated = DmsShapedManifest.For(
                _facts,
                RestoreManifest.PopulatedTemplateKind,
                _projects
            );
            _populatedBeforeDocument = DmsTargetValidator.Validate(
                _facts,
                _identity,
                populated,
                _projects,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );
            await _database.ExecuteAsync(
                "INSERT INTO \"dms\".\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") VALUES ('2b3c4d5e-6f70-4a8b-9c0d-1e2f3a4b5c6e', 1);"
            );
            (_factsWithDocument, _) = await ReadAsync(_database);
            _populatedAfterDocument = DmsTargetValidator.Validate(
                _factsWithDocument,
                _identity,
                populated,
                _projects,
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
        public void It_reproduces_the_PowerShell_canonical_inventory_text() =>
            _canonicalText.Should().Be(_committedText);

        [Test]
        public void It_reproduces_the_PowerShell_inventory_hash() => _hash.Should().Be(_committedHash);

        [Test]
        public void It_reads_the_effective_schema_row() =>
            _facts
                .EffectiveSchema.Should()
                .Be(
                    new DmsEffectiveSchemaRead.Found(
                        DmsShapedManifest.ApiSchemaFormatVersion,
                        DmsShapedManifest.EffectiveSchemaHash,
                        DmsShapedManifest.ResourceKeyCount,
                        DmsShapedManifest.ResourceKeySeedHashB64
                    )
                );

        [Test]
        public void It_reads_the_live_engine_major() => _facts.EngineMajorVersion.Should().Be(_serverMajor);

        [Test]
        public void It_reports_no_compatibility_level() => _facts.CompatibilityLevel.Should().BeNull();

        [Test]
        public void It_lists_the_document_cache_json_column() =>
            _facts
                .DocumentJsonColumns.Should()
                .Equal(new DmsDocumentJsonColumn("dms", "DocumentCache", "jsonb"));

        [Test]
        public void It_enumerates_the_schemas_including_public() =>
            _facts.SchemaNames.Should().Equal("dms", "edfi", "public", "tracked_changes_edfi");

        [Test]
        public void It_reports_no_principals_and_no_database_level_objects() =>
            (_facts.Principals.Count, _facts.DatabaseLevelObjects.Count).Should().Be((0, 0));

        [Test]
        public void It_reports_exactly_the_ownership_the_DDL_produces() =>
            _facts
                .Security.Should()
                .BeOfType<DmsSecurityFacts.Postgresql>()
                .Which.Ownership.Should()
                .BeEquivalentTo([
                    new DmsPostgresqlOwnershipRow(
                        "function",
                        DmsTargetValidator.EnqueueOwnerRoleName,
                        "dms.\"TF_Document_EnqueueProjectionInsert\"()"
                    ),
                    new DmsPostgresqlOwnershipRow("schema", "pg_database_owner", "public"),
                ]);

        [Test]
        public void It_reports_exactly_the_enqueue_owner_grants() =>
            ((DmsSecurityFacts.Postgresql)_facts.Security)
                .Privileges.Where(row => row.Grantee == DmsTargetValidator.EnqueueOwnerRoleName)
                .Select(row => $"{row.Kind}|{row.Identity}|{row.Privilege}")
                .Should()
                .BeEquivalentTo(
                    "schema|dms|USAGE",
                    "relation|dms.\"DocumentProjectionWork\"|SELECT",
                    "relation|dms.\"DocumentProjectionWork\"|INSERT",
                    "relation|dms.\"DocumentProjectionWork\"|UPDATE",
                    "relation|dms.\"DocumentCacheState\"|SELECT",
                    "function|dms.\"TF_Document_EnqueueProjectionInsert\"()|EXECUTE"
                );

        [Test]
        public void It_keeps_public_execute_off_the_enqueue_owned_function() =>
            ((DmsSecurityFacts.Postgresql)_facts.Security)
                .Privileges.Should()
                .NotContain(row =>
                    row.Grantee == "-" && row.Identity == "dms.\"TF_Document_EnqueueProjectionInsert\"()"
                );

        [Test]
        public void It_reads_the_fixture_identity() =>
            _identity.Should().Be(new DmsIdentityRead.Found(DmsShapedManifest.SourceIdentity));

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
        private const string Table = "\"dms\".\"EffectiveSchema\"";

        private readonly List<TestDatabase> _databases = [];
        private DmsTargetFacts _wellFormed = null!;
        private DmsTargetFacts _nullHash = null!;
        private DmsTargetFacts _integerCount = null!;
        private DmsTargetFacts _textSeedHash = null!;
        private DmsTargetFacts _seedHashMissing = null!;

        private async Task<DmsTargetFacts> FactsAfterAsync(string purpose, string? alteration)
        {
            TestDatabase database = await CreateDmsShapedDatabaseAsync(purpose);
            _databases.Add(database);
            if (alteration is not null)
            {
                await database.ExecuteAsync(alteration);
            }

            return (await ReadAsync(database)).Facts;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _wellFormed = await FactsAfterAsync("esform", null);
            _nullHash = await FactsAfterAsync(
                "esnull",
                $"ALTER TABLE {Table} ALTER COLUMN \"EffectiveSchemaHash\" DROP NOT NULL; UPDATE {Table} SET \"EffectiveSchemaHash\" = NULL;"
            );
            _integerCount = await FactsAfterAsync(
                "esint",
                $"ALTER TABLE {Table} ALTER COLUMN \"ResourceKeyCount\" TYPE integer;"
            );
            _textSeedHash = await FactsAfterAsync(
                "estext",
                $"ALTER TABLE {Table} DROP CONSTRAINT \"CK_EffectiveSchema_ResourceKeySeedHash_Length\", ALTER COLUMN \"ResourceKeySeedHash\" TYPE text USING encode(\"ResourceKeySeedHash\", 'hex');"
            );
            _seedHashMissing = await FactsAfterAsync(
                "esdrop",
                $"ALTER TABLE {Table} DROP COLUMN \"ResourceKeySeedHash\";"
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

    [TestFixture]
    public class Given_an_identity_state_object_inside_the_dms_schema
    {
        private TestDatabase? _database;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("identitystate");
            await _database.ExecuteAsync(
                "CREATE TABLE \"dms\".\"OpenIddictTokens\" (\"Id\" bigint NOT NULL, CONSTRAINT \"PK_OpenIddictTokens\" PRIMARY KEY (\"Id\"));"
            );
            (DmsTargetFacts facts, DmsIdentityRead identity) = await ReadAsync(_database);
            RestoreManifest describingTheContamination = DmsShapedManifest.For(
                facts,
                RestoreManifest.MinimalTemplateKind,
                _projects
            );
            _hashMatches = string.Equals(
                CanonicalInventoryHash.Compute(describingTheContamination.Inventory),
                describingTheContamination.InventorySha256,
                StringComparison.Ordinal
            );
            _staging = DmsTargetValidator.Validate(
                facts,
                identity,
                describingTheContamination,
                _projects,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );
            _reconcile = DmsTargetValidator.Validate(
                facts,
                identity,
                describingTheContamination,
                _projects,
                new DmsTargetValidationRule.Reconcile(DmsShapedManifest.SourceIdentity)
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

        private bool _hashMatches;
        private DmsTargetValidation _staging = null!;
        private DmsTargetValidation _reconcile = null!;

        [Test]
        public void It_is_a_manifest_that_matches_the_contaminated_inventory() =>
            _hashMatches.Should().BeTrue();

        [Test]
        public void It_reports_the_content_profile_on_staging() =>
            _staging
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.ContentProfileViolation,
                        DmsTargetValidator.ContentProfileCheck
                    )
                );

        [Test]
        public void It_reports_the_content_profile_on_reconciliation() =>
            _reconcile
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.ContentProfileViolation,
                        DmsTargetValidator.ContentProfileCheck
                    )
                );
    }

    [TestFixture]
    public class Given_permission_drift_on_the_dms_shaped_database
    {
        private TestDatabase? _database;
        private string? _role;
        private string _committedHash = string.Empty;
        private DmsTargetFacts _granted = null!;
        private DmsIdentityRead _grantedIdentity = null!;
        private DmsTargetValidation _grantVerdict = null!;
        private DmsTargetFacts _reowned = null!;
        private DmsTargetValidation _ownerVerdict = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("drift");
            _role = await CreateRoleAsync("drift");
            _committedHash = FixtureText("dms-shaped-database.inventory.sha256");
            string role = PostgresqlIdentifiers.Quote(_role);

            await _database.ExecuteAsync($"GRANT SELECT ON \"dms\".\"Document\" TO {role};");
            (_granted, _grantedIdentity) = await ReadAsync(_database);
            _grantVerdict = Validate(_granted, _grantedIdentity);

            // The grant on dms.Document stays in place, so the owner change of edfi.School is read with both
            // violations present and the verdict proves ownership is judged first.
            await _database.ExecuteAsync($"ALTER TABLE \"edfi\".\"School\" OWNER TO {role};");
            (_reowned, DmsIdentityRead reownedIdentity) = await ReadAsync(_database);
            _ownerVerdict = Validate(_reowned, reownedIdentity);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_role is not null)
            {
                await DropRoleAsync(_role);
            }
        }

        [Test]
        public void It_reports_an_extra_grant_as_a_security_state_violation() =>
            _grantVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsTargetValidator.PrivilegesCheck
                    )
                );

        [Test]
        public void It_sees_the_extra_grant_in_the_privilege_rows() =>
            ((DmsSecurityFacts.Postgresql)_granted.Security)
                .Privileges.Should()
                .ContainSingle(row => row.Grantee == _role && row.Identity == "dms.\"Document\"")
                .Which.Privilege.Should()
                .Be("SELECT");

        [Test]
        public void It_leaves_the_identity_unchanged_by_a_grant() =>
            _grantedIdentity.Should().Be(new DmsIdentityRead.Found(DmsShapedManifest.SourceIdentity));

        [Test]
        public void It_leaves_the_effective_schema_unchanged_by_a_grant() =>
            _granted
                .EffectiveSchema.Should()
                .BeOfType<DmsEffectiveSchemaRead.Found>()
                .Which.EffectiveSchemaHash.Should()
                .Be(DmsShapedManifest.EffectiveSchemaHash);

        [Test]
        public void It_leaves_the_inventory_hash_unchanged_by_a_grant() =>
            HashOf(_granted).Should().Be(_committedHash);

        [Test]
        public void It_reports_an_owner_change_as_an_ownership_violation() =>
            _ownerVerdict
                .Should()
                .Be(
                    new DmsTargetValidation.Invalid(
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsTargetValidator.OwnershipCheck
                    )
                );

        [Test]
        public void It_sees_the_foreign_owner_in_the_ownership_rows() =>
            ((DmsSecurityFacts.Postgresql)_reowned.Security)
                .Ownership.Should()
                .Contain(new DmsPostgresqlOwnershipRow("relation", _role!, "edfi.\"School\""));

        [Test]
        public void It_leaves_the_inventory_hash_unchanged_by_an_owner_change() =>
            HashOf(_reowned).Should().Be(_committedHash);

        private static DmsTargetValidation Validate(DmsTargetFacts facts, DmsIdentityRead identity) =>
            DmsTargetValidator.Validate(
                facts,
                identity,
                DmsShapedManifest.For(facts, RestoreManifest.MinimalTemplateKind, _projects),
                _projects,
                new DmsTargetValidationRule.Staging(Guid.NewGuid())
            );

        private static string HashOf(DmsTargetFacts facts) =>
            DmsTargetValidator.TryBuildInventory(facts, out CanonicalInventory? inventory, out string? defect)
                ? CanonicalInventoryHash.Compute(inventory)
                : throw new InvalidOperationException(defect);
    }
}
