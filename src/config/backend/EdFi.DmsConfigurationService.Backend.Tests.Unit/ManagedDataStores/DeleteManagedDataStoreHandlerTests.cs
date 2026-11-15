// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

public class Given_a_managed_delete_handler
{
    private IDataStoreManageRepository _repository = null!;
    private IDmsDataStoreProvisioner _provisioner = null!;
    private DeleteManagedDataStoreHandler _handler = null!;
    private ManagedDataStoreDeleteOperation _operation = null!;
    private JobExecutionContext _execution = null!;
    private List<string> _order = null!;
    private DmsDataStoreDeleteResult _physical = null!;

    [SetUp]
    public void Setup()
    {
        _repository = A.Fake<IDataStoreManageRepository>();
        _provisioner = A.Fake<IDmsDataStoreProvisioner>();
        _handler = new(_repository, _provisioner);
        _execution = new("delete-job", new TenantContext.NotMultitenant(), 1, 5, A.Fake<IJobFence>());
        _order = [];
        var target = ManagedDataStoreNameFormatter.Format(
            "Store",
            DmsDatabaseTemplate.Minimal,
            ProtectedDatabaseNames.Create("cms", [])
        );
        _operation = new(
            new(
                1,
                new(
                    "Store",
                    DmsDatabaseTemplate.Minimal,
                    target,
                    Guid.NewGuid(),
                    new("mssql", "tcp", "db.example", 1433, null, "master")
                ),
                new DmsTrustedArtifactPin
                {
                    ContractVersion = 1,
                    Provider = "mssql",
                    PackageId = "EdFi.Minimal",
                    PackageVersion = "1",
                    PackageSha256 = new('a', 64),
                    ArtifactSha256 = new('b', 64),
                    TemplateKind = "Minimal",
                    DataStandardVersion = "5.2",
                    EffectiveSchemaHash = new('c', 64),
                    InventorySha256 = new('d', 64),
                    ApiSchemaFormatVersion = "1",
                    RelationalMappingVersion = "v3",
                },
                7,
                "create-job"
            ),
            false
        );
        _physical = new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean);
        A.CallTo(() => _repository.StartDeleteAsync(1, A<JobExecutionContext>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _order.Add("start");
                return Task.FromResult(_operation);
            });
        A.CallTo(() =>
                _repository.AdmitDeleteAsync(
                    A<ManagedDataStoreDeleteOperation>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<JobExecutionContext>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => _order.Add("admit"));
        A.CallTo(() =>
                _provisioner.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                async (
                    DmsDataStoreDeleteRequest request,
                    ManagedDataStoreEndpoint endpoint,
                    Func<ManagedDataStoreEndpoint, CancellationToken, Task> admit,
                    CancellationToken token
                ) =>
                {
                    request.TargetName.Should().Be(_operation.Command.Identity.TargetName);
                    request
                        .ExpectedSourceIdentity.Should()
                        .Be(_operation.Command.Identity.ExpectedSourceIdentity);
                    endpoint.Should().Be(_operation.Command.Identity.Endpoint);
                    _order.Add("lock");
                    await admit(endpoint, token);
                    _order.Add("physical");
                    return _physical;
                }
            );
        A.CallTo(() =>
                _repository.CompleteDeleteAsync(
                    _operation.Command,
                    A<JobExecutionContext>._,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => _order.Add("finalize"));
    }

    private Task Run() => _handler.ExecuteAsync(_execution, new(1), default);

    [TestCase(false), TestCase(true)]
    public async Task It_admits_before_drop_or_absence_and_finalizes_only_after_success(bool absent)
    {
        if (absent)
        {
            _physical = new DmsDataStoreDeleteResult.AlreadyAbsent();
        }
        await Run();
        _order.Should().Equal("start", "lock", "admit", "physical", "finalize");
        A.CallTo(() => _provisioner.DeleteAsync(A<DmsDataStoreDeleteRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_replays_deleted_without_any_physical_work()
    {
        _operation = _operation with { AlreadyDeleted = true };
        await Run();
        _order.Should().Equal("start");
    }

    [TestCase(DmsProvisioningFailureCode.TargetUnowned)]
    [TestCase(DmsProvisioningFailureCode.TargetEndpointChanged)]
    [TestCase(DmsProvisioningFailureCode.TargetEndpointUnverifiable)]
    [TestCase(DmsProvisioningFailureCode.LockTimeout)]
    [TestCase(DmsProvisioningFailureCode.StartupValidationFailed)]
    public async Task It_preserves_the_catalog_on_typed_failure(DmsProvisioningFailureCode code)
    {
        var failure = DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation("Delete"));
        _physical = new DmsDataStoreDeleteResult.Failure(failure, DmsCleanupReport.Clean);
        var thrown = await ((Func<Task>)Run).Should().ThrowAsync<ManagedDataStoreProvisioningException>();
        thrown.Which.Failure.Should().Be(failure);
        _order.Should().NotContain("finalize");
    }

    [TestCase("lease"), TestCase("unavailable"), TestCase("cancel")]
    public async Task It_aborts_before_physical_work_when_admission_cannot_commit(string reason)
    {
        Exception failure = reason switch
        {
            "lease" => new JobLeaseLostException(),
            "unavailable" => new JobFenceUnavailableException(),
            _ => new OperationCanceledException(),
        };
        A.CallTo(() =>
                _repository.AdmitDeleteAsync(
                    A<ManagedDataStoreDeleteOperation>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<JobExecutionContext>._,
                    A<CancellationToken>._
                )
            )
            .Throws(failure);
        var thrown = await ((Func<Task>)Run).Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(failure);
        _order.Should().NotContain("physical").And.NotContain("finalize");
    }

    [Test]
    public async Task It_does_not_treat_connection_exceptions_as_absence()
    {
        A.CallTo(() =>
                _provisioner.DeleteAsync(
                    A<DmsDataStoreDeleteRequest>._,
                    A<ManagedDataStoreEndpoint>._,
                    A<Func<ManagedDataStoreEndpoint, CancellationToken, Task>>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new IOException("test unavailable"));
        await ((Func<Task>)Run).Should().ThrowAsync<IOException>();
        _order.Should().NotContain("finalize");
    }

    [Test]
    public async Task It_retries_after_physical_success_without_compensating_creation()
    {
        A.CallTo(() =>
                _repository.CompleteDeleteAsync(
                    _operation.Command,
                    A<JobExecutionContext>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new JobLeaseLostException())
            .Once()
            .Then.Returns(Task.CompletedTask);
        await ((Func<Task>)Run).Should().ThrowAsync<JobLeaseLostException>();
        _physical = new DmsDataStoreDeleteResult.AlreadyAbsent();
        await Run();
        A.CallTo(() => _provisioner.CreateAsync(A<DmsDataStoreCreateRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                _repository.CompleteDeleteAsync(
                    _operation.Command,
                    A<JobExecutionContext>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_finishes_one_atomic_outcome_if_shutdown_arrives_during_settlement()
    {
        using var harness = new ExecutorHarness();
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _physical = new DmsDataStoreDeleteResult.Failure(
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.LockTimeout,
                DmsProvisioningDiagnostic.ForOperation("Delete")
            ),
            DmsCleanupReport.Clean
        );
        harness.Script.Run = (context, _, token) => _handler.ExecuteAsync(context, new(1), token);
        harness.Leases.OutcomeResult = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return new JobWriteResult.Success(null, DateTime.UtcNow);
        };
        Task<JobExecutionResult> running = harness.Executor.ExecuteAsync(
            ExecutorHarness.Job(),
            stopping.Token
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stopping.CancelAsync();
        }
        finally
        {
            release.TrySetResult();
        }
        (await running.WaitAsync(TimeSpan.FromSeconds(10)))
            .Outcome.Should()
            .Be(JobExecutionOutcome.RetryScheduled);
        harness.Leases.OutcomeWrites.Should().Equal("FailManaged");
        harness
            .Leases.LastManagedFailure!.ProvisioningCode.Should()
            .Be(DmsProvisioningFailureCode.LockTimeout);
        _order.Should().NotContain("finalize");
    }
}
