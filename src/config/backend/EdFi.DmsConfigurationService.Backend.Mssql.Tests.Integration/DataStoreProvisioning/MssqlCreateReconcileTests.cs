// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's reconcile end to end on SQL Server (6.5, Step 4.7) over the real catalog and engine, with a
/// package whose artifact is a server-made backup of the committed DMS-shaped fixture: a target created by this
/// provisioner is provisioned again. With its pin and expected identity it is <c>Reconciled</c> after the full
/// validation; with another expected identity it is <c>TargetUnowned</c>; without a pin the request is refused before
/// any server call; after the allowlist moves to another backup with identical content, the old pin is
/// <c>ArtifactChangedSinceCreate</c> before any server call. A target whose effective schema was corrupted, whose
/// compatibility level was changed, or on which <c>public</c> was granted a permission after the create, carries the
/// expected identity and is still refused. A login the server refuses (connection, identity, facts) or that cannot see
/// the file catalog leaves the target unverifiable. No retry mutates the target: its instance, identity, sentinel
/// document, object count and canonical inventory hash are unchanged, and the engine recorded nothing but reads. Needs
/// a staging directory shared with the server, because each fixture first creates its target.
/// </summary>
[Category(Category)]
public class MssqlCreateReconcileTests
{
    private static readonly Guid _sentinelDocument = Guid.Parse("8b7a6c5d-4e3f-4b2a-9d1c-0f9e8d7c6b5a");

    /// <summary>Every engine member a reconcile may call; anything else would be a mutation or a staging step.</summary>
    private static readonly string[] _readMembers =
    [
        nameof(IDmsTargetEngine.AcquireTargetLockAsync),
        nameof(IDmsTargetEngine.ReadServerFactsAsync),
        nameof(IDmsTargetEngine.InspectAsync),
        nameof(IDmsTargetEngine.OpenSessionAsync),
        nameof(IDmsTargetEngine.ReadIdentityAsync),
        nameof(IDmsTargetEngine.ReadTargetFactsAsync),
    ];

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsDataStoreCreateResult result
    )
    {
        DmsProvisioningFailure failure = result
            .Should()
            .BeOfType<DmsDataStoreCreateResult.Failure>()
            .Subject.Detail;
        return (failure.Code, failure.Category, failure.Diagnostic.Operation);
    }

    /// <summary>What a retry must leave unchanged about the created target.</summary>
    private sealed record Evidence(
        DmsInstanceIdentity.Mssql Instance,
        Guid Identity,
        bool SentinelDocument,
        int ObjectCount,
        string Inventory
    );

    private static async Task<Evidence> EvidenceAsync(DmsTargetDatabaseName target)
    {
        TestDatabase database = new(target.Value);
        DmsInstanceIdentity.Mssql instance = await database.CatalogInstanceAsync();
        Evidence evidence = new(
            instance,
            Guid.Parse(
                await database.ScalarAsync<string>(
                    "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
                )
            ),
            await database.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM [dms].[Document] WHERE [DocumentUuid] = '{_sentinelDocument:D}';"
            ) == 1,
            await database.ScalarAsync<int>(TargetEvidence.ObjectCountQuery),
            await TargetEvidence.InventoryFingerprintAsync(target, instance)
        );
        await WaitForNoSessionsOnAsync(target.Value);
        return evidence;
    }

    /// <summary>A lane whose create has published the target; the fixture's own retries follow.</summary>
    private static async Task<Lane> CreatedAsync(byte[] artifact)
    {
        Lane lane = await Lane.BuildAsync(artifact, new LaneHooks());
        (await lane.CreateAsync()).Should().BeOfType<DmsDataStoreCreateResult.Created>();
        return lane;
    }

    /// <summary>Runs <paramref name="retry"/> and returns its result with the engine calls it made.</summary>
    private static async Task<(DmsDataStoreCreateResult Result, string[] Members)> RetryAsync(
        RecordingEngine engine,
        Func<Task<DmsDataStoreCreateResult>> retry
    )
    {
        int before = engine.Calls.Count;
        DmsDataStoreCreateResult result = await retry();
        return (result, [.. engine.Calls.Skip(before).Select(call => call.Member)]);
    }

    [TestFixture]
    public class Given_a_created_target_is_provisioned_again
    {
        private Lane? _lane;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _withPin;
        private (DmsDataStoreCreateResult Result, string[] Members) _otherIdentity;
        private (DmsDataStoreCreateResult Result, string[] Members) _withoutPin;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await CreatedAsync(await ShapedBackup.CreateAsync());

            // Data, not schema: the inventory and the validation are unaffected, the evidence is not.
            await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                $"INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId]) VALUES ('{_sentinelDocument:D}', 2);"
            );
            _before = await EvidenceAsync(_lane.Target);

            Lane lane = _lane;
            _withPin = await RetryAsync(lane.Engine, () => lane.Service.CreateAsync(lane.Request, default));
            _otherIdentity = await RetryAsync(
                lane.Engine,
                () =>
                    lane.Service.CreateAsync(
                        lane.Request with
                        {
                            ExpectedSourceIdentity = Guid.NewGuid(),
                        },
                        default
                    )
            );
            _withoutPin = await RetryAsync(
                lane.Engine,
                () => lane.Service.CreateAsync(lane.Request with { PinnedArtifact = null! }, default)
            );
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_reconciles_the_owned_target_with_its_pin() =>
            _withPin
                .Result.Should()
                .Be(
                    new DmsDataStoreCreateResult.Reconciled(
                        _lane!.Request.PinnedArtifact,
                        DmsCleanupReport.Clean
                    )
                );

        [Test]
        public void It_reports_another_expected_identity_as_unowned() =>
            Verdict(_otherIdentity.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_refuses_a_retry_without_a_pin_before_any_server_call()
        {
            Verdict(_withoutPin.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactPinMissing,
                        DmsProvisioningFailureCategory.InvalidRequest,
                        DmsDataStoreProvisioningService.BoundaryStep
                    )
                );
            _withoutPin.Members.Should().BeEmpty();
        }

        [Test]
        public void It_only_read_during_the_retries()
        {
            _withPin.Members.Should().Equal(_readMembers);
            _otherIdentity.Members.Should().Equal(_readMembers[..^1]);
        }

        [Test]
        public void It_left_the_target_exactly_as_it_was()
        {
            _before.Should().Match<Evidence>(evidence => evidence.SentinelDocument);
            _after.Should().Be(_before);
            _after.Identity.Should().Be(_lane!.Request.ExpectedSourceIdentity);
        }

        [Test]
        public void It_left_no_operation_directory() => _lane!.OperationEntries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_allowlist_moved_to_another_backup_with_identical_content
    {
        private Lane? _lane;
        private Lane.Deployment _switched = null!;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _oldPin;
        private (DmsDataStoreCreateResult Result, string[] Members) _newPin;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await CreatedAsync(await ShapedBackup.CreateAsync());
            _before = await EvidenceAsync(_lane.Target);

            // A second server-made backup of the same fixture: the same schema and inventory, other bytes (each
            // backup records its own database, set and dates), under a different package version.
            _switched = await _lane.SwitchAllowlistAsync(await ShapedBackup.CreateAsync(), "1.0.124");
            Lane lane = _lane;
            _oldPin = await RetryAsync(
                _switched.Engine,
                () => _switched.Service.CreateAsync(lane.Request, default)
            );

            // The control: the content passes the full validation, so only the pin tells the packages apart.
            _newPin = await RetryAsync(
                _switched.Engine,
                () =>
                    _switched.Service.CreateAsync(
                        lane.Request with
                        {
                            PinnedArtifact = _switched.Pin,
                        },
                        default
                    )
            );
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_switched_to_a_package_that_differs_only_in_bytes_and_version()
        {
            DmsTrustedArtifactPin created = _lane!.Request.PinnedArtifact;
            (
                _switched.Pin.PackageSha256 == created.PackageSha256,
                _switched.Pin.ArtifactSha256 == created.ArtifactSha256,
                _switched.Pin.PackageVersion == created.PackageVersion
            )
                .Should()
                .Be((false, false, false));
            (_switched.Pin.InventorySha256, _switched.Pin.EffectiveSchemaHash)
                .Should()
                .Be((created.InventorySha256, created.EffectiveSchemaHash));
        }

        [Test]
        public void It_refuses_the_old_pin_before_any_server_call()
        {
            Verdict(_oldPin.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveArtifactStep
                    )
                );
            _oldPin.Members.Should().BeEmpty();
        }

        [Test]
        public void It_would_reconcile_the_identical_content_under_the_new_pin() =>
            (_newPin.Result, _newPin.Members)
                .Should()
                .Match<(DmsDataStoreCreateResult Result, string[] Members)>(run =>
                    run.Result is DmsDataStoreCreateResult.Reconciled
                    && run.Members.SequenceEqual(_readMembers)
                );

        [Test]
        public void It_left_the_target_exactly_as_it_was() => _after.Should().Be(_before);
    }

    [TestFixture]
    public class Given_a_created_target_drifted_after_the_create
    {
        private Lane? _corrupted;
        private Lane? _recompatible;
        private Lane? _granted;
        private int _driftedLevel;
        private (DmsDataStoreCreateResult Result, string[] Members) _corruptedRetry;
        private (DmsDataStoreCreateResult Result, string[] Members) _recompatibleRetry;
        private (DmsDataStoreCreateResult Result, string[] Members) _grantedRetry;
        private (Evidence Before, Evidence After) _corruptedEvidence;
        private (Evidence Before, Evidence After) _recompatibleEvidence;
        private (Evidence Before, Evidence After) _grantedEvidence;
        private string _corruptedHashAfter = "";
        private int _levelAfter;
        private int _publicGrantsAfter;

        private const string PublicSelectOnSchool =
            "SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = 0 AND class = 1 AND major_id = OBJECT_ID(N'[edfi].[School]') AND permission_name = N'SELECT' AND state_desc = N'GRANT';";

        private static async Task<(DmsDataStoreCreateResult Result, string[] Members)> RetryOwnAsync(
            Lane lane
        ) => await RetryAsync(lane.Engine, () => lane.Service.CreateAsync(lane.Request, default));

        private static string Check(string check) =>
            DmsDataStoreProvisioningService.ReconcileValidateStep + "." + check;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            byte[] artifact = await ShapedBackup.CreateAsync();

            _corrupted = await CreatedAsync(artifact);
            TestDatabase corrupted = new(_corrupted.Target.Value);
            await corrupted.ExecuteAsync(
                $"UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = N'{new string('0', 64)}';"
            );
            Evidence corruptedBefore = await EvidenceAsync(_corrupted.Target);
            _corruptedRetry = await RetryOwnAsync(_corrupted);
            _corruptedEvidence = (corruptedBefore, await EvidenceAsync(_corrupted.Target));
            _corruptedHashAfter = await corrupted.ScalarAsync<string>(
                "SELECT [EffectiveSchemaHash] FROM [dms].[EffectiveSchema];"
            );

            // The compatibility level the manifest records (the server's model database) is moved to another one.
            _recompatible = await CreatedAsync(artifact);
            _driftedLevel = await LevelOfAsync(_recompatible.Target) == 160 ? 150 : 160;
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {MssqlIdentifiers.Quote(_recompatible.Target.Value)} SET COMPATIBILITY_LEVEL = {_driftedLevel:D};"
            );
            Evidence recompatibleBefore = await EvidenceAsync(_recompatible.Target);
            _recompatibleRetry = await RetryOwnAsync(_recompatible);
            _recompatibleEvidence = (recompatibleBefore, await EvidenceAsync(_recompatible.Target));
            _levelAfter = await LevelOfAsync(_recompatible.Target);

            _granted = await CreatedAsync(artifact);
            TestDatabase granted = new(_granted.Target.Value);
            await granted.ExecuteAsync("GRANT SELECT ON [edfi].[School] TO [public];");
            Evidence grantedBefore = await EvidenceAsync(_granted.Target);
            _grantedRetry = await RetryOwnAsync(_granted);
            _grantedEvidence = (grantedBefore, await EvidenceAsync(_granted.Target));
            _publicGrantsAfter = await granted.ScalarAsync<int>(PublicSelectOnSchool);
        }

        private static async Task<int> LevelOfAsync(DmsTargetDatabaseName target) =>
            await ScalarOnServerAsync<byte>(
                "SELECT compatibility_level FROM sys.databases WHERE name = @name;",
                ("@name", target.Value)
            );

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _corrupted, _recompatible, _granted })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }
        }

        [Test]
        public void It_refuses_a_corrupted_effective_schema_despite_the_expected_identity() =>
            Verdict(_corruptedRetry.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        Check(DmsTargetValidator.EffectiveSchemaCheck)
                    )
                );

        [Test]
        public void It_refuses_a_changed_compatibility_level_despite_the_expected_identity() =>
            Verdict(_recompatibleRetry.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        Check(DmsTargetValidator.CompatibilityLevelCheck)
                    )
                );

        [Test]
        public void It_refuses_a_permission_granted_to_public_after_the_create_despite_the_expected_identity() =>
            Verdict(_grantedRetry.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        Check(DmsTargetValidator.SecurityStateCheck)
                    )
                );

        [Test]
        public void It_read_the_full_facts_and_nothing_more() =>
            new[] { _corruptedRetry.Members, _recompatibleRetry.Members, _grantedRetry.Members }
                .Should()
                .AllSatisfy(members => members.Should().Equal(_readMembers));

        [Test]
        public void It_left_every_drift_in_place_for_inspection()
        {
            _corruptedEvidence.After.Should().Be(_corruptedEvidence.Before);
            _recompatibleEvidence.After.Should().Be(_recompatibleEvidence.Before);
            _grantedEvidence.After.Should().Be(_grantedEvidence.Before);
            (_corruptedHashAfter, _levelAfter, _publicGrantsAfter)
                .Should()
                .Be((new string('0', 64), _driftedLevel, 1));
        }
    }

    /// <summary>
    /// F-01 over the real catalog and engine, without a restore: an owned target seeded under the target name
    /// reconciles when intact; with its identity column dropped it is <c>TargetUnverifiable</c>; with a NULL or a
    /// missing <c>dms.EffectiveSchema</c> value, or (R2-1) with Populated-predicate tables whose shape SQL Server
    /// refuses when compiling the predicate, it is <c>TargetIncompatible</c>, all for inspection, never
    /// <c>Unknown</c>. The lane's template is Minimal, whose object-level inventory does not see the column, so the
    /// predicate check alone refuses it; with both defects the effective schema is reported first. Each retry only
    /// reads, and the target keeps its instance and stays open to connections.
    /// </summary>
    [TestFixture]
    public class Given_an_owned_target_with_malformed_metadata
    {
        private readonly List<Lane> _lanes = [];
        private Run _intact = null!;
        private Run _identityColumnMissing = null!;
        private Run _nullHash = null!;
        private Run _seedHashMissing = null!;
        private Run _resourceNameMissing = null!;
        private Run _xmlResourceName = null!;
        private Run _combined = null!;

        private sealed record Run(
            DmsDataStoreCreateResult Result,
            string[] Members,
            (bool Exists, int DatabaseId, string UserAccess) Before,
            (bool Exists, int DatabaseId, string UserAccess) After
        );

        private async Task<Run> RetryOnSeededAsync(byte[] artifact, params string[] alterations)
        {
            Lane lane = await Lane.BuildAsync(artifact, new LaneHooks());
            _lanes.Add(lane);
            await lane.SeedTargetAsync();
            TestDatabase target = new(lane.Target.Value);
            foreach (string alteration in alterations)
            {
                await target.ExecuteAsync(alteration);
            }

            await WaitForNoSessionsOnAsync(lane.Target.Value);
            (bool, int, string) before = await lane.TargetCatalogAsync();
            (DmsDataStoreCreateResult result, string[] members) = await RetryAsync(
                lane.Engine,
                () => lane.Service.CreateAsync(lane.Request, default)
            );
            return new Run(result, members, before, await lane.TargetCatalogAsync());
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            byte[] artifact = await ShapedBackup.CreateAsync();
            _intact = await RetryOnSeededAsync(artifact);
            _identityColumnMissing = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[DataStoreIdentity] DROP COLUMN [SourceIdentity];"
            );
            _nullHash = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[EffectiveSchema] DROP CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash];",
                "ALTER TABLE [dms].[EffectiveSchema] ALTER COLUMN [EffectiveSchemaHash] nvarchar(64) NULL;",
                "UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = NULL;"
            );
            _seedHashMissing = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[EffectiveSchema] DROP COLUMN [ResourceKeySeedHash];"
            );
            _resourceNameMissing = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[ResourceKey] DROP COLUMN [ResourceName];"
            );
            _xmlResourceName = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[ResourceKey] ALTER COLUMN [ResourceName] xml NULL;"
            );
            _combined = await RetryOnSeededAsync(
                artifact,
                "ALTER TABLE [dms].[ResourceKey] DROP COLUMN [ResourceName];",
                "ALTER TABLE [dms].[EffectiveSchema] DROP CONSTRAINT [UX_EffectiveSchema_EffectiveSchemaHash];",
                "ALTER TABLE [dms].[EffectiveSchema] ALTER COLUMN [EffectiveSchemaHash] nvarchar(64) NULL;",
                "UPDATE [dms].[EffectiveSchema] SET [EffectiveSchemaHash] = NULL;"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane lane in _lanes)
            {
                await lane.DisposeAsync();
            }
        }

        [Test]
        public void It_reconciles_the_intact_seeded_target() =>
            _intact.Result.Should().BeOfType<DmsDataStoreCreateResult.Reconciled>();

        [Test]
        public void It_reports_a_missing_identity_column_as_unverifiable_for_inspection() =>
            Verdict(_identityColumnMissing.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reports_a_null_effective_schema_value_as_incompatible_for_inspection() =>
            Verdict(_nullHash.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_reports_a_missing_effective_schema_column_as_incompatible_for_inspection() =>
            Verdict(_seedHashMissing.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_reports_a_minimal_target_with_malformed_predicate_tables_as_incompatible_for_inspection()
        {
            Verdict(_resourceNameMissing.Result).Should().Be(PopulatedPredicateIncompatible);
            Verdict(_xmlResourceName.Result).Should().Be(PopulatedPredicateIncompatible);
        }

        [Test]
        public void It_reports_the_effective_schema_first_when_the_predicate_tables_are_malformed_too() =>
            Verdict(_combined.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_only_read_during_each_retry()
        {
            _identityColumnMissing.Members.Should().Equal(_readMembers[..^1]);
            _nullHash.Members.Should().Equal(_readMembers);
            _seedHashMissing.Members.Should().Equal(_readMembers);
            _resourceNameMissing.Members.Should().Equal(_readMembers);
            _xmlResourceName.Members.Should().Equal(_readMembers);
            _combined.Members.Should().Equal(_readMembers);
        }

        [Test]
        public void It_left_each_target_in_place_and_open() =>
            new[]
            {
                _intact,
                _identityColumnMissing,
                _nullHash,
                _seedHashMissing,
                _resourceNameMissing,
                _xmlResourceName,
                _combined,
            }
                .Should()
                .AllSatisfy(run =>
                {
                    run.After.Should().Be(run.Before);
                    run.After.UserAccess.Should().Be("MULTI_USER");
                });

        private static (
            DmsProvisioningFailureCode,
            DmsProvisioningFailureCategory,
            string
        ) EffectiveSchemaIncompatible =>
            (
                DmsProvisioningFailureCode.TargetIncompatible,
                DmsProvisioningFailureCategory.OperatorInspectionRequired,
                DmsDataStoreProvisioningService.ReconcileValidateStep
                    + "."
                    + DmsTargetValidator.EffectiveSchemaCheck
            );

        private static (
            DmsProvisioningFailureCode,
            DmsProvisioningFailureCategory,
            string
        ) PopulatedPredicateIncompatible =>
            (
                DmsProvisioningFailureCode.TargetIncompatible,
                DmsProvisioningFailureCategory.OperatorInspectionRequired,
                DmsDataStoreProvisioningService.ReconcileValidateStep
                    + "."
                    + DmsTargetValidator.PopulatedPredicateCheck
            );
    }

    [TestFixture]
    public class Given_the_admin_login_is_refused_access_to_a_created_target
    {
        private static readonly string _sqlExceptionType = typeof(SqlException).FullName!;

        private Lane? _lane;
        private string? _login;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _connectDenied;
        private (DmsDataStoreCreateResult Result, string[] Members) _identityDenied;
        private (DmsDataStoreCreateResult Result, string[] Members) _factsDenied;
        private (DmsDataStoreCreateResult Result, string[] Members) _fileCatalogHidden;
        private bool _loginSessionsGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await CreatedAsync(await ShapedBackup.CreateAsync());
            _before = await EvidenceAsync(_lane.Target);

            // A login with no server role that can see the file catalog (VIEW ANY DEFINITION), so its inspection
            // carries the primary file; it has no user in the target and guest has no CONNECT there: the open is
            // refused (4060).
            _login = await CreateLoginAsync("reconcile");
            string login = MssqlIdentifiers.Quote(_login);
            await ExecuteOnServerAsync($"GRANT VIEW ANY DEFINITION TO {login};");
            Lane.Deployment restricted = _lane.WithEngine(EngineAs(_login));
            DmsDataStoreCreateRequest request = _lane.Request;
            _connectDenied = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            // A user without any grant: the session opens and the identity read is refused (229).
            TestDatabase target = new(_lane.Target.Value);
            await target.ExecuteAsync($"CREATE USER {login} FOR LOGIN {login};");
            _identityDenied = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            // The matching identity becomes readable, dms.EffectiveSchema stays refused: the facts read is (229).
            await target.ExecuteAsync($"GRANT SELECT ON [dms].[DataStoreIdentity] TO {login};");
            _factsDenied = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            // Without VIEW ANY DEFINITION neither the login's inspection nor its session can see the primary file:
            // the session cannot be bound to the inspected instance and nothing is read on it.
            await ExecuteOnServerAsync($"REVOKE VIEW ANY DEFINITION FROM {login};");
            _fileCatalogHidden = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            string loginName = _login;
            _loginSessionsGone = await WaitUntilAsync(
                async () => await SessionCountOfLoginAsync(loginName) == 0,
                TimeSpan.FromSeconds(20)
            );

            // The fixture's own user is removed again before the target is compared with its state after the create.
            await target.ExecuteAsync($"DROP USER {login};");
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string, string?) Refusal(
            DmsDataStoreCreateResult result
        )
        {
            DmsProvisioningFailure failure = result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            return (
                failure.Code,
                failure.Category,
                failure.Diagnostic.Operation,
                failure.Diagnostic.ProviderErrorCode
            );
        }

        [Test]
        public void It_reports_a_refused_connection_as_unverifiable_for_inspection() =>
            Refusal(_connectDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileOpenSessionStep,
                        "4060"
                    )
                );

        [Test]
        public void It_reports_a_refused_identity_read_as_unverifiable_for_inspection() =>
            Refusal(_identityDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep,
                        "229"
                    )
                );

        [Test]
        public void It_reports_a_refused_facts_read_of_an_owned_target_as_unverifiable_for_inspection() =>
            Refusal(_factsDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileReadTargetFactsStep,
                        "229"
                    )
                );

        [Test]
        public void It_reports_a_session_it_cannot_bind_to_the_instance_as_unverifiable_without_a_server_code() =>
            Refusal(_fileCatalogHidden.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileOpenSessionStep,
                        (string?)null
                    )
                );

        [Test]
        public void It_reads_nothing_after_the_refusal()
        {
            _connectDenied.Members.Should().Equal(_readMembers[..^2]);
            _identityDenied.Members.Should().Equal(_readMembers[..^1]);
            _factsDenied.Members.Should().Equal(_readMembers);
            _fileCatalogHidden.Members.Should().Equal(_readMembers[..^2]);
        }

        [Test]
        public void It_keeps_only_the_type_chain_in_the_diagnostic() =>
            new[] { _connectDenied.Result, _identityDenied.Result, _factsDenied.Result }
                .Select(result =>
                    ((DmsDataStoreCreateResult.Failure)result).Detail.Diagnostic.ExceptionTypeChain
                )
                .Should()
                .AllSatisfy(chain => chain.Should().Be(_sqlExceptionType));

        [Test]
        public void It_released_every_session_of_the_refused_login_and_its_operation_directories() =>
            (_loginSessionsGone, _lane!.OperationEntries.Length).Should().Be((true, 0));

        [Test]
        public void It_left_the_target_exactly_as_it_was() => _after.Should().Be(_before);
    }

    /// <summary>
    /// F-002 and AC18: this provisioner requires <c>sysadmin</c> on SQL Server. A login with every other privilege it
    /// once documented (<c>dbcreator</c>, <c>ALTER ANY DATABASE</c>, <c>ALTER ANY CONNECTION</c>,
    /// <c>VIEW SERVER STATE</c>) gets a <c>NotReady</c> environment from the real engine naming exactly the missing
    /// <c>sysadmin</c>. Recorded in the real readiness as startup records it, that verdict makes the provisioner's own
    /// gate refuse a create and a delete of an existing target as <c>StartupValidationFailed</c> before any engine
    /// call, so the target is untouched.
    /// </summary>
    [TestFixture]
    public class Given_the_admin_login_has_every_privilege_but_sysadmin
    {
        private Lane? _lane;
        private string? _login;
        private DmsEngineEnvironment _environment = null!;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;
        private int _engineCalls = -1;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireSharedStagingOrSkip();
            _lane = await CreatedAsync(await ShapedBackup.CreateAsync());
            _before = await EvidenceAsync(_lane.Target);

            _login = await CreateLoginAsync("nosysadmin");
            string quoted = MssqlIdentifiers.Quote(_login);
            await ExecuteOnServerAsync(
                $"ALTER SERVER ROLE [dbcreator] ADD MEMBER {quoted}; GRANT ALTER ANY DATABASE TO {quoted}; GRANT ALTER ANY CONNECTION TO {quoted}; GRANT VIEW SERVER STATE TO {quoted};"
            );

            // The engine's verdict under that login, recorded as DmsDataStoreProvisioningStartup records it.
            MssqlDmsTargetEngine engine = EngineAs(_login);
            _environment = await engine.ValidateEnvironmentAsync(default);
            DmsDataStoreProvisionerReadiness readiness = new();
            readiness.Record(
                _environment is DmsEngineEnvironment.NotReady notReady
                    ? new DmsProvisionerReadiness.NotReady(notReady.Reasons)
                    : new DmsProvisionerReadiness.Ready(engine.Provider, DateTimeOffset.UtcNow)
            );

            Lane.Deployment gated = _lane.WithEngine(engine, readiness: readiness);
            _create = await gated.Service.CreateAsync(_lane.Request, default);
            _delete = await gated.Service.DeleteAsync(_lane.DeleteRequest, default);
            _engineCalls = gated.Engine.Calls.Count;
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_login is not null)
            {
                SqlConnection.ClearAllPools();
                await DropLoginAsync(_login);
            }
        }

        private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) GateRefusal(
            DmsProvisioningFailure failure
        ) => (failure.Code, failure.Category, failure.Diagnostic.Operation);

        private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) NotReady =>
            (
                DmsProvisioningFailureCode.StartupValidationFailed,
                DmsProvisioningFailureCategory.NotReady,
                DisabledDmsDataStoreProvisioner.Operation
            );

        [Test]
        public void It_is_not_ready_naming_exactly_the_missing_sysadmin() =>
            _environment
                .Should()
                .BeOfType<DmsEngineEnvironment.NotReady>()
                .Which.Reasons.Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        MssqlEnvironment.CreateDatabasePrivilegeProbe,
                        DmsProvisioningDiagnostic.ForOperation(MssqlEnvironment.CreateDatabaseRoleOperation)
                    )
                );

        [Test]
        public void It_refuses_the_create_at_the_readiness_gate() =>
            GateRefusal(_create.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail)
                .Should()
                .Be(NotReady);

        [Test]
        public void It_refuses_the_delete_at_the_readiness_gate() =>
            GateRefusal(_delete.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail)
                .Should()
                .Be(NotReady);

        [Test]
        public void It_calls_the_engine_for_nothing_and_leaves_the_target_as_it_was()
        {
            _engineCalls.Should().Be(0);
            _after.Should().Be(_before);
        }
    }
}
