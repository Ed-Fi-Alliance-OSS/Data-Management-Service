// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed class PostgresqlGeneratedDdlBaselineDatabase : IAsyncDisposable
{
    private bool _disposed;

    private PostgresqlGeneratedDdlBaselineDatabase(string fixtureSignature, string baselineDatabaseName)
    {
        FixtureSignature = fixtureSignature;
        BaselineDatabaseName = baselineDatabaseName;
    }

    public string FixtureSignature { get; }

    public string BaselineDatabaseName { get; }

    public static async Task<PostgresqlGeneratedDdlBaselineDatabase> CreateAsync(
        string fixtureSignature,
        string generatedDdl,
        int commandTimeoutSeconds = 300
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedDdl);

        var baselineDatabase = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(
            generatedDdl,
            commandTimeoutSeconds
        );

        try
        {
            var baselineDatabaseName = baselineDatabase.DatabaseName;
            await baselineDatabase.DetachAsync();

            return new(fixtureSignature, baselineDatabaseName);
        }
        catch
        {
            await baselineDatabase.DisposeAsync();
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
        await PostgresqlGeneratedDdlTestDatabase.DropDatabaseIfExistsAsync(BaselineDatabaseName);
    }
}
