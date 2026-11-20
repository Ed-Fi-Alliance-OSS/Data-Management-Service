// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsDataStoreProvisioningEndpointTests
{
    private static ManagedDataStoreEndpoint EndpointFor(string provider) =>
        provider == "postgresql"
            ? new("postgresql", "tcp", "a", 5432, null, "postgres")
            : new("mssql", "tcp", "a", 1433, null, "master");

    private static ManagedDataStoreEndpoint? Parse(string value, string provider)
    {
        if (provider == "postgresql")
        {
            return PostgresqlManagedEndpoint.TryParse(value, out var postgres) ? postgres : null;
        }
        return MssqlManagedEndpoint.TryParse(value, out var mssql) ? mssql : null;
    }

    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_guarded_operation(string provider)
    {
        private string ConnectionFor(string connection) =>
            provider == "postgresql"
                ? connection
                : connection
                    .Replace("Host=", "Server=tcp:", StringComparison.Ordinal)
                    .Replace("Username=", "User ID=", StringComparison.Ordinal);

        private ManagedDataStoreEndpoint Expected => EndpointFor(provider);

        private Harness Guarded(string connection = "Host=a;Username=admin;Password=rotated")
        {
            Harness harness = new();
            harness.AdminConnection.Result = new DmsAdminConnectionResolution.Resolved(
                new(ConnectionFor(connection))
            );
            harness.Engine.Provider = provider;
            harness.Engine.OnParseManagedEndpoint = value => Parse(value, provider);
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = EndpointFor(provider),
                };
            harness.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            return harness;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_admits_once_with_the_actual_holder_before_any_target_work(bool create)
        {
            Harness harness = Guarded();
            ManagedDataStoreEndpoint actual = Expected with { };
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = actual,
                };
            int admissions = 0;
            Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token)
            {
                endpoint.Should().BeSameAs(actual);
                harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
                harness.OnlyHolder.ReleaseCount.Should().Be(0);
                token.IsCancellationRequested.Should().BeFalse();
                admissions++;
                return Task.CompletedTask;
            }
            if (create)
            {
                await harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, default);
            }
            else
            {
                (await harness.Service.DeleteAsync(DeleteRequest(), Expected, Authorize, default))
                    .Should()
                    .BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();
            }
            admissions.Should().Be(1);
            harness.OnlyHolder.ReleaseCount.Should().Be(1);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_preserves_admission_exceptions_and_cleans_up(bool create)
        {
            foreach (
                Exception exception in new Exception[]
                {
                    new JobLeaseLostException(),
                    new JobFenceUnavailableException(),
                    new OperationCanceledException(),
                    new InvalidOperationException("CMS unavailable"),
                    new DmsProvisioningFailedException(
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.Unknown,
                            DmsProvisioningDiagnostic.ForOperation("CallerAdmission")
                        )
                    ),
                }
            )
            {
                Harness harness = Guarded();
                Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token) =>
                    Task.FromException(exception);
                Func<Task> action = async () =>
                {
                    if (create)
                    {
                        await harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, default);
                    }
                    else
                    {
                        await harness.Service.DeleteAsync(DeleteRequest(), Expected, Authorize, default);
                    }
                };
                (await action.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(exception);
                harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
                harness.OnlyHolder.ReleaseCount.Should().Be(1);
                SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_stops_after_holder_loss_during_admission_even_if_callback_ignores_cancellation(
            bool create
        )
        {
            Harness harness = Guarded();
            Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token)
            {
                harness.OnlyHolder.Lose();
                token.IsCancellationRequested.Should().BeTrue();
                return Task.CompletedTask;
            }
            DmsProvisioningFailure failure = create
                ? FailureOf(await harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, default))
                : FailureOf(await harness.Service.DeleteAsync(DeleteRequest(), Expected, Authorize, default));
            failure.Code.Should().Be(DmsProvisioningFailureCode.LockLost);
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [Test]
        public async Task It_preserves_the_callbacks_cancellation_when_holder_loss_coincides()
        {
            Harness harness = Guarded();
            OperationCanceledException exception = new();
            Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token)
            {
                harness.OnlyHolder.Lose();
                return Task.FromException(exception);
            }
            Func<Task> action = () =>
                harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, default);
            (await action.Should().ThrowAsync<OperationCanceledException>())
                .Which.Should()
                .BeSameAs(exception);
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_does_not_admit_when_lock_acquisition_times_out(bool create)
        {
            Harness harness = Guarded();
            harness.Engine.OnAcquireTargetLock = _ =>
                Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Timeout());
            Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token) =>
                throw new AssertionException("Admission requires an acquired lock.");
            var failure = create
                ? FailureOf(await harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, default))
                : FailureOf(await harness.Service.DeleteAsync(DeleteRequest(), Expected, Authorize, default));
            failure.Code.Should().Be(DmsProvisioningFailureCode.LockTimeout);
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_stops_caller_cancellation_even_when_admission_returns_normally(bool create)
        {
            Harness harness = Guarded();
            using CancellationTokenSource caller = new();
            Task Authorize(ManagedDataStoreEndpoint endpoint, CancellationToken token)
            {
                caller.Cancel();
                token.IsCancellationRequested.Should().BeTrue();
                return Task.CompletedTask;
            }
            Func<Task> action = async () =>
            {
                if (create)
                {
                    await harness.Service.CreateAsync(CreateRequest(), Expected, Authorize, caller.Token);
                }
                else
                {
                    await harness.Service.DeleteAsync(DeleteRequest(), Expected, Authorize, caller.Token);
                }
            };
            await action.Should().ThrowAsync<OperationCanceledException>();
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [Test]
        public async Task It_does_not_admit_an_unverified_holder()
        {
            Harness harness = Guarded();
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = Expected with { Host = "b" },
                };
            int admissions = 0;
            var result = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) =>
                {
                    admissions++;
                    return Task.CompletedTask;
                },
                default
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.TargetEndpointChanged);
            admissions.Should().Be(0);
        }

        [TestCase("Host=b;Username=admin", DmsProvisioningFailureCode.TargetEndpointChanged)]
        [TestCase("Host=a,b;Username=admin", DmsProvisioningFailureCode.TargetEndpointUnverifiable)]
        [TestCase("not a connection", DmsProvisioningFailureCode.TargetEndpointUnverifiable)]
        public async Task It_refuses_rotation_or_unverifiable_settings_before_any_target_work(
            string connection,
            DmsProvisioningFailureCode code
        )
        {
            Harness harness = Guarded(connection);
            var create = await harness.Service.CreateAsync(
                CreateRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            var delete = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(create).Code.Should().Be(code);
            FailureOf(delete).Code.Should().Be(code);
            FailureOf(delete).Category.Should().Be(DmsProvisioningFailureCategory.OperatorInspectionRequired);
            harness.Engine.Calls.Should().BeEmpty();
            harness.Catalog.Calls.Should().Be(0);
            harness.AdminConnection.Resolutions.Should().Be(2);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_refuses_a_different_or_unverifiable_holder_before_already_absent(bool missing)
        {
            Harness harness = Guarded();
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = missing ? null : Expected with { Host = "b" },
                };
            var result = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(result)
                .Code.Should()
                .Be(
                    missing
                        ? DmsProvisioningFailureCode.TargetEndpointUnverifiable
                        : DmsProvisioningFailureCode.TargetEndpointChanged
                );
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            harness.OnlyHolder.ReleaseCount.Should().Be(1);
        }

        [Test]
        public async Task It_releases_the_staged_artifact_when_the_create_holder_has_another_endpoint()
        {
            Harness harness = Guarded();
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = Expected with { Host = "b" },
                };
            var result = await harness.Service.CreateAsync(
                CreateRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.TargetEndpointChanged);
            harness.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            harness.OnlyHolder.ReleaseCount.Should().Be(1);
            SharedTemplateCatalog.Workspace.OperationEntries.Should().BeEmpty();
        }

        [Test]
        public async Task It_refuses_an_invalid_persisted_descriptor()
        {
            Harness harness = Guarded();
            var result = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected with
                {
                    MaintenanceDatabase = "other",
                },
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
            harness.Engine.Calls.Should().BeEmpty();
        }

        [Test]
        public async Task It_allows_credentials_to_rotate_and_resolves_only_once()
        {
            Harness harness = Guarded();
            var result = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            result.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();
            harness.AdminConnection.Resolutions.Should().Be(1);
        }

        [Test]
        public async Task It_classifies_an_unusable_resolution_as_unverifiable_only_for_guarded_calls()
        {
            Harness harness = Guarded();
            harness.AdminConnection.Result = new DmsAdminConnectionResolution.Failed(
                DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.UnusableValueStep)
            );
            var create = await harness.Service.CreateAsync(
                CreateRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            var delete = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(create).Code.Should().Be(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
            FailureOf(delete).Code.Should().Be(DmsProvisioningFailureCode.TargetEndpointUnverifiable);
            harness.Engine.Calls.Should().BeEmpty();
            var standalone = await harness.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);
            FailureOf(standalone).Code.Should().Be(DmsProvisioningFailureCode.SecretResolutionFailed);
        }

        [Test]
        public async Task It_preserves_transient_secret_resolution_failure()
        {
            Harness harness = Guarded();
            harness.AdminConnection.Result = new DmsAdminConnectionResolution.Failed(
                DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.TimedOutStep)
            );
            var result = await harness.Service.DeleteAsync(
                DeleteRequest(),
                Expected,
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.SecretResolutionFailed);
            FailureOf(result).Category.Should().Be(DmsProvisioningFailureCategory.Transient);
            harness.Engine.Calls.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_rotation_during_create
    {
        [Test]
        public async Task It_keeps_the_bound_snapshot_after_rotation_before_lock_and_during_cleanup()
        {
            string original = "Host=a;Username=admin;Password=first";
            DmsDataStoreProvisioningOptions options = new() { AdminConnectionString = original };
            SecretsOptions secrets = new();
            DmsAdminConnectionSource source = new(
                Options.Create(options),
                new SecretValueCache(Options.Create(secrets)),
                Options.Create(secrets),
                TimeProvider.System
            );
            Harness harness = new(adminConnection: source);
            harness.Engine.OnParseManagedEndpoint = value => Parse(value, "postgresql");
            var acquire = harness.Engine.OnAcquireTargetLock;
            harness.Engine.OnAcquireTargetLock = async token =>
                ((DmsTargetLockResult.Acquired)await acquire(token)) with
                {
                    Endpoint = EndpointFor("postgresql"),
                };
            var stage = harness.Catalog.OnStage;
            harness.Catalog.OnStage = (template, operation, token) =>
            {
                options.AdminConnectionString = "Host=b;Username=admin;Password=second";
                return stage(template, operation, token);
            };
            List<string> observed = [];
            harness.Engine.OnCall = _ => observed.Add(source.Current);
            harness.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            harness.Engine.OnCreateStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(
                        new DmsInstanceIdentity.Postgresql(4242, "claim")
                    )
                );
            harness.Engine.OnRestoreIntoStaging = (_, _, _) =>
                Task.FromResult<DmsCommandOutcome<DmsInstanceIdentity>>(
                    new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                        new DmsCommandFailure(
                            DmsCommandFailureClass.Rejected,
                            DmsProvisioningDiagnostic.ForOperation("Restore")
                        )
                    )
                );
            harness.Engine.OnDropStaging = (_, _, _) =>
            {
                options.AdminConnectionString = "Host=c;Username=admin;Password=third";
                observed.Add(source.Current);
                return Task.FromResult<DmsCommandOutcome>(new DmsCommandOutcome.Succeeded());
            };
            var result = await harness.Service.CreateAsync(
                CreateRequest(),
                EndpointFor("postgresql"),
                (_, _) => Task.CompletedTask,
                CancellationToken.None
            );
            FailureOf(result).Code.Should().Be(DmsProvisioningFailureCode.RestoreFailed);
            harness.Engine.Members.Should().Contain(SpyDmsTargetEngine.DropStaging);
            observed.Should().OnlyContain(value => value == "Host=a;Username=admin;Password=first");
            Action readAfterOperation = () => _ = source.Current;
            readAfterOperation.Should().Throw<InvalidOperationException>();
        }
    }
}
