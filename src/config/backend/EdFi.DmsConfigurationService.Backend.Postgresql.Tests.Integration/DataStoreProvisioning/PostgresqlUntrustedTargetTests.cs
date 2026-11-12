// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// H-01: the provisioner reads a candidate database as a superuser, so nothing another role chose may run during those
/// reads. A non-superuser role with <c>CREATEDB</c> plants <c>dms."DataStoreIdentity"</c> as a view over an invoker
/// function that counts its calls on a sequence (which survives a rolled-back call) and then tries to make that role a
/// superuser, returning the record's expected identity so a read would look like ownership. Under the target name the
/// create (on collision) and the delete of that record are refused for inspection, the function never runs, the role
/// keeps its attributes, and nothing is staged, fenced, renamed, or dropped. The database owned by the hostile role is
/// refused at inspection without a session; a database a superuser owns, with the view the hostile role created under
/// a <c>CREATE</c> grant, is refused at the identity read before the view is selected from.
/// </summary>
[Category(Category)]
public class PostgresqlUntrustedTargetTests
{
    /// <summary>The engine members that change a database or stage one.</summary>
    private static readonly string[] _mutatingMembers =
    [
        nameof(IDmsTargetEngine.CreateStagingAsync),
        nameof(IDmsTargetEngine.RestoreIntoStagingAsync),
        nameof(IDmsTargetEngine.RenameIntoTargetAsync),
        nameof(IDmsTargetEngine.AssignMarkerAsync),
        nameof(IDmsTargetEngine.FenceAsync),
        nameof(IDmsTargetEngine.UnfenceAsync),
        nameof(IDmsTargetEngine.DropInstanceAsync),
        nameof(IDmsTargetEngine.DropStagingAsync),
    ];

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsProvisioningFailure failure
    ) => (failure.Code, failure.Category, failure.Diagnostic.Operation);

    /// <summary>Whether the planted function ever ran: reading the sequence runs no code.</summary>
    private static Task<bool> InvokedAsync(string database) =>
        new TestDatabase(database).ScalarAsync<bool>("SELECT is_called FROM \"dms\".\"Invocations\";");

    /// <summary>One planted target, with a create and a delete of the record that expects it.</summary>
    public abstract class PlantedTargetFixture
    {
        /// <summary>The hostile role's attributes that a successful escalation would change.</summary>
        private static Task<string> AttributesOfAsync(string role) =>
            ScalarOnServerAsync<string>(
                "SELECT r.rolsuper::text || '|' || r.rolcreaterole::text || '|' || r.rolbypassrls::text FROM pg_catalog.pg_roles r WHERE r.rolname = @name;",
                ("name", role)
            );

        /// <summary>
        /// The planted objects, created by <paramref name="role"/> on a session it opens to <paramref name="database"/>, so
        /// it owns them: the call counter, the escalating function, and the identity view over it.
        /// </summary>
        private static async Task PlantAsync(string database, string role, Guid identity)
        {
            await using NpgsqlConnection connection = await AdminConnectionAs(role)
                .OpenAsync(database, "dms1438-test", default);
            await using NpgsqlCommand command = new(
                "CREATE SCHEMA \"dms\";"
                    + "CREATE SEQUENCE \"dms\".\"Invocations\";"
                    + "CREATE FUNCTION \"dms\".\"Escalate\"() RETURNS uuid LANGUAGE plpgsql SECURITY INVOKER AS $body$ BEGIN"
                    + " PERFORM pg_catalog.nextval('\"dms\".\"Invocations\"');"
                    + $" EXECUTE 'ALTER ROLE {PostgresqlIdentifiers.Quote(role)} SUPERUSER CREATEROLE BYPASSRLS';"
                    + $" RETURN '{identity:D}'::uuid; END $body$;"
                    + "CREATE VIEW \"dms\".\"DataStoreIdentity\" AS SELECT \"dms\".\"Escalate\"() AS \"SourceIdentity\";",
                connection
            );
            await command.ExecuteNonQueryAsync();
        }

        private string? _role;
        private Lane? _lane;

        protected string Role => _role!;

        private protected Lane Lane => _lane!;

        protected string AttributesBefore { get; private set; } = string.Empty;

        protected string AttributesAfterCreate { get; private set; } = string.Empty;

        protected string AttributesAfter { get; private set; } = string.Empty;

        protected (bool Exists, uint Oid, bool AllowsConnections) TargetBefore { get; private set; }

        protected (bool Exists, uint Oid, bool AllowsConnections) TargetAfter { get; private set; }

        protected DmsDataStoreCreateResult Created { get; private set; } = null!;

        protected DmsDataStoreDeleteResult Deleted { get; private set; } = null!;

        protected string[] Members { get; private set; } = [];

        protected bool InvokedByCreate { get; private set; }

        /// <summary>Whether the function had run once the delete ended; null when the delete left no database to read.</summary>
        protected bool? InvokedAfterDelete { get; private set; }

        /// <summary>Creates the target database under the lane's name, with the owner the fixture is about.</summary>
        protected abstract Task CreateTargetAsync(string target, string role);

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            _role = await CreateLoginRoleAsync("hostile");
            await ExecuteOnServerAsync($"ALTER ROLE {PostgresqlIdentifiers.Quote(_role)} CREATEDB;");
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await CreateTargetAsync(_lane.Target.Value, _role);
            await PlantAsync(_lane.Target.Value, _role, _lane.Request.ExpectedSourceIdentity);

            AttributesBefore = await AttributesOfAsync(_role);
            TargetBefore = await _lane.TargetCatalogAsync();
            Created = await _lane.CreateAsync();

            // Read before the delete, so a create that ran the function is reported even if the delete then removes it.
            AttributesAfterCreate = await AttributesOfAsync(_role);
            InvokedByCreate = await InvokedAsync(_lane.Target.Value);
            Deleted = await _lane.DeleteAsync();
            TargetAfter = await _lane.TargetCatalogAsync();
            AttributesAfter = await AttributesOfAsync(_role);
            InvokedAfterDelete = TargetAfter.Exists ? await InvokedAsync(_lane.Target.Value) : null;
            Members = [.. _lane.Engine.Calls.Select(call => call.Member)];
        }

        [OneTimeTearDown]
        public async Task Teardown()
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
        public void It_never_runs_the_planted_function() =>
            (InvokedByCreate, InvokedAfterDelete).Should().Be((false, (bool?)false));

        [Test]
        public void It_leaves_the_hostile_roles_attributes_unchanged() =>
            (AttributesBefore, AttributesAfterCreate, AttributesAfter)
                .Should()
                .Be(("false|false|false", "false|false|false", "false|false|false"));

        [Test]
        public void It_stages_fences_renames_and_drops_nothing() =>
            Members.Should().NotContain(_mutatingMembers);

        [Test]
        public void It_leaves_the_target_in_place_and_connectable() =>
            (TargetAfter, TargetBefore.Exists, TargetBefore.AllowsConnections)
                .Should()
                .Be((TargetBefore, true, true));
    }

    /// <summary>The hostile role owns the database itself; inspection refuses it before any session is opened.</summary>
    [TestFixture]
    public class Given_a_database_a_non_superuser_role_owns : PlantedTargetFixture
    {
        protected override Task CreateTargetAsync(string target, string role) =>
            ExecuteOnServerAsync(
                $"CREATE DATABASE {PostgresqlIdentifiers.Quote(target)} OWNER {PostgresqlIdentifiers.Quote(role)};"
            );

        [Test]
        public void It_refuses_the_create_for_inspection_at_the_inspection() =>
            Verdict(Created.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.InspectTargetStep
                    )
                );

        [Test]
        public void It_refuses_the_delete_for_inspection_at_the_inspection() =>
            Verdict(Deleted.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.InspectTargetStep
                    )
                );

        [Test]
        public void It_never_opens_a_session_to_the_database() =>
            Members
                .Should()
                .NotContain([
                    nameof(IDmsTargetEngine.OpenSessionAsync),
                    nameof(IDmsTargetEngine.ReadIdentityAsync),
                    nameof(IDmsTargetEngine.ReadTargetFactsAsync),
                ]);

        [Test]
        public async Task It_reports_the_owner_as_untrusted_on_inspection() =>
            (await Engine().InspectAsync(Lane.Target, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.State.Should()
                .Be(DmsDatabaseState.UntrustedOwner);
    }

    /// <summary>
    /// A superuser owns the database, but the hostile role, granted <c>CREATE</c> on it, created the identity view: the
    /// session opens, and the identity read refuses the relation before selecting from it.
    /// </summary>
    [TestFixture]
    public class Given_a_superuser_database_holding_another_roles_identity_view : PlantedTargetFixture
    {
        protected override async Task CreateTargetAsync(string target, string role)
        {
            await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(target)};");
            await ExecuteOnServerAsync(
                $"GRANT CREATE ON DATABASE {PostgresqlIdentifiers.Quote(target)} TO {PostgresqlIdentifiers.Quote(role)};"
            );
        }

        [Test]
        public void It_refuses_the_create_for_inspection_at_the_identity_read() =>
            Verdict(Created.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_refuses_the_delete_for_inspection_at_the_identity_read() =>
            Verdict(Deleted.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.DeleteIdentityStep
                    )
                );

        [Test]
        public void It_reads_no_facts() =>
            Members.Should().NotContain(nameof(IDmsTargetEngine.ReadTargetFactsAsync));

        [Test]
        public async Task It_reports_the_relation_as_untrusted_without_reading_it()
        {
            PostgresqlDmsTargetEngine engine = Engine();
            DmsInstanceIdentity instance = (await engine.InspectAsync(Lane.Target, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
            DmsSession session = (await engine.OpenSessionAsync(Lane.Target, instance, default))
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Subject.Session;
            await using (session)
            {
                (await engine.ReadIdentityAsync(session, default))
                    .Should()
                    .Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.RelationUntrusted));
            }

            (await InvokedAsync(Lane.Target.Value)).Should().BeFalse();
        }
    }

    /// <summary>
    /// V5-01: a target a superuser owns, with the expected identity, where an ordinary role owns a schema the inherited
    /// <c>search_path</c> names and has planted there a <c>!~(name, name)</c> that counts its calls. The inventory reads
    /// write <c>nspname !~ '^pg_'</c> unqualified, and the literal's unknown type makes the planted operator an exact
    /// match ahead of <c>pg_catalog.!~(name, text)</c>. The control shows an ordinary connection as the disposable
    /// superuser administrator, with the inherited path, running the inventory query runs the operator. The provisioner's
    /// own sessions start on <c>pg_catalog</c> instead, so its reconcile reads the full facts without running it, and the
    /// contaminated inventory is still refused for inspection. The path is inherited from the database, or from the
    /// administrator role; both are disposable, so no shared default changes.
    /// </summary>
    public abstract class PlantedOperatorFixture
    {
        private const string Schema = "dms1438_ops";

        private static readonly string _inheritedPath = $"{Schema}, public";

        private Lane? _lane;
        private string? _hostile;
        private string? _admin;

        protected string InheritedSearchPath { get; private set; } = string.Empty;

        protected bool RanOnTheUnhardenedConnection { get; private set; }

        protected bool ResetBeforeTheProvisioner { get; private set; }

        protected string ProvisionerSearchPath { get; private set; } = string.Empty;

        protected DmsDataStoreCreateResult Reconciled { get; private set; } = null!;

        protected string[] Members { get; private set; } = [];

        protected bool RanDuringTheReconcile { get; private set; }

        /// <summary>Makes <c>search_path</c> name the planted schema for <paramref name="admin"/>'s sessions on <paramref name="target"/>.</summary>
        protected abstract Task InheritSearchPathAsync(string target, string admin, string path);

        private static Task<bool> RanAsync(string target) =>
            new TestDatabase(target).ScalarAsync<bool>($"SELECT is_called FROM \"{Schema}\".\"Calls\";");

        /// <summary>The ordinary role's objects: the call counter, the counting function, and the operator over it.</summary>
        private static Task PlantAsync(string target, string hostile)
        {
            string role = PostgresqlIdentifiers.Quote(hostile);
            return new TestDatabase(target).ExecuteAsync(
                $"CREATE SCHEMA \"{Schema}\" AUTHORIZATION {role};"
                    + $"SET ROLE {role};"
                    + $"CREATE SEQUENCE \"{Schema}\".\"Calls\";"
                    + $"CREATE FUNCTION \"{Schema}\".\"NameNotMatches\"(name, name) RETURNS boolean LANGUAGE plpgsql AS $body$ BEGIN"
                    + $" PERFORM pg_catalog.nextval('\"{Schema}\".\"Calls\"');"
                    + " RETURN pg_catalog.textregexne($1::pg_catalog.text, $2::pg_catalog.text); END $body$;"
                    + $"CREATE OPERATOR \"{Schema}\".!~ (LEFTARG = name, RIGHTARG = name, FUNCTION = \"{Schema}\".\"NameNotMatches\");"
                    + "RESET ROLE;"
            );
        }

        /// <summary>A connection as <paramref name="admin"/> built without the provisioner's factory, so the inherited path applies.</summary>
        private static async Task<(string SearchPath, bool Ran)> RunOnUnhardenedConnectionAsync(
            string target,
            string admin
        )
        {
            NpgsqlConnectionStringBuilder builder = new(AdminConnectionString!)
            {
                Username = admin,
                Password = LoginRolePassword,
                Database = target,
                Pooling = false,
                ApplicationName = "dms1438-test-unhardened",
            };
            await using NpgsqlConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand show = new("SHOW search_path;", connection);
            string path = (string)(await show.ExecuteScalarAsync())!;

            // The production inventory statement; executing it evaluates the filter on every row.
            await using NpgsqlCommand inventory = new(PostgresqlInventoryQueries.SchemaNames, connection);
            await inventory.ExecuteNonQueryAsync();
            return (path, await RanAsync(target));
        }

        private static async Task<string> ProvisionerSearchPathAsync(string target, string admin)
        {
            await using NpgsqlConnection connection = await AdminConnectionAs(admin)
                .OpenAsync(target, PostgresqlAdminConnection.SessionApplicationName, default);
            await using NpgsqlCommand show = new("SHOW search_path;", connection);
            return (string)(await show.ExecuteScalarAsync())!;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await RequireSuperuserOrSkipAsync();
            _hostile = await CreateRoleAsync("operator");
            _admin = await CreateLoginRoleAsync("operator_admin");
            await ExecuteOnServerAsync($"ALTER ROLE {PostgresqlIdentifiers.Quote(_admin)} SUPERUSER;");
            _lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            await _lane.SeedTargetAsync();
            string target = _lane.Target.Value;
            await PlantAsync(target, _hostile);
            await InheritSearchPathAsync(target, _admin, _inheritedPath);

            (InheritedSearchPath, RanOnTheUnhardenedConnection) = await RunOnUnhardenedConnectionAsync(
                target,
                _admin
            );
            await new TestDatabase(target).ExecuteAsync(
                $"SELECT pg_catalog.setval('\"{Schema}\".\"Calls\"', 1, false);"
            );
            ResetBeforeTheProvisioner = !await RanAsync(target);
            ProvisionerSearchPath = await ProvisionerSearchPathAsync(target, _admin);

            Lane.Deployment underAdmin = _lane.WithEngine(EngineAs(_admin));
            Reconciled = await underAdmin.Service.CreateAsync(_lane.Request, default);
            Members = [.. underAdmin.Engine.Calls.Select(call => call.Member)];
            RanDuringTheReconcile = await RanAsync(target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            foreach (string? role in new[] { _admin, _hostile })
            {
                if (role is not null)
                {
                    await DropRoleAsync(role);
                }
            }
        }

        [Test]
        public void It_runs_the_planted_operator_on_a_connection_with_the_inherited_path() =>
            (InheritedSearchPath, RanOnTheUnhardenedConnection, ResetBeforeTheProvisioner)
                .Should()
                .Be((_inheritedPath, true, true));

        [Test]
        public void It_starts_the_provisioners_sessions_on_the_trusted_path() =>
            ProvisionerSearchPath.Should().Be("pg_catalog");

        [Test]
        public void It_reads_the_full_facts_and_changes_nothing() =>
            Members
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.AcquireTargetLockAsync),
                    nameof(IDmsTargetEngine.ReadServerFactsAsync),
                    nameof(IDmsTargetEngine.InspectAsync),
                    nameof(IDmsTargetEngine.OpenSessionAsync),
                    nameof(IDmsTargetEngine.ReadIdentityAsync),
                    nameof(IDmsTargetEngine.ReadTargetFactsAsync)
                );

        [Test]
        public void It_never_runs_the_planted_operator() => RanDuringTheReconcile.Should().BeFalse();

        [Test]
        public void It_refuses_the_contaminated_inventory_for_inspection() =>
            Verdict(Reconciled.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileValidateStep
                            + "."
                            + DmsTargetValidator.InventoryHashCheck
                    )
                );
    }

    /// <summary>V5-01 with the planted schema on the target database's own <c>search_path</c>.</summary>
    [TestFixture]
    public class Given_an_operator_planted_on_the_databases_search_path : PlantedOperatorFixture
    {
        protected override Task InheritSearchPathAsync(string target, string admin, string path) =>
            ExecuteOnServerAsync(
                $"ALTER DATABASE {PostgresqlIdentifiers.Quote(target)} SET search_path = {path};"
            );
    }

    /// <summary>V5-01 with the planted schema on the disposable administrator role's <c>search_path</c>.</summary>
    [TestFixture]
    public class Given_an_operator_planted_on_the_administrators_search_path : PlantedOperatorFixture
    {
        protected override Task InheritSearchPathAsync(string target, string admin, string path) =>
            ExecuteOnServerAsync(
                $"ALTER ROLE {PostgresqlIdentifiers.Quote(admin)} SET search_path = {path};"
            );
    }
}
