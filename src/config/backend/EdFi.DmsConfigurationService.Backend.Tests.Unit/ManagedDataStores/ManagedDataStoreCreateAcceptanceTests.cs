// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_managed_data_store_create_acceptance
{
    private static DmsDataStoreProvisionerReadiness Readiness(string state, string provider = "postgresql")
    {
        DmsDataStoreProvisionerReadiness readiness = new();
        if (state == "Ready")
        {
            readiness.Record(new DmsProvisionerReadiness.Ready(provider, DateTimeOffset.UtcNow));
        }
        else if (state == "Disabled")
        {
            readiness.Record(new DmsProvisionerReadiness.Disabled());
        }
        else if (state == "NotReady")
        {
            readiness.Record(
                new DmsProvisionerReadiness.NotReady([
                    new(DmsProvisioningFailureCode.StartupValidationFailed, "TestProbe"),
                ])
            );
        }
        return readiness;
    }

    private static ManagedDataStoreCreateAcceptance Acceptance(
        IDmsDataStoreProvisionerReadiness readiness,
        IDmsAdminConnectionSource source,
        IDataStoreManageRepository repository,
        ManagedDataStoreEndpointParser? parser = null,
        ProtectedDatabaseNames? protectedNames = null
    ) =>
        new(
            readiness,
            source,
            parser ?? PostgresqlManagedEndpoint.TryParse,
            protectedNames ?? ProtectedDatabaseNames.Create("cms", []),
            repository
        );

    private static DataStoreManageInsertCommand Request() =>
        new() { Name = " Store ", DatabaseTemplate = "Minimal" };

    [TestCase("StartupPending")]
    [TestCase("NotReady")]
    [TestCase("Disabled")]
    public async Task It_refuses_closed_readiness_before_resolution_or_repository_access(string state)
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        (await Acceptance(Readiness(state), source, repository).AcceptAsync(Request(), default))
            .Should()
            .BeOfType<DataStoreManageCreateResult.ProvisioningUnavailable>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [TestCase("", "Minimal")]
    [TestCase("café", "Minimal")]
    [TestCase("Store", "minimal")]
    public async Task It_validates_input_before_the_closed_gate(string name, string template)
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        (
            await Acceptance(Readiness("Disabled"), source, repository)
                .AcceptAsync(new() { Name = name, DatabaseTemplate = template }, default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [TestCase("postgresql", "Host=DB.EXAMPLE;Username=admin;Password=never-persist;")]
    [TestCase("mssql", "Server=tcp:DB.EXAMPLE,1433;User ID=admin;Password=never-persist;")]
    public async Task It_captures_one_resolved_endpoint_and_sends_only_immutable_non_secret_data(
        string provider,
        string connection
    )
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Returns(new DmsAdminConnectionResolution.Resolved(new(connection)));
        ManagedDataStoreCreateCommand? captured = null;
        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .Invokes((ManagedDataStoreCreateCommand command, CancellationToken _) => captured = command)
            .Returns(new DataStoreManageCreateResult.Success(7, "job"));
        ManagedDataStoreEndpointParser parser =
            provider == "postgresql" ? PostgresqlManagedEndpoint.TryParse : MssqlManagedEndpoint.TryParse;
        (
            await Acceptance(Readiness("Ready", provider), source, repository, parser)
                .AcceptAsync(Request(), default)
        )
            .Should()
            .Be(new DataStoreManageCreateResult.Success(7, "job"));
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        captured.Should().NotBeNull();
        captured!.Name.Should().Be("Store");
        captured.TargetName.Value.Should().Be("EdFi_Ods_Store_Minimal");
        captured.ExpectedSourceIdentity.Should().NotBeEmpty();
        captured.Endpoint.Host.Should().Be("db.example");
        captured.Endpoint.Provider.Should().Be(provider);
        captured.Endpoint.CanonicalEncoding.Should().NotContain("never-persist").And.NotContain("admin");
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Returns(new DmsAdminConnectionResolution.Resolved(new("Host=rotated.example")));
        captured.Endpoint.Host.Should().Be("db.example");
    }

    [TestCase("resolution")]
    [TestCase("endpoint")]
    [TestCase("provider")]
    public async Task It_refuses_failed_resolution_or_invalid_endpoint_without_repository_access(
        string failure
    )
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        string connection = failure == "endpoint" ? "Host=a,b" : "Host=db.example";
        DmsAdminConnectionResolution resolution =
            failure == "resolution"
                ? new DmsAdminConnectionResolution.Failed(DmsProvisioningDiagnostic.ForOperation("Resolve"))
                : new DmsAdminConnectionResolution.Resolved(new(connection));
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).Returns(resolution);
        (
            await Acceptance(
                    Readiness("Ready", failure == "provider" ? "mssql" : "postgresql"),
                    source,
                    repository
                )
                .AcceptAsync(Request(), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.ConnectionResolutionFailed>();
        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_protected_generated_targets_before_resolution()
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        (
            await Acceptance(
                    Readiness("Ready"),
                    source,
                    repository,
                    protectedNames: ProtectedDatabaseNames.Create("EdFi_Ods_Store_Minimal", [])
                )
                .AcceptAsync(Request(), default)
        )
            .Should()
            .BeOfType<DataStoreManageCreateResult.InvalidRequest>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_sanitizes_resolution_exceptions_and_propagates_caller_cancellation_without_repository_access(
        bool cancel
    )
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        using CancellationTokenSource cancellation = new();
        Exception failure = cancel
            ? new OperationCanceledException()
            : new InvalidOperationException("sensitive resolver message");
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Invokes(() =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
            })
            .Throws(failure);
        var action = () =>
            Acceptance(Readiness("Ready"), source, repository).AcceptAsync(Request(), cancellation.Token);
        if (cancel)
        {
            (await action.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
        }
        else
        {
            (await action())
                .Should()
                .BeOfType<DataStoreManageCreateResult.FailureUnknown>()
                .Subject.Diagnostic.ToString()
                .Should()
                .NotContain("sensitive resolver message");
        }

        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_does_not_retry_acceptance_after_an_ambiguous_repository_failure()
    {
        var source = A.Fake<IDmsAdminConnectionSource>();
        var repository = A.Fake<IDataStoreManageRepository>();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._))
            .Returns(new DmsAdminConnectionResolution.Resolved(new("Host=db.example")));
        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .Throws(new IOException("commit response lost"));
        var action = () => Acceptance(Readiness("Ready"), source, repository).AcceptAsync(Request(), default);
        (await action()).Should().BeOfType<DataStoreManageCreateResult.FailureUnknown>();
        A.CallTo(() =>
                repository.AcceptCreateAsync(A<ManagedDataStoreCreateCommand>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => source.ResolveAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }
}
