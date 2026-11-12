// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Step 3.9 review: why the PostgreSQL administrator must be a superuser. The artifact ends with
/// <c>ALTER FUNCTION … OWNER TO edfi_dms_enqueue_owner</c>, which PostgreSQL allows a non-superuser only when
/// it may <c>SET ROLE</c> to the new owner and that owner holds <c>CREATE</c> on the function's schema; the
/// artifact grants the owner <c>USAGE</c> on <c>dms</c> only, and the security gate refuses any other grant to
/// it. The most privileged non-superuser the provisioner could be given (<c>CREATEDB</c>, <c>CREATEROLE</c>,
/// <c>pg_signal_backend</c>, <c>pg_read_all_stats</c>, and membership in the owner with <c>SET</c>) therefore
/// fails the replay with <c>42501</c>, leaving no target and no staging database; readiness refuses that
/// role before any create is attempted.
/// </summary>
[Category(Category)]
public class PostgresqlNonSuperuserCreateTests
{
    [TestFixture]
    public class Given_the_most_privileged_non_superuser_administrator
    {
        private string? _role;
        private Lane? _lane;
        private DmsReadinessReason[] _readiness = [];
        private DmsDataStoreCreateResult _result = null!;
        private (bool Exists, uint Oid, bool AllowsConnections) _target;
        private long _stagingLeft;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            await RequireSuperuserOrSkipAsync();
            await EnsureEnqueueOwnerRoleAsync();

            _role = await CreateLoginRoleAsync("replayer", "pg_signal_backend", "pg_read_all_stats");
            string role = PostgresqlIdentifiers.Quote(_role);
            await ExecuteOnServerAsync($"ALTER ROLE {role} CREATEDB CREATEROLE;");
            await ExecuteOnServerAsync(
                $"GRANT \"edfi_dms_enqueue_owner\" TO {role} WITH INHERIT FALSE, SET TRUE;"
            );

            _readiness =
            [
                .. (await EngineAs(_role).ValidateEnvironmentAsync(default))
                    is DmsEngineEnvironment.NotReady notReady
                    ? notReady.Reasons
                    : [],
            ];

            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            _result = await _lane.WithEngine(EngineAs(_role)).Service.CreateAsync(_lane.Request, default);
            _target = await _lane.TargetCatalogAsync();
            _stagingLeft = await ScalarOnServerAsync<long>(
                "SELECT count(*) FROM pg_catalog.pg_database d WHERE d.datname LIKE 'edfi_dms_provision_stage_%' AND pg_catalog.shobj_description(d.oid, 'pg_database') LIKE @suffix;",
                ("suffix", "%:" + _lane.Request.ExpectedSourceIdentity.ToString("D"))
            );
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_role is not null)
            {
                await DropRoleAsync(_role);
            }
        }

        [Test]
        public void It_is_refused_by_readiness_as_not_a_superuser() =>
            _readiness
                .Should()
                .Equal(
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                        PostgresqlEnvironment.SuperuserProbe,
                        DmsProvisioningDiagnostic.ForOperation(
                            PostgresqlEnvironment.ArtifactOwnershipOperation
                        )
                    )
                );

        [Test]
        public void It_fails_the_replay_itself_with_42501() =>
            _result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Diagnostic.Should()
                .Match<DmsProvisioningDiagnostic>(diagnostic =>
                    diagnostic.Operation == PsqlRestoreRunner.RestoreStep
                    && diagnostic.ProviderErrorCode == "42501"
                );

        [Test]
        public void It_reports_the_missing_privilege() =>
            _result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.AdminPrivilegeMissing);

        [Test]
        public void It_leaves_no_target_and_no_staging_database() =>
            (_target.Exists, _stagingLeft).Should().Be((false, 0L));
    }
}
