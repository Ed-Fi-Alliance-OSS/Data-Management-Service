// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    private sealed class RuntimeEncryptionRecorder(IConnectionStringEncryptionService inner, string expected)
        : IConnectionStringEncryptionService
    {
        public int EncryptCalls { get; private set; }

        public byte[]? Encrypt(string? connectionString)
        {
            (connectionString == expected)
                .Should()
                .BeTrue("only the captured validated plaintext may reach encryption");
            EncryptCalls++;
            return inner.Encrypt(connectionString);
        }

        public string? Decrypt(byte[]? encryptedConnectionString) => inner.Decrypt(encryptedConnectionString);
    }

    [Test, Category("ManagedRuntimeConnection")]
    public async Task It_encrypts_the_validated_runtime_snapshot_once_without_resolving_again(
        [Values(0, 1, 2)] int passwordKind
    )
    {
        var seed = await SeedPublication(provider: "postgresql");
        PostgresqlManagedRuntimeConnectionProvider provider = new();
        var runtimeOptions = new ManagedDataStoreRuntimeOptions
        {
            RuntimeConnectionString =
                "Host=db.example;Username=runtime;Password=runtime-private;Database=ignored",
        };
        string password = passwordKind switch
        {
            1 => "runtime-${secret:}",
            2 => "runtime-${secret:abc",
            _ => "runtime-private",
        };
        var configured = provider.CreateBuilder(runtimeOptions.RuntimeConnectionString);
        configured["Password"] = password;
        runtimeOptions.RuntimeConnectionString = configured.ConnectionString;
        var secrets = Options.Create(new SecretsOptions());
        ManagedDataStoreRuntimeConnectionBuilder builder = new(
            Options.Create(runtimeOptions),
            provider,
            new SecretValueCache(secrets),
            secrets,
            TimeProvider.System
        );
        var captured = (
            await builder.BuildAsync(
                seed.Command.Identity.Endpoint,
                seed.Command.Identity.TargetName,
                default
            )
        )
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Success>()
            .Subject;

        // Rotation after capture must not be consulted by publication or its idempotent replay.
        runtimeOptions.RuntimeConnectionString =
            "Host=different.example;Username=runtime;Password=rotated-private;Database=ignored";
        var encryption = new RuntimeEncryptionRecorder(
            new ConnectionStringEncryptionService(PublicationOptions),
            captured.ConnectionString
        );
        var repository = PublicationRepository(encryption: encryption);
        var command = seed.Command with { RuntimeConnectionString = captured.ConnectionString };
        int id = await repository.PublishCreateAsync(command, seed.Execution, default);
        (await repository.PublishCreateAsync(command, seed.Execution, default)).Should().Be(id);
        encryption.EncryptCalls.Should().Be(1);

        await using var connection = await Open();
        var row = await connection.QuerySingleAsync<(string Provider, byte[] ConnectionString)>(
            """SELECT "Provider", "ConnectionString" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
            new { Id = id }
        );
        row.Provider.Should().Be("postgresql");
        row.Provider.Should().Be(captured.CatalogProvider);
        string plaintext = encryption.Decrypt(row.ConnectionString)!;
        // Never put decrypted connection strings or credential values into assertion output.
        (plaintext == captured.ConnectionString)
            .Should()
            .BeTrue();
        provider.TryParse(plaintext, out var actual).Should().BeTrue();
        (actual == seed.Command.Identity.Endpoint).Should().BeTrue();
        ((string)provider.CreateBuilder(plaintext)["Database"] == seed.Command.Identity.TargetName.Value)
            .Should()
            .BeTrue();
        ((string)provider.CreateBuilder(plaintext)["Password"] == password).Should().BeTrue();
    }
}
