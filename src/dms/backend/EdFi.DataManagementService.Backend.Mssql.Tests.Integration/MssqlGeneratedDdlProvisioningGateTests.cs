// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningGate
{
    [Test]
    public void It_preserves_the_existing_configuration_parser()
    {
        new MssqlGeneratedDdlProvisioningGate("").MaximumConcurrency.Should().Be(0);
        new MssqlGeneratedDdlProvisioningGate(" \t ").MaximumConcurrency.Should().Be(0);
        new MssqlGeneratedDdlProvisioningGate("1").MaximumConcurrency.Should().Be(1);
        new MssqlGeneratedDdlProvisioningGate("0002").MaximumConcurrency.Should().Be(2);
        new MssqlGeneratedDdlProvisioningGate("2147483647").MaximumConcurrency.Should().Be(int.MaxValue);

        foreach (var invalidValue in new[] { "0", "-1", "+2", " 2", "2 ", "x", "2147483648" })
        {
            Action act = () => new MssqlGeneratedDdlProvisioningGate(invalidValue);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage(
                    "*MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY*must be a positive integer when set."
                );
        }
    }

    [Test]
    public void It_captures_configuration_once_including_initialization_failure()
    {
        var configuredValue = "2";
        var readCount = 0;
        var lazyGate = MssqlGeneratedDdlProvisioningGate.CreateLazy(() =>
        {
            readCount++;
            return configuredValue;
        });

        var first = lazyGate.Value;
        configuredValue = "1";
        var second = lazyGate.Value;

        second.Should().BeSameAs(first);
        second.MaximumConcurrency.Should().Be(2);
        readCount.Should().Be(1);

        var invalidReadCount = 0;
        var invalidLazyGate = MssqlGeneratedDdlProvisioningGate.CreateLazy(() =>
        {
            invalidReadCount++;
            return "0";
        });

        Action firstInvalidAccess = () => _ = invalidLazyGate.Value;
        Action secondInvalidAccess = () => _ = invalidLazyGate.Value;

        firstInvalidAccess.Should().Throw<InvalidOperationException>();
        secondInvalidAccess.Should().Throw<InvalidOperationException>();
        invalidReadCount.Should().Be(1);
    }

    [Test]
    public async Task It_excludes_queued_entrants_and_admits_them_after_release()
    {
        var gate = new MssqlGeneratedDdlProvisioningGate("2");
        var first = await gate.AcquireAsync();
        var second = await gate.AcquireAsync();
        var waiting = gate.AcquireAsync().AsTask();
        MssqlGeneratedDdlProvisioningLease? third = null;
        var firstReleased = false;

        try
        {
            gate.AdmittedCount.Should().Be(2);
            waiting.IsCompleted.Should().BeFalse();

            await first.DisposeAsync();
            firstReleased = true;
            third = await waiting;

            gate.AdmittedCount.Should().Be(2);
            third.CaptureSnapshot().IsAdmitted.Should().BeTrue();
        }
        finally
        {
            if (!firstReleased)
            {
                await first.DisposeAsync();
            }

            await second.DisposeAsync();

            if (third is null)
            {
                third = await waiting;
            }

            await third.DisposeAsync();
        }
    }

    [Test]
    public async Task It_counts_unlimited_operations_and_disposes_each_lease_once()
    {
        var gate = new MssqlGeneratedDdlProvisioningGate("");
        var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gate.AcquireAsync().AsTask()));

        gate.AdmittedCount.Should().Be(4);
        leases[0].CaptureSnapshot().AdmissionWaitElapsed.Should().Be(TimeSpan.Zero);

        await leases[0].DisposeAsync();
        await leases[0].DisposeAsync();
        gate.AdmittedCount.Should().Be(3);

        foreach (var lease in leases.Skip(1))
        {
            await lease.DisposeAsync();
        }

        gate.AdmittedCount.Should().Be(0);
    }

    [Test]
    public async Task It_releases_admission_when_the_owned_operation_throws()
    {
        var gate = new MssqlGeneratedDdlProvisioningGate("1");
        var sentinel = new InvalidOperationException("sentinel");
        Func<Task> operation = async () =>
        {
            await using var lease = await gate.AcquireAsync();
            throw sentinel;
        };

        var thrown = await operation.Should().ThrowAsync<InvalidOperationException>();

        thrown.Which.Should().BeSameAs(sentinel);
        gate.AdmittedCount.Should().Be(0);

        var nextLease = await gate.AcquireAsync();
        gate.AdmittedCount.Should().Be(1);
        await nextLease.DisposeAsync();
        gate.AdmittedCount.Should().Be(0);
    }
}
