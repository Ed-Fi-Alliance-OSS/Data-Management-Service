// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsProvisioningFailureCode;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsTargetValidator;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.TargetFactsSample;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The 6.9c validation set over fakes: every check in order, the identity rule per stage, the F-P9 content
/// profile port, the F-P9b ownership and privilege allow-rules, the Populated predicate, and the
/// first-failing-check verdict.
/// </summary>
public class DmsTargetValidatorTests
{
    private static readonly Guid _expected = Guid.Parse("2c7e9d10-8f4a-4b6c-9e1d-3a5b7c9d1e2f");

    private static readonly DmsTargetValidation _valid = new DmsTargetValidation.Valid();

    private static DmsTargetValidation.Invalid Invalid(DmsProvisioningFailureCode code, string check) =>
        new(code, check);

    private static DmsTargetValidation Staging(
        DmsTargetFacts facts,
        RestoreManifest? manifest = null,
        DmsIdentityRead? identity = null,
        string[]? expectedProjects = null
    ) =>
        Validate(
            facts,
            identity ?? Identity(),
            manifest ?? ManifestFor(PostgresqlFacts()),
            expectedProjects ?? Projects,
            new DmsTargetValidationRule.Staging(_expected)
        );

    [TestFixture]
    public class Given_a_clean_database_under_each_rule
    {
        private DmsTargetValidation _staging = null!;
        private DmsTargetValidation _reconcile = null!;
        private DmsTargetValidation _afterRename = null!;
        private Exception? _emptyStaging;
        private Exception? _emptyAfterRename;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            RestoreManifest manifest = ManifestFor(facts);
            _staging = Validate(
                facts,
                Identity(),
                manifest,
                Projects,
                new DmsTargetValidationRule.Staging(_expected)
            );
            _reconcile = Validate(
                facts,
                Identity(),
                manifest,
                Projects,
                new DmsTargetValidationRule.Reconcile(PackageIdentity)
            );
            _afterRename = Validate(
                facts,
                Identity(),
                manifest,
                Projects,
                new DmsTargetValidationRule.AfterRename(PackageIdentity)
            );
            _emptyStaging = Record.Exception(() => new DmsTargetValidationRule.Staging(Guid.Empty));
            _emptyAfterRename = Record.Exception(() => new DmsTargetValidationRule.AfterRename(Guid.Empty));
        }

        [Test]
        public void It_passes_staging() => _staging.Should().Be(_valid);

        [Test]
        public void It_passes_reconciliation_with_the_expected_identity() => _reconcile.Should().Be(_valid);

        [Test]
        public void It_passes_the_post_rename_subset() => _afterRename.Should().Be(_valid);

        [Test]
        public void It_refuses_an_empty_expected_identity() =>
            _emptyStaging.Should().BeOfType<ArgumentException>();

        [Test]
        public void It_refuses_an_empty_package_identity() =>
            _emptyAfterRename.Should().BeOfType<ArgumentException>();
    }

    [TestFixture]
    public class Given_the_identity_rules
    {
        private DmsTargetValidation _stagingEqualsExpected = null!;
        private DmsTargetValidation _stagingInvalid = null!;
        private DmsTargetValidation _reconcileOther = null!;
        private DmsTargetValidation _reconcileInvalid = null!;
        private DmsTargetValidation _afterRenameOther = null!;
        private DmsTargetValidation _afterRenameIgnoresPrivileges = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            RestoreManifest manifest = ManifestFor(facts);
            DmsIdentityRead invalid = new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows);
            _stagingEqualsExpected = Validate(
                facts,
                new DmsIdentityRead.Found(_expected),
                manifest,
                Projects,
                new DmsTargetValidationRule.Staging(_expected)
            );
            _stagingInvalid = Validate(
                facts,
                invalid,
                manifest,
                Projects,
                new DmsTargetValidationRule.Staging(_expected)
            );
            _reconcileOther = Validate(
                facts,
                Identity(),
                manifest,
                Projects,
                new DmsTargetValidationRule.Reconcile(_expected)
            );
            _reconcileInvalid = Validate(
                facts,
                invalid,
                manifest,
                Projects,
                new DmsTargetValidationRule.Reconcile(_expected)
            );
            _afterRenameOther = Validate(
                facts,
                new DmsIdentityRead.Found(_expected),
                manifest,
                Projects,
                new DmsTargetValidationRule.AfterRename(PackageIdentity)
            );
            _afterRenameIgnoresPrivileges = Validate(
                facts.WithPrivileges(Held("relation", "SELECT", Admin, "intruder", "edfi.\"School\"")),
                Identity(),
                manifest,
                Projects,
                new DmsTargetValidationRule.AfterRename(PackageIdentity)
            );
        }

        [Test]
        public void It_rejects_a_staging_identity_equal_to_the_expected_one() =>
            _stagingEqualsExpected.Should().Be(Invalid(PackageIdentityInvalid, IdentityCheck));

        [Test]
        public void It_rejects_an_unreadable_staging_identity() =>
            _stagingInvalid.Should().Be(Invalid(PackageIdentityInvalid, IdentityCheck));

        [Test]
        public void It_reports_a_different_identity_on_reconciliation_as_unowned() =>
            _reconcileOther.Should().Be(Invalid(TargetUnowned, IdentityCheck));

        [Test]
        public void It_reports_an_unreadable_identity_on_reconciliation_as_unverifiable() =>
            _reconcileInvalid.Should().Be(Invalid(TargetUnverifiable, IdentityCheck));

        [Test]
        public void It_reports_a_changed_identity_after_rename_as_instance_changed() =>
            _afterRenameOther.Should().Be(Invalid(InstanceChanged, IdentityCheck));

        [Test]
        public void It_checks_only_items_1_and_2_after_rename() =>
            _afterRenameIgnoresPrivileges.Should().Be(_valid);
    }

    [TestFixture]
    public class Given_effective_schema_drift
    {
        private Dictionary<string, DmsTargetValidation> _verdicts = [];
        private DmsTargetValidation _unreadable = null!;
        private DmsTargetValidation _beforeIdentity = null!;

        [SetUp]
        public void Setup()
        {
            DmsEffectiveSchemaRead.Found row = EffectiveSchema();
            Dictionary<string, DmsEffectiveSchemaRead.Found> drifts = new()
            {
                ["format"] = row with { ApiSchemaFormatVersion = "2.0.0" },
                ["hash"] = row with { EffectiveSchemaHash = string.Concat(Enumerable.Repeat("ac", 32)) },
                ["count"] = row with { ResourceKeyCount = 43 },
                ["seed"] = row with { ResourceKeySeedHashB64 = Convert.ToBase64String(new byte[32]) },
            };
            _verdicts = drifts.ToDictionary(
                drift => drift.Key,
                drift => Staging(PostgresqlFacts() with { EffectiveSchema = drift.Value })
            );
            _unreadable = Staging(
                PostgresqlFacts() with
                {
                    EffectiveSchema = new DmsEffectiveSchemaRead.Invalid(
                        DmsEffectiveSchemaReadProblem.TableMissing
                    ),
                }
            );
            _beforeIdentity = Staging(
                PostgresqlFacts() with
                {
                    EffectiveSchema = drifts["hash"],
                },
                identity: new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows)
            );
        }

        [Test]
        public void It_rejects_a_format_version_drift() =>
            _verdicts["format"].Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));

        [Test]
        public void It_rejects_a_hash_drift() =>
            _verdicts["hash"].Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));

        [Test]
        public void It_rejects_a_resource_key_count_drift() =>
            _verdicts["count"].Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));

        [Test]
        public void It_rejects_a_seed_hash_drift() =>
            _verdicts["seed"].Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));

        [Test]
        public void It_rejects_an_unreadable_row() =>
            _unreadable.Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));

        [Test]
        public void It_checks_the_effective_schema_before_the_identity() =>
            _beforeIdentity.Should().Be(Invalid(TargetIncompatible, EffectiveSchemaCheck));
    }

    [TestFixture]
    public class Given_engine_and_baseline_drift
    {
        private DmsTargetValidation _olderServer = null!;
        private DmsTargetValidation _newerServer = null!;
        private DmsTargetValidation _unparsableManifestVersion = null!;
        private DmsTargetValidation _compatibilityLevelMissing = null!;
        private DmsTargetValidation _compatibilityLevelDifferent = null!;
        private DmsTargetValidation _noJsonColumns = null!;
        private DmsTargetValidation _noDocumentCache = null!;
        private DmsTargetValidation _divergentTypes = null!;
        private DmsTargetValidation _typeDiffersFromManifest = null!;
        private DmsTargetValidation _manifestAndDatabaseOffBaseline = null!;
        private DmsTargetValidation _mixedCaseType = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            _olderServer = Staging(facts with { EngineMajorVersion = 17 });
            _newerServer = Staging(facts with { EngineMajorVersion = 19 });
            _unparsableManifestVersion = Staging(facts, ManifestFor(facts, engineVersion: "latest"));

            DmsTargetFacts mssqlFacts = facts with
            {
                DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "DocumentCache", "nvarchar(max)")],
            };
            RestoreManifest mssqlManifest = ManifestFor(
                mssqlFacts,
                databaseEngine: RestoreManifest.MssqlEngine,
                engineVersion: "17.0.900.7",
                databaseCompatibilityLevel: 170
            );
            _compatibilityLevelMissing = Staging(mssqlFacts, mssqlManifest);
            _compatibilityLevelDifferent = Staging(
                mssqlFacts with
                {
                    CompatibilityLevel = 160,
                },
                mssqlManifest
            );

            _noJsonColumns = Staging(facts with { DocumentJsonColumns = [] });
            _noDocumentCache = Staging(
                facts with
                {
                    DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "Document", "jsonb")],
                }
            );
            _divergentTypes = Staging(
                facts with
                {
                    DocumentJsonColumns =
                    [
                        new DmsDocumentJsonColumn("dms", "DocumentCache", "jsonb"),
                        new DmsDocumentJsonColumn("dms", "Document", "json"),
                    ],
                }
            );
            DmsTargetFacts jsonFacts = facts with
            {
                DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "DocumentCache", "json")],
            };
            _typeDiffersFromManifest = Staging(jsonFacts);
            _manifestAndDatabaseOffBaseline = Staging(
                jsonFacts,
                ManifestFor(jsonFacts, documentJsonColumnType: "json")
            );
            _mixedCaseType = Staging(
                facts with
                {
                    DocumentJsonColumns = [new DmsDocumentJsonColumn("dms", "DocumentCache", " JSONB ")],
                }
            );
        }

        [Test]
        public void It_rejects_a_server_older_than_the_manifest_engine() =>
            _olderServer.Should().Be(Invalid(TargetIncompatible, EngineMajorCheck));

        [Test]
        public void It_accepts_a_server_newer_than_the_manifest_engine() => _newerServer.Should().Be(_valid);

        [Test]
        public void It_rejects_a_manifest_engine_version_without_a_leading_major() =>
            _unparsableManifestVersion.Should().Be(Invalid(TargetIncompatible, EngineMajorCheck));

        [Test]
        public void It_rejects_a_missing_compatibility_level_the_manifest_declares() =>
            _compatibilityLevelMissing.Should().Be(Invalid(TargetIncompatible, CompatibilityLevelCheck));

        [Test]
        public void It_rejects_a_different_compatibility_level() =>
            _compatibilityLevelDifferent.Should().Be(Invalid(TargetIncompatible, CompatibilityLevelCheck));

        [Test]
        public void It_rejects_a_database_without_json_columns() =>
            _noJsonColumns.Should().Be(Invalid(TargetIncompatible, DocumentJsonColumnTypeCheck));

        [Test]
        public void It_rejects_a_database_without_the_document_cache_carrier() =>
            _noDocumentCache.Should().Be(Invalid(TargetIncompatible, DocumentJsonColumnTypeCheck));

        [Test]
        public void It_rejects_carriers_of_different_types() =>
            _divergentTypes.Should().Be(Invalid(TargetIncompatible, DocumentJsonColumnTypeCheck));

        [Test]
        public void It_rejects_a_type_that_differs_from_the_manifest() =>
            _typeDiffersFromManifest.Should().Be(Invalid(TargetIncompatible, DocumentJsonColumnTypeCheck));

        [Test]
        public void It_rejects_a_type_off_the_engine_baseline_even_when_the_manifest_agrees() =>
            _manifestAndDatabaseOffBaseline
                .Should()
                .Be(Invalid(TargetIncompatible, DocumentJsonColumnTypeCheck));

        [Test]
        public void It_normalizes_the_catalog_type_before_comparing() => _mixedCaseType.Should().Be(_valid);
    }

    [TestFixture]
    public class Given_inventory_drift
    {
        private DmsTargetValidation _extraObject = null!;
        private DmsTargetValidation _missingObject = null!;
        private DmsTargetValidation _objectInUnlistedSchema = null!;
        private DmsTargetValidation _duplicateSchema = null!;
        private bool _buildable;
        private string? _defect;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            _extraObject = Staging(facts.WithObjects(new DmsInventoryObject("edfi", "Intruder", "table")));
            _missingObject = Staging(
                facts with
                {
                    InventoryObjects = [.. facts.InventoryObjects.Where(entry => entry.Name != "School")],
                }
            );
            _objectInUnlistedSchema = Staging(
                facts.WithObjects(new DmsInventoryObject("ghost", "Table", "table"))
            );
            _duplicateSchema = Staging(facts.WithSchemas("edfi"));
            _buildable = TryBuildInventory(
                facts.WithObjects(new DmsInventoryObject("ghost", "Table", "table")),
                out _,
                out _defect
            );
        }

        [Test]
        public void It_rejects_an_object_the_manifest_does_not_declare() =>
            _extraObject.Should().Be(Invalid(TargetIncompatible, InventoryHashCheck));

        [Test]
        public void It_rejects_a_missing_declared_object() =>
            _missingObject.Should().Be(Invalid(TargetIncompatible, InventoryHashCheck));

        [Test]
        public void It_rejects_an_object_whose_schema_was_not_enumerated() =>
            _objectInUnlistedSchema.Should().Be(Invalid(TargetIncompatible, InventoryHashCheck));

        [Test]
        public void It_rejects_a_repeated_schema() =>
            _duplicateSchema.Should().Be(Invalid(TargetIncompatible, InventoryHashCheck));

        [Test]
        public void It_names_the_defect_by_position_only() =>
            (_buildable, _defect)
                .Should()
                .Be((false, "object entry 11 names a schema the schema enumeration did not report."));
    }

    [TestFixture]
    public class Given_content_profile_violations_the_manifest_also_declares
    {
        private Dictionary<string, DmsTargetValidation> _verdicts = [];
        private DmsTargetValidation _authAllowed = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            Dictionary<string, DmsTargetFacts> contaminated = new()
            {
                ["dms missing"] = facts.WithoutSchema("dms"),
                ["dms empty"] = facts with
                {
                    InventoryObjects = [.. facts.InventoryObjects.Where(entry => entry.SchemaName != "dms")],
                },
                ["dmscs"] = facts
                    .WithSchemas("dmscs")
                    .WithObjects(new DmsInventoryObject("dmscs", "Vendor", "table")),
                ["openiddict"] = facts.WithObjects(
                    new DmsInventoryObject("edfi", "OpenIddictToken", "table")
                ),
                ["public not empty"] = facts.WithObjects(
                    new DmsInventoryObject("public", "scratch", "table")
                ),
                ["resource without companion"] = facts
                    .WithSchemas("tpdm")
                    .WithObjects(new DmsInventoryObject("tpdm", "Candidate", "table")),
                ["companion without resource"] = facts.WithSchemas("tracked_changes_tpdm"),
                ["lookalike companion"] = facts.WithSchemas("tracked_changesx"),
                ["edfi missing"] = facts
                    .WithoutSchema("edfi")
                    .WithoutSchema("tracked_changes_edfi")
                    .WithSchemas("tpdm", "tracked_changes_tpdm"),
                ["principal"] = facts with { Principals = ["app_user"] },
                ["database-level object"] = facts with
                {
                    DatabaseLevelObjects = [new DmsDatabaseLevelObject("extension", "postgis")],
                },
            };
            _verdicts = contaminated.ToDictionary(
                pair => pair.Key,
                pair =>
                    Validate(
                        pair.Value,
                        Identity(),
                        ManifestFor(pair.Value, projects: ProjectsOf(pair.Key)),
                        ProjectsOf(pair.Key),
                        new DmsTargetValidationRule.Staging(_expected)
                    )
            );
            DmsTargetFacts withAuth = facts
                .WithSchemas("auth")
                .WithObjects(
                    new DmsInventoryObject("auth", "EducationOrganizationIdToEducationOrganizationId", "view")
                );
            _authAllowed = Staging(withAuth, ManifestFor(withAuth));
        }

        private static string[] ProjectsOf(string vector) => vector == "edfi missing" ? ["tpdm"] : Projects;

        [Test]
        public void It_rejects_every_contamination_as_a_content_profile_violation() =>
            _verdicts
                .Should()
                .AllSatisfy(pair =>
                    pair.Value.Should().Be(Invalid(ContentProfileViolation, ContentProfileCheck), pair.Key)
                );

        [Test]
        public void It_covers_eleven_vectors() => _verdicts.Should().HaveCount(11);

        [Test]
        public void It_allows_the_auth_companion() => _authAllowed.Should().Be(_valid);
    }

    [TestFixture]
    public class Given_project_set_drift
    {
        private DmsTargetValidation _manifestNamesFewer = null!;
        private DmsTargetValidation _manifestCaseDiffers = null!;
        private DmsTargetValidation _expectationsNameMore = null!;
        private DmsTargetValidation _expectationsCaseDiffers = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts twoProjects = PostgresqlFacts()
                .WithSchemas("tpdm", "tracked_changes_tpdm")
                .WithObjects(new DmsInventoryObject("tpdm", "Candidate", "table"));
            _manifestNamesFewer = Validate(
                twoProjects,
                Identity(),
                ManifestFor(twoProjects, projects: ["edfi"]),
                ["edfi", "tpdm"],
                new DmsTargetValidationRule.Staging(_expected)
            );
            DmsTargetFacts facts = PostgresqlFacts();
            _manifestCaseDiffers = Staging(facts, ManifestFor(facts, projects: ["EDFI"]));
            _expectationsNameMore = Staging(facts, expectedProjects: ["edfi", "tpdm"]);
            _expectationsCaseDiffers = Staging(facts, expectedProjects: ["EdFi"]);
        }

        [Test]
        public void It_rejects_a_manifest_naming_fewer_projects_than_the_database() =>
            _manifestNamesFewer.Should().Be(Invalid(TargetIncompatible, ProjectSetCheck));

        [Test]
        public void It_compares_the_manifest_projects_ordinally() =>
            _manifestCaseDiffers.Should().Be(Invalid(TargetIncompatible, ProjectSetCheck));

        [Test]
        public void It_rejects_expectations_naming_more_projects_than_the_database() =>
            _expectationsNameMore.Should().Be(Invalid(TargetIncompatible, ProjectSetCheck));

        [Test]
        public void It_compares_the_expectations_ignoring_case() =>
            _expectationsCaseDiffers.Should().Be(_valid);
    }

    [TestFixture]
    public class Given_security_rule_vectors
    {
        private DmsTargetValidation _extraEnqueueGrant = null!;
        private DmsTargetValidation _publicExecuteOnEnqueueFunction = null!;
        private DmsTargetValidation _foreignOwner = null!;
        private DmsTargetValidation _extraGrant = null!;
        private DmsTargetValidation _publicSelect = null!;
        private DmsTargetValidation _publicUsageOnDms = null!;
        private DmsTargetValidation _columnGrant = null!;
        private DmsTargetValidation _enqueueOwnedRelation = null!;
        private DmsTargetValidation _ownerPrivilegeOfForeignOwner = null!;
        private DmsTargetValidation _ownershipAndPrivilegeViolations = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            _extraEnqueueGrant = Staging(
                facts.WithPrivileges(
                    Held("relation", "DELETE", Admin, Enqueue, "dms.\"DocumentProjectionWork\"")
                )
            );
            _publicExecuteOnEnqueueFunction = Staging(
                facts.WithPrivileges(Held("function", "EXECUTE", Enqueue, "-", EnqueueFunction))
            );
            _foreignOwner = Staging(facts.WithOwnership(Owned("relation", "intruder", "edfi.\"School\"")));
            _extraGrant = Staging(
                facts.WithPrivileges(Held("relation", "SELECT", Admin, "reporting", "edfi.\"School\""))
            );
            _publicSelect = Staging(
                facts.WithPrivileges(Held("relation", "SELECT", Admin, "-", "edfi.\"School\""))
            );
            _publicUsageOnDms = Staging(facts.WithPrivileges(Held("schema", "USAGE", Admin, "-", "dms")));
            _columnGrant = Staging(
                facts.WithPrivileges(
                    Held("column", "SELECT", Admin, "reporting", "edfi.\"School\".\"SchoolId\"")
                )
            );
            _enqueueOwnedRelation = Staging(
                facts.WithOwnership(Owned("relation", Enqueue, "dms.\"DocumentProjectionWork\""))
            );
            _ownerPrivilegeOfForeignOwner = Staging(
                facts
                    .WithOwnership(Owned("relation", "intruder", "edfi.\"School\""))
                    .WithPrivileges(Held("relation", "SELECT", "intruder", "intruder", "edfi.\"School\""))
            );
            _ownershipAndPrivilegeViolations = Staging(
                facts
                    .WithOwnership(Owned("relation", "intruder", "edfi.\"School\""))
                    .WithPrivileges(Held("relation", "SELECT", Admin, "reporting", "dms.\"Document\""))
            );
        }

        [Test]
        public void It_rejects_a_grant_to_the_enqueue_owner_outside_the_allowlist() =>
            _extraEnqueueGrant.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_public_execute_on_an_enqueue_owned_function() =>
            _publicExecuteOnEnqueueFunction.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_a_foreign_owner() =>
            _foreignOwner.Should().Be(Invalid(SecurityStateViolation, OwnershipCheck));

        [Test]
        public void It_rejects_an_extra_grant_to_another_role() =>
            _extraGrant.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_public_select_on_a_relation() =>
            _publicSelect.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_public_usage_on_a_schema_other_than_public() =>
            _publicUsageOnDms.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_a_column_grant() =>
            _columnGrant.Should().Be(Invalid(SecurityStateViolation, PrivilegesCheck));

        [Test]
        public void It_rejects_the_enqueue_owner_owning_a_relation() =>
            _enqueueOwnedRelation.Should().Be(Invalid(SecurityStateViolation, OwnershipCheck));

        [Test]
        public void It_reports_ownership_before_the_privileges_an_owner_change_moves() =>
            _ownerPrivilegeOfForeignOwner.Should().Be(Invalid(SecurityStateViolation, OwnershipCheck));

        [Test]
        public void It_reports_ownership_before_an_unrelated_privilege_violation() =>
            _ownershipAndPrivilegeViolations.Should().Be(Invalid(SecurityStateViolation, OwnershipCheck));
    }

    [TestFixture]
    public class Given_the_populated_predicate
    {
        private DmsTargetValidation _populatedWithout = null!;
        private DmsTargetValidation _populatedUnknown = null!;
        private DmsTargetValidation _populatedWith = null!;
        private DmsTargetValidation _minimalWithout = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            RestoreManifest populated = ManifestFor(facts, RestoreManifest.PopulatedTemplateKind);
            _populatedWithout = Staging(facts, populated);
            _populatedUnknown = Staging(facts with { HasPopulatedDocument = null }, populated);
            _populatedWith = Staging(facts with { HasPopulatedDocument = true }, populated);
            _minimalWithout = Staging(facts);
        }

        [Test]
        public void It_rejects_a_populated_template_without_a_document() =>
            _populatedWithout.Should().Be(Invalid(TargetIncompatible, PopulatedPredicateCheck));

        [Test]
        public void It_rejects_a_populated_template_whose_document_tables_are_missing() =>
            _populatedUnknown.Should().Be(Invalid(TargetIncompatible, PopulatedPredicateCheck));

        [Test]
        public void It_accepts_a_populated_template_with_a_document() => _populatedWith.Should().Be(_valid);

        [Test]
        public void It_ignores_the_predicate_for_a_minimal_template() => _minimalWithout.Should().Be(_valid);
    }

    [TestFixture]
    public class Given_several_failing_checks
    {
        private DmsTargetValidation _inventoryAndPrivileges = null!;
        private DmsTargetValidation _profileAndPrivileges = null!;

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = PostgresqlFacts();
            DmsPostgresqlPrivilegeRow intruder = Held(
                "relation",
                "SELECT",
                Admin,
                "intruder",
                "edfi.\"School\""
            );
            _inventoryAndPrivileges = Staging(
                facts.WithObjects(new DmsInventoryObject("edfi", "Extra", "table")).WithPrivileges(intruder)
            );
            DmsTargetFacts contaminated = facts.WithSchemas("dmscs").WithPrivileges(intruder);
            _profileAndPrivileges = Staging(contaminated, ManifestFor(contaminated));
        }

        [Test]
        public void It_reports_the_inventory_hash_before_the_privileges() =>
            _inventoryAndPrivileges.Should().Be(Invalid(TargetIncompatible, InventoryHashCheck));

        [Test]
        public void It_reports_the_content_profile_before_the_privileges() =>
            _profileAndPrivileges.Should().Be(Invalid(ContentProfileViolation, ContentProfileCheck));
    }

    /// <summary>
    /// Identity state inside the non-empty <c>dms</c> schema is contamination the manifest also declares, so
    /// only the content-profile gate can catch it; the match is case-insensitive on the object name.
    /// </summary>
    [TestFixture]
    public class Given_identity_state_objects_inside_the_dms_schema
    {
        private readonly Dictionary<string, DmsTargetValidation> _staging = [];
        private readonly Dictionary<string, DmsTargetValidation> _reconcile = [];
        private DmsTargetValidation _lookalikePasses = null!;

        [SetUp]
        public void Setup()
        {
            _staging.Clear();
            _reconcile.Clear();
            string[] names = ["OpenIddictTokens", "openiddictauthorizations", "OPENIDDICTSCOPES"];
            foreach (string name in names)
            {
                DmsTargetFacts facts = PostgresqlFacts()
                    .WithObjects(new DmsInventoryObject("dms", name, "table"));
                RestoreManifest manifest = ManifestFor(facts);
                _staging[name] = Validate(
                    facts,
                    Identity(),
                    manifest,
                    Projects,
                    new DmsTargetValidationRule.Staging(_expected)
                );
                _reconcile[name] = Validate(
                    facts,
                    Identity(),
                    manifest,
                    Projects,
                    new DmsTargetValidationRule.Reconcile(PackageIdentity)
                );
            }

            DmsTargetFacts lookalike = PostgresqlFacts()
                .WithObjects(new DmsInventoryObject("dms", "OpenIdConnectState", "table"));
            _lookalikePasses = Staging(lookalike, ManifestFor(lookalike));
        }

        [Test]
        public void It_rejects_every_identity_state_object_on_staging() =>
            _staging
                .Should()
                .AllSatisfy(pair =>
                    pair.Value.Should().Be(Invalid(ContentProfileViolation, ContentProfileCheck), pair.Key)
                );

        [Test]
        public void It_rejects_every_identity_state_object_on_reconciliation() =>
            _reconcile
                .Should()
                .AllSatisfy(pair =>
                    pair.Value.Should().Be(Invalid(ContentProfileViolation, ContentProfileCheck), pair.Key)
                );

        [Test]
        public void It_covers_three_casings() => (_staging.Count, _reconcile.Count).Should().Be((3, 3));

        [Test]
        public void It_accepts_a_name_that_only_shares_the_prefix_letters() =>
            _lookalikePasses.Should().Be(_valid);
    }

    /// <summary>
    /// <c>Assert-MssqlArtifactSecurityState</c> (F-P9b, D-15): only the built-in security state of a new SQL
    /// Server database passes; every other permission or membership is a violation, judged by principal id,
    /// securable class and scope, and grant state, never by name.
    /// </summary>
    [TestFixture]
    public class Given_sql_server_security_state_vectors
    {
        private DmsTargetValidation _clean = null!;
        private Dictionary<string, DmsTargetValidation> _violations = [];

        [SetUp]
        public void Setup()
        {
            DmsTargetFacts facts = MssqlFacts();
            _clean = Staging(facts, MssqlManifestFor(facts));
            Dictionary<string, DmsTargetFacts> vectors = new(StringComparer.Ordinal)
            {
                ["connect granted to guest"] = facts.WithPermissions(
                    Permission(0, "CONNECT", "GRANT", MssqlGuest, 0)
                ),
                ["deny on a system object"] = facts.WithPermissions(
                    Permission(1, "SELECT", "DENY", MssqlPublic, -385)
                ),
                ["extra db_owner member"] = facts.WithRoleMembers(Member(MssqlDbOwner, 5)),
                ["dbo in another role"] = facts.WithRoleMembers(Member(16385, MssqlDbo)),
                ["public select on a user object"] = facts.WithPermissions(
                    Permission(1, "SELECT", "GRANT", MssqlPublic, 245575913)
                ),
                ["default with grant option"] = facts.WithPermissions(
                    Permission(0, "CONNECT", "GRANT_WITH_GRANT_OPTION", MssqlDbo, 0)
                ),
                ["column-scoped select on a system object"] = facts.WithPermissions(
                    Permission(1, "SELECT", "GRANT", MssqlPublic, -385, 1)
                ),
                ["schema grant to public"] = facts.WithPermissions(
                    Permission(3, "SELECT", "GRANT", MssqlPublic, 5)
                ),
                ["database default to the wrong grantee"] = facts.WithPermissions(
                    Permission(0, "VIEW ANY COLUMN MASTER KEY DEFINITION", "GRANT", MssqlDbo, 0)
                ),
                ["connect granted to public"] = facts.WithPermissions(
                    Permission(0, "CONNECT", "GRANT", MssqlPublic, 0)
                ),
                ["system object select to dbo"] = facts.WithPermissions(
                    Permission(1, "SELECT", "GRANT", MssqlDbo, -385)
                ),
                ["system object execute to public"] = facts.WithPermissions(
                    Permission(1, "EXECUTE", "GRANT", MssqlPublic, -385)
                ),
                ["state in another case"] = facts.WithPermissions(
                    Permission(0, "CONNECT", "grant", MssqlDbo, 0)
                ),
                ["database permission outside the defaults"] = facts.WithPermissions(
                    Permission(0, "ALTER", "GRANT", MssqlDbo, 0)
                ),
            };
            _violations = vectors.ToDictionary(
                pair => pair.Key,
                pair => Staging(pair.Value, MssqlManifestFor(pair.Value)),
                StringComparer.Ordinal
            );
        }

        [Test]
        public void It_passes_the_built_in_security_state_of_a_new_database() => _clean.Should().Be(_valid);

        [Test]
        public void It_reports_every_other_permission_or_membership_as_a_security_state_violation() =>
            _violations
                .Should()
                .AllSatisfy(pair =>
                    pair.Value.Should().Be(Invalid(SecurityStateViolation, SecurityStateCheck), pair.Key)
                );

        [Test]
        public void It_covers_each_vector() => _violations.Count.Should().Be(14);
    }
}
