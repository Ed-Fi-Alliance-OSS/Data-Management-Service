// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed class PostgresqlGeneratedDdlBaselineDatabase : IAsyncDisposable
{
    private static readonly object _sync = new();
    private static readonly Dictionary<string, SharedBaselineEntry> _sharedBaselines = new(
        StringComparer.Ordinal
    );

    private readonly SharedBaselineEntry _sharedBaselineEntry;
    private readonly SharedBaselineState _sharedBaselineState;
    private bool _disposed;

    private PostgresqlGeneratedDdlBaselineDatabase(
        string fixtureSignature,
        SharedBaselineEntry sharedBaselineEntry,
        SharedBaselineState sharedBaselineState
    )
    {
        FixtureSignature = fixtureSignature;
        _sharedBaselineEntry = sharedBaselineEntry;
        _sharedBaselineState = sharedBaselineState;
    }

    public string FixtureSignature { get; }

    public string BaselineDatabaseName => _sharedBaselineState.BaselineDatabaseName;

    public static async Task<PostgresqlGeneratedDdlBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var generatedDdlHash = ComputeGeneratedDdlHash(generatedDdl);
        SharedBaselineEntry sharedBaselineEntry;

        lock (_sync)
        {
            if (_sharedBaselines.TryGetValue(fixtureSignature, out var existingEntry))
            {
                sharedBaselineEntry = existingEntry;

                if (
                    !string.Equals(
                        sharedBaselineEntry.GeneratedDdlHash,
                        generatedDdlHash,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Fixture signature '{fixtureSignature}' is already associated with different generated DDL."
                    );
                }

                sharedBaselineEntry.LeaseCount++;
            }
            else
            {
                sharedBaselineEntry = new(
                    generatedDdlHash,
                    CreateSharedBaselineStateAsync(generatedDdl, commandTimeoutSeconds)
                );
                _sharedBaselines[fixtureSignature] = sharedBaselineEntry;
            }
        }

        try
        {
            var sharedBaselineState = await sharedBaselineEntry.InitializationTask;
            return new(fixtureSignature, sharedBaselineEntry, sharedBaselineState);
        }
        catch
        {
            ReleaseLease(fixtureSignature, sharedBaselineEntry);
            throw;
        }
    }

    public Task<PostgresqlGeneratedDdlTestDatabase> CreateIsolatedDatabaseAsync(
        int commandTimeoutSeconds = 300
    )
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgresqlGeneratedDdlBaselineDatabase));
        }

        return PostgresqlGeneratedDdlTestDatabase.CreateFromTemplateAsync(
            BaselineDatabaseName,
            commandTimeoutSeconds
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (ReleaseLease(FixtureSignature, _sharedBaselineEntry))
        {
            await _sharedBaselineState.DisposeAsync();
        }
    }

    private static bool ReleaseLease(string fixtureSignature, SharedBaselineEntry sharedBaselineEntry)
    {
        lock (_sync)
        {
            if (
                !_sharedBaselines.TryGetValue(fixtureSignature, out var currentEntry)
                || !ReferenceEquals(currentEntry, sharedBaselineEntry)
            )
            {
                return false;
            }

            sharedBaselineEntry.LeaseCount--;

            if (sharedBaselineEntry.LeaseCount != 0)
            {
                return false;
            }

            _sharedBaselines.Remove(fixtureSignature);
            return true;
        }
    }

    private static async Task<SharedBaselineState> CreateSharedBaselineStateAsync(
        string generatedDdl,
        int commandTimeoutSeconds
    )
    {
        var baselineDatabase = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            generatedDdl,
            commandTimeoutSeconds
        );

        try
        {
            var baselineDatabaseName = baselineDatabase.DatabaseName;
            await baselineDatabase.DetachAsync();
            return new(baselineDatabaseName);
        }
        catch
        {
            await baselineDatabase.DisposeAsync();
            throw;
        }
    }

    private static string ComputeGeneratedDdlHash(string generatedDdl)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedDdl)));
    }

    private sealed class SharedBaselineEntry(
        string generatedDdlHash,
        Task<SharedBaselineState> initializationTask
    )
    {
        public string GeneratedDdlHash { get; } = generatedDdlHash;

        public Task<SharedBaselineState> InitializationTask { get; } = initializationTask;

        public int LeaseCount { get; set; } = 1;
    }

    private sealed class SharedBaselineState(string baselineDatabaseName) : IAsyncDisposable
    {
        public string BaselineDatabaseName { get; } = baselineDatabaseName;

        public ValueTask DisposeAsync()
        {
            return new(PostgresqlGeneratedDdlTestDatabase.DropDatabaseIfExistsAsync(BaselineDatabaseName));
        }
    }
}
