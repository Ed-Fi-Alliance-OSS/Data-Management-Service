// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

public class Given_a_managed_create_handler
{
    private IDataStoreManageRepository _repository = null!;
    private IDmsDataStoreProvisioner _provisioner = null!;
    private CreateManagedDataStoreHandler _handler = null!;
    private JobExecutionContext _execution = null!;
    private ManagedDataStoreCreateOperation _operation = null!;
    private DmsTrustedArtifactPin _pin = null!;
    private List<string> _order = null!;
    private DmsDataStoreCreateResult _physical = null!;
    private ManagedDataStoreRuntimeOptions _runtime = null!;

    [SetUp]
    public void Setup()
    {
        _repository = A.Fake<IDataStoreManageRepository>();
        _provisioner = A.Fake<IDmsDataStoreProvisioner>();
        _order = [];
        var endpoint = new ManagedDataStoreEndpoint(
            "postgresql",
            "tcp",
            "db.example",
            5432,
            null,
            "postgres"
        );
        var target = ManagedDataStoreNameFormatter.Format(
            "Store",
            DmsDatabaseTemplate.Minimal,
            ProtectedDatabaseNames.Create("cms", [])
        );
        _operation = new(
            1,
            new("Store", DmsDatabaseTemplate.Minimal, target, Guid.NewGuid(), endpoint),
            null,
            false
        );
        _pin = new()
        {
            ContractVersion = 1,
            Provider = "postgresql",
            PackageId = "EdFi.Minimal",
            PackageVersion = "1.0",
            PackageSha256 = new('a', 64),
            ArtifactSha256 = new('b', 64),
            TemplateKind = "Minimal",
            DataStandardVersion = "5.2",
            EffectiveSchemaHash = new('c', 64),
            InventorySha256 = new('d', 64),
            ApiSchemaFormatVersion = "1",
            RelationalMappingVersion = "v3",
        };
        _physical = new DmsDataStoreCreateResult.Created(_pin, DmsCleanupReport.Clean);
        _execution = new("job", new TenantContext.NotMultitenant(), 1, 5, A.Fake<IJobFence>());
        _runtime = new()
        {
            RuntimeConnectionString = "Host=db.example;Username=runtime;Password=test-only;Database=ignored",
        };
        var secrets = Options.Create(new SecretsOptions());
        _handler = new(
            _repository,
            _provisioner,
            new(
                Options.Create(_runtime),
                new PostgresqlManagedRuntimeConnectionProvider(),
                new SecretValueCache(secrets),
                secrets,
                TimeProvider.System
            )
        );
        A.CallTo(() => _repository.StartCreateAsync(1, _execution, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _order.Add("start");
                return Task.FromResult(_operation);
            });
        A.CallTo(() => _provisioner.ResolveArtifactAsync(DmsDatabaseTemplate.Minimal, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _order.Add("resolve");
                return Task.FromResult<DmsArtifactResolution>(
                    new DmsArtifactResolution.Resolved(_pin, DmsCleanupReport.Clean)
                );
            });
        A.CallTo(() =>
                _repository.PinCreateAsync(
                    A<ManagedDataStoreCreateOperation>._,
                    _pin,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                _order.Add("pin");
                _operation = _operation with { PinnedArtifact = _pin };
                return Task.FromResult(_operation);
            });
        A.CallTo(() =>
                _repository.AdmitCreateAsync(
                    A<ManagedDataStoreCreateOperation>._,
                    endpoint,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => _order.Add("admit"));
        A.CallTo(() =>
                _provisioner.CreateAsync(
                    A<DmsDataStoreCreateRequest>._,
                    endpoint,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreCreateRequest request,
                    ManagedDataStoreEndpoint actual,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> authorize,
                    CancellationToken token
                ) =>
                {
                    request.PinnedArtifact.Should().Be(_pin);
                    request.ExpectedSourceIdentity.Should().Be(_operation.Identity.ExpectedSourceIdentity);
                    _order.Add("lock");
                    await authorize(actual, token);
                    _order.Add("physical");
                    return _physical;
                }
            );
        A.CallTo(() =>
                _repository.PublishCreateAsync(
                    A<ManagedDataStorePublishCommand>._,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .Invokes(
                (ManagedDataStorePublishCommand command, JobExecutionContext _, CancellationToken _) =>
                {
                    _order.Add("publish");
                    command.PinnedArtifact.Should().Be(_pin);
                    var connection = new Npgsql.NpgsqlConnectionStringBuilder(
                        command.RuntimeConnectionString
                    );
                    connection.Database.Should().Be(target.Value);
                    connection.Username.Should().Be("runtime");
                }
            )
            .Returns(7);
    }

    private Task Run() => _handler.ExecuteAsync(_execution, new(1), default);

    [
        TestCase(DmsProvisioningFailureCode.ArtifactUntrusted),
        TestCase(DmsProvisioningFailureCode.StagingFailed)
    ]
    public async Task It_stops_before_pinning_or_physical_work_when_resolution_fails(
        DmsProvisioningFailureCode code
    )
    {
        var failure = DmsProvisioningFailure.Create(
            code,
            DmsProvisioningDiagnostic.ForOperation("ResolveArtifact")
        );
        A.CallTo(() => _provisioner.ResolveArtifactAsync(DmsDatabaseTemplate.Minimal, A<CancellationToken>._))
            .Returns(new DmsArtifactResolution.Failure(failure, DmsCleanupReport.Clean));
        var exception = await ((Func<Task>)Run).Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        exception.Which.Failure.Should().Be(failure);
        _order.Should().Equal("start");
    }

    [TestCase(false), TestCase(true)]
    public async Task It_propagates_cancellation_or_unavailable_fence_during_admission(bool canceled)
    {
        Exception expected = canceled ? new OperationCanceledException() : new JobFenceUnavailableException();
        A.CallTo(() =>
                _repository.AdmitCreateAsync(
                    A<ManagedDataStoreCreateOperation>._,
                    A<ManagedDataStoreEndpoint>._,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .Throws(expected);
        var exception = await ((Func<Task>)Run).Should().ThrowAsync<Exception>();
        exception.Which.Should().BeSameAs(expected);
        _order.Should().NotContain("physical").And.NotContain("publish");
    }

    [TestCase("postgresql"), TestCase("mssql")]
    public void It_restores_only_the_canonical_endpoint_encoding(string provider)
    {
        var endpoint = new ManagedDataStoreEndpoint(
            provider,
            "tcp",
            "db.example",
            provider == "mssql" ? 1433 : 5432,
            null,
            provider == "mssql" ? "master" : "postgres"
        );
        ManagedDataStoreEndpoint.FromCanonicalEncoding(endpoint.CanonicalEncoding).Should().Be(endpoint);
        var bad = () =>
            ManagedDataStoreEndpoint.FromCanonicalEncoding(
                endpoint.CanonicalEncoding.Replace("db.example", "DB.EXAMPLE")
            );
        bad.Should().Throw<ArgumentException>();
    }

    [TestCase(false), TestCase(true)]
    public async Task It_orders_pin_admission_and_publication_for_create_or_reconciliation(bool reconciled)
    {
        if (reconciled)
        {
            _physical = new DmsDataStoreCreateResult.Reconciled(_pin, DmsCleanupReport.Clean);
        }
        await Run();
        _order.Should().Equal("start", "resolve", "pin", "lock", "admit", "physical", "publish");
        A.CallTo(() => _provisioner.CreateAsync(A<DmsDataStoreCreateRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reuses_a_persisted_pin_without_resolving_again()
    {
        _operation = _operation with { PinnedArtifact = _pin };
        await Run();
        _order.Should().Equal("start", "lock", "admit", "physical", "publish");
    }

    [Test]
    public async Task It_returns_after_verified_created_replay_without_resolving_secrets()
    {
        _operation = _operation with { AlreadyCreated = true, PinnedArtifact = _pin };
        _runtime.RuntimeConnectionString = null;
        await Run();
        _order.Should().Equal("start");
    }

    [TestCase("pin"), TestCase("admit"), TestCase("publish")]
    public async Task It_propagates_fence_loss_without_compensation_or_success(string point)
    {
        if (point == "pin")
        {
            A.CallTo(() =>
                    _repository.PinCreateAsync(
                        A<ManagedDataStoreCreateOperation>._,
                        _pin,
                        _execution,
                        A<CancellationToken>._
                    )
                )
                .Throws(new JobLeaseLostException());
        }
        if (point == "admit")
        {
            A.CallTo(() =>
                    _repository.AdmitCreateAsync(
                        A<ManagedDataStoreCreateOperation>._,
                        A<ManagedDataStoreEndpoint>._,
                        _execution,
                        A<CancellationToken>._
                    )
                )
                .Throws(new JobLeaseLostException());
        }
        if (point == "publish")
        {
            A.CallTo(() =>
                    _repository.PublishCreateAsync(
                        A<ManagedDataStorePublishCommand>._,
                        _execution,
                        A<CancellationToken>._
                    )
                )
                .Throws(new JobLeaseLostException());
        }
        await ((Func<Task>)Run).Should().ThrowAsync<JobLeaseLostException>();
        if (point != "publish")
        {
            _order.Should().NotContain("physical");
        }
        A.CallTo(() => _provisioner.DeleteAsync(A<DmsDataStoreDeleteRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [
        TestCase(DmsProvisioningFailureCode.LockTimeout),
        TestCase(DmsProvisioningFailureCode.ArtifactChangedSinceCreate),
        TestCase(DmsProvisioningFailureCode.StartupValidationFailed),
        TestCase(DmsProvisioningFailureCode.AdminPrivilegeMissing),
        TestCase(DmsProvisioningFailureCode.InvalidTargetName)
    ]
    public async Task It_signals_the_typed_physical_failure_without_publication(
        DmsProvisioningFailureCode code
    )
    {
        var failure = DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation("Create"));
        _physical = new DmsDataStoreCreateResult.Failure(failure, DmsCleanupReport.Clean);
        var exception = await ((Func<Task>)Run).Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        exception.Which.Failure.Should().Be(failure);
        _order.Should().NotContain("publish");
    }

    [Test]
    public async Task It_refuses_a_different_pin_in_a_successful_physical_result()
    {
        _physical = new DmsDataStoreCreateResult.Created(
            _pin with
            {
                PackageVersion = "changed",
            },
            DmsCleanupReport.Clean
        );
        var failure = await ((Func<Task>)Run).Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        failure.Which.Failure.Code.Should().Be(DmsProvisioningFailureCode.ArtifactChangedSinceCreate);
        _order.Should().NotContain("publish");
    }

    [
        TestCase("Host=changed.example", DmsProvisioningFailureCode.TargetEndpointChanged),
        TestCase("Host=db.example,other.example", DmsProvisioningFailureCode.TargetEndpointUnverifiable),
        TestCase(null, DmsProvisioningFailureCode.TargetEndpointUnverifiable)
    ]
    public async Task It_refuses_an_invalid_runtime_connection_before_physical_create(
        string? runtime,
        DmsProvisioningFailureCode code
    )
    {
        _runtime.RuntimeConnectionString = runtime;
        var exception = await ((Func<Task>)Run).Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        exception.Which.Failure.Code.Should().Be(code);
        // No target lock, admission, physical create or publication.
        _order.Should().Equal("start", "resolve", "pin");
    }

    [Test]
    public async Task It_publishes_the_runtime_snapshot_captured_before_physical_create()
    {
        string expected = new Npgsql.NpgsqlConnectionStringBuilder(_runtime.RuntimeConnectionString)
        {
            Database = _operation.Identity.TargetName.Value,
        }.ConnectionString;
        string? published = null;
        // Rotate the runtime endpoint while the physical operation holds its admission.
        A.CallTo(() =>
                _repository.AdmitCreateAsync(
                    A<ManagedDataStoreCreateOperation>._,
                    A<ManagedDataStoreEndpoint>._,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .Invokes(() =>
            {
                _order.Add("admit");
                _runtime.RuntimeConnectionString =
                    "Host=changed.example;Username=rotated;Password=test-only;Database=ignored";
            });
        A.CallTo(() =>
                _repository.PublishCreateAsync(
                    A<ManagedDataStorePublishCommand>._,
                    _execution,
                    A<CancellationToken>._
                )
            )
            .Invokes(
                (ManagedDataStorePublishCommand command, JobExecutionContext _, CancellationToken _) =>
                {
                    _order.Add("publish");
                    published = command.RuntimeConnectionString;
                }
            )
            .Returns(7);
        await Run();
        _order.Should().Equal("start", "resolve", "pin", "lock", "admit", "physical", "publish");
        (published == expected)
            .Should()
            .BeTrue("publication must use the snapshot validated before physical work");
    }

    [
        TestCase(DmsProvisioningFailureCode.LockTimeout, 1, true),
        TestCase(DmsProvisioningFailureCode.LockTimeout, 5, false),
        TestCase(DmsProvisioningFailureCode.TargetUnowned, 1, false),
        TestCase(DmsProvisioningFailureCode.StartupValidationFailed, 1, false),
        TestCase(DmsProvisioningFailureCode.TargetEndpointChanged, 1, false),
        TestCase(DmsProvisioningFailureCode.SecretResolutionFailed, 1, true)
    ]
    public async Task It_routes_typed_failure_through_executor_atomic_settlement(
        DmsProvisioningFailureCode code,
        int attempt,
        bool retry
    )
    {
        using var harness = new ExecutorHarness();
        var failure = DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation("Create"));
        harness.Script.Run = (_, _, _) => throw new ManagedDataStoreProvisioningException(failure);
        var result = await harness.Executor.ExecuteAsync(ExecutorHarness.Job(attemptCount: attempt), default);
        result.Outcome.Should().Be(retry ? JobExecutionOutcome.RetryScheduled : JobExecutionOutcome.Failed);
        harness.Leases.OutcomeWrites.Should().Equal("FailManaged");
        harness.Leases.ManagedBackoff.HasValue.Should().Be(retry);
        if (attempt == 5)
        {
            harness.Leases.LastManagedFailure!.JobCode.Should().Be(JobErrorCode.AttemptsExhausted.Code);
        }
        else
        {
            harness.Leases.LastManagedFailure!.ProvisioningCode.Should().Be(code);
        }
    }
}
