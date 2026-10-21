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
    private int[] _parsedLimits = [];
    private Exception[] _configurationFailures = [];
    private MssqlGeneratedDdlProvisioningGate _firstGate = null!;
    private MssqlGeneratedDdlProvisioningGate _secondGate = null!;
    private int _settingReads;
    private int _invalidSettingReads;
    private Exception[] _initializationFailures = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _parsedLimits = new[] { "", " \t ", "1", "0002", "2147483647" }
            .Select(value => new MssqlGeneratedDdlProvisioningGate(value).MaximumConcurrency)
            .ToArray();
        _configurationFailures = new[] { "0", "-1", "+2", " 2", "2 ", "x", "2147483648" }
            .Select(value => CaptureFailure(() => _ = new MssqlGeneratedDdlProvisioningGate(value)))
            .ToArray();

        var configuredValue = "2";
        _settingReads = 0;
        var lazyGate = MssqlGeneratedDdlProvisioningGate.CreateLazy(() =>
        {
            _settingReads++;
            return configuredValue;
        });
        _firstGate = lazyGate.Value;
        configuredValue = "1";
        _secondGate = lazyGate.Value;

        _invalidSettingReads = 0;
        var invalidLazyGate = MssqlGeneratedDdlProvisioningGate.CreateLazy(() =>
        {
            _invalidSettingReads++;
            return "0";
        });
        _initializationFailures =
        [
            CaptureFailure(() => _ = invalidLazyGate.Value),
            CaptureFailure(() => _ = invalidLazyGate.Value),
        ];
    }

    [Test]
    public void It_parses_unlimited_and_positive_limits() =>
        _parsedLimits.Should().Equal(0, 0, 1, 2, int.MaxValue);

    [Test]
    public void It_rejects_invalid_configuration()
    {
        foreach (var failure in _configurationFailures)
        {
            failure
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Be("MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY must be a positive integer when set.");
        }
    }

    [Test]
    public void It_reuses_the_initialized_gate() => _secondGate.Should().BeSameAs(_firstGate);

    [Test]
    public void It_retains_the_first_configured_limit() => _secondGate.MaximumConcurrency.Should().Be(2);

    [Test]
    public void It_reads_valid_configuration_once() => _settingReads.Should().Be(1);

    [Test]
    public void It_preserves_initialization_failures()
    {
        foreach (var failure in _initializationFailures)
        {
            failure.Should().BeOfType<InvalidOperationException>();
        }
    }

    [Test]
    public void It_reads_invalid_configuration_once() => _invalidSettingReads.Should().Be(1);

    private static Exception CaptureFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        return new AssertionException("Expected the provisioning configuration to be rejected.");
    }
}

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningGate_With_A_Queued_Entrant
{
    private int _queuedCount;
    private bool _entrantWasQueued;
    private int _countAfterAdmission;
    private bool _entrantWasAdmitted;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var gate = new MssqlGeneratedDdlProvisioningGate("2");
        var first = await gate.AcquireAsync();
        var second = await gate.AcquireAsync();
        var waiting = gate.AcquireAsync().AsTask();
        MssqlGeneratedDdlProvisioningLease third = null!;
        var firstReleased = false;
        try
        {
            _queuedCount = gate.AdmittedCount;
            _entrantWasQueued = !waiting.IsCompleted;
            await first.DisposeAsync();
            firstReleased = true;
            third = await waiting.WaitAsync(TimeSpan.FromSeconds(90));
            _countAfterAdmission = gate.AdmittedCount;
            _entrantWasAdmitted = third.CaptureSnapshot().IsAdmitted;
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
                third = await waiting.WaitAsync(TimeSpan.FromSeconds(90));
            }
            await third.DisposeAsync();
        }
    }

    [Test]
    public void It_excludes_queued_entrants_from_the_admitted_count() => _queuedCount.Should().Be(2);

    [Test]
    public void It_queues_an_entrant_when_all_permits_are_held() => _entrantWasQueued.Should().BeTrue();

    [Test]
    public void It_replaces_the_released_entrant_in_the_admitted_count() =>
        _countAfterAdmission.Should().Be(2);

    [Test]
    public void It_admits_the_queued_entrant_after_release() => _entrantWasAdmitted.Should().BeTrue();
}

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningGate_With_Unlimited_Admission
{
    private int[] _unlimitedCounts = [];
    private TimeSpan _unlimitedWait;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var unlimitedGate = new MssqlGeneratedDdlProvisioningGate("");
        var leases = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => unlimitedGate.AcquireAsync().AsTask())
        );
        _unlimitedWait = leases[0].CaptureSnapshot().AdmissionWaitElapsed;
        var initialCount = unlimitedGate.AdmittedCount;
        await leases[0].DisposeAsync();
        var afterFirstDispose = unlimitedGate.AdmittedCount;
        await leases[0].DisposeAsync();
        var afterDoubleDispose = unlimitedGate.AdmittedCount;
        foreach (var lease in leases.Skip(1))
        {
            await lease.DisposeAsync();
        }
        _unlimitedCounts = [initialCount, afterFirstDispose, afterDoubleDispose, unlimitedGate.AdmittedCount];
    }

    [Test]
    public void It_counts_unlimited_operations_and_disposes_each_lease_once() =>
        _unlimitedCounts.Should().Equal(4, 3, 3, 0);

    [Test]
    public void It_has_no_admission_wait_when_unlimited() => _unlimitedWait.Should().Be(TimeSpan.Zero);
}
