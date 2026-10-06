// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Category("CdcMessageContract")]
[Property("CdcInvariant", "CDC-INV-10")]
public sealed class Given_MessageContractProviderFence_WithCancellation(CdcProvider provider)
{
    private MessageContractProviderObserver _observer = null!;
    private CdcDeploymentRequest _request = null!;
    private ICdcConnectTransport _connect = null!;

    [SetUp]
    public void Setup()
    {
        _request = CdcDeploymentRequestTestData.Request(provider);
        _connect = A.Fake<ICdcConnectTransport>();
        _observer = new(
            _request.Binding,
            () =>
            {
                var connection = A.Fake<DbConnection>();
                var command = A.Fake<DbCommand>();
                var reader = A.Fake<DbDataReader>();
                A.CallTo(connection)
                    .WithReturnType<DbCommand>()
                    .Where(call => call.Method.Name == "CreateDbCommand")
                    .Returns(command);
                A.CallTo(() => command.ExecuteScalarAsync(A<CancellationToken>._))
                    .Returns(provider == CdcProvider.Postgresql ? "0/1" : "1");
                A.CallTo(() => command.ExecuteNonQueryAsync(A<CancellationToken>._)).Returns(1);
                A.CallTo(command)
                    .WithReturnType<Task<DbDataReader>>()
                    .Where(call => call.Method.Name == "ExecuteDbDataReaderAsync")
                    .Returns(Task.FromResult(reader));
                A.CallTo(() => reader.ReadAsync(A<CancellationToken>._)).Returns(true);
                A.CallTo(() => reader[0]).Returns(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
                A.CallTo(() => reader[1]).Returns(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 });
                return connection;
            }
        );
    }

    [Test]
    public async Task It_reports_the_phase_and_barrier_when_the_internal_timeout_expires()
    {
        A.CallTo(() => _connect.ReadStatusAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, call.GetArgument<CancellationToken>(1));
                throw new InvalidOperationException("The status read must be canceled.");
            });
        var fences = new MessageContractProviderFences(
            _observer,
            _request,
            _connect,
            TimeSpan.FromSeconds(1)
        );
        Func<Task> fence = () => FenceAsync(fences, CancellationToken.None);

        await fence
            .Should()
            .ThrowAsync<AssertionException>()
            .WithMessage(
                provider == CdcProvider.Postgresql
                    ? "PostgreSQL timeout-phase committed source fence timed out; WAL barrier=1. Details redacted."
                    : "SQL Server timeout-phase committed source fence timed out; barrier=00000000:00000000:0001/00000000:00000000:0002/2. Details redacted."
            );
    }

    [Test]
    public async Task It_preserves_caller_cancellation_instead_of_reporting_a_fence_timeout()
    {
        using var caller = new CancellationTokenSource();
        A.CallTo(() => _connect.ReadStatusAsync(_request, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                caller.Cancel();
                return Task.FromCanceled<CdcTransportResult<CdcConnectStatus>>(
                    call.GetArgument<CancellationToken>(1)
                );
            });
        var fences = new MessageContractProviderFences(_observer, _request, _connect);
        Func<Task> fence = () => FenceAsync(fences, caller.Token);

        await fence.Should().ThrowAsync<OperationCanceledException>();
    }

    private Task FenceAsync(MessageContractProviderFences fences, CancellationToken token) =>
        provider == CdcProvider.Postgresql
            ? fences.FencePostgresqlSourceAsync("timeout-phase", token)
            : fences.FenceSqlServerSourceAsync("timeout-phase", token);
}
