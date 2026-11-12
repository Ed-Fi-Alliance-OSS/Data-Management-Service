// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

internal sealed record MssqlGeneratedDdlProvisioningSnapshot(
    bool IsAdmitted,
    int MaximumConcurrency,
    int AdmittedCount,
    TimeSpan AdmissionWaitElapsed
)
{
    public static MssqlGeneratedDdlProvisioningSnapshot NotAdmitted { get; } =
        new(false, 0, 0, TimeSpan.Zero);
}

internal sealed class MssqlGeneratedDdlProvisioningGate
{
    public const string SettingName = "MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY";

    private readonly SemaphoreSlim? _semaphore;
    private int _admittedCount;

    public MssqlGeneratedDdlProvisioningGate(string configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            MaximumConcurrency = 0;
            return;
        }

        if (
            !int.TryParse(
                configuredValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var maximumConcurrency
            )
            || maximumConcurrency < 1
        )
        {
            throw new InvalidOperationException($"{SettingName} must be a positive integer when set.");
        }

        MaximumConcurrency = maximumConcurrency;
        _semaphore = new(maximumConcurrency, maximumConcurrency);
    }

    public int MaximumConcurrency { get; }

    public int AdmittedCount => Volatile.Read(ref _admittedCount);

    public static Lazy<MssqlGeneratedDdlProvisioningGate> CreateLazy(Func<string> readSetting) =>
        new(() => new MssqlGeneratedDdlProvisioningGate(readSetting()));

    public async ValueTask<MssqlGeneratedDdlProvisioningLease> AcquireAsync()
    {
        if (_semaphore is null)
        {
            Interlocked.Increment(ref _admittedCount);
            return new(this, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        await _semaphore.WaitAsync();
        stopwatch.Stop();
        Interlocked.Increment(ref _admittedCount);
        return new(this, stopwatch.Elapsed);
    }

    internal void Release()
    {
        Interlocked.Decrement(ref _admittedCount);
        _semaphore?.Release();
    }
}

internal sealed class MssqlGeneratedDdlProvisioningLease(
    MssqlGeneratedDdlProvisioningGate gate,
    TimeSpan admissionWaitElapsed
) : IAsyncDisposable
{
    private int _disposed;

    public MssqlGeneratedDdlProvisioningSnapshot CaptureSnapshot() =>
        new(true, gate.MaximumConcurrency, gate.AdmittedCount, admissionWaitElapsed);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            gate.Release();
        }

        return ValueTask.CompletedTask;
    }
}
