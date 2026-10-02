// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Reflection;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Boots the host in the Development environment, where the container validates scopes and builds
/// every registration at startup, so a read seam or cache registered with a lifetime that captures
/// the scoped tenant provider fails the boot here. The seam is transient like the repositories that
/// call it, and the cache is a singleton.
/// </summary>
[TestFixture("postgresql", "with a resolver")]
[TestFixture("postgresql", "without a resolver")]
[TestFixture("mssql", "with a resolver")]
[TestFixture("mssql", "without a resolver")]
public class Given_the_host_booted_in_the_development_environment(string datastore, string resolver)
{
    /// <summary>
    /// What appsettings.Test.json supplies, so the Development boot needs no database or identity
    /// provider, applied through <c>UseSetting</c> because some of it is read while services register.
    /// </summary>
    private static readonly Dictionary<string, string> _settings = new()
    {
        ["AppSettings:DeployDatabaseOnStartup"] = "false",
        ["DatabaseSettings:EncryptionKey"] = "TestEncryptionKey32CharactersLong1",
        ["IdentitySettings:AllowRegistration"] = "true",
        ["IdentitySettings:TokenCleanupEnabled"] = "false",
        ["IdentitySettings:ConfigServiceRole"] = "test-role",
        ["IdentitySettings:ClientRole"] = "dms-client",
        ["IdentitySettings:Authority"] = "http://localhost/realms/dms",
        ["IdentitySettings:Audience"] = "account",
        ["IdentitySettings:ClientId"] = "test_client",
        ["IdentitySettings:ClientSecret"] = "ValidClientSecret1234567890!Abcd",
        ["IdentitySettings:RequireHttpsMetadata"] = "false",
        ["IdentitySettings:RoleClaimType"] = "role",
        ["ClaimsOptions:DangerouslyEnableUnrestrictedClaimsLoading"] = "true",
        ["JobSettings:WorkerEnabled"] = "false",
        ["JobSettings:SchedulerEnabled"] = "false",
        ["JobSettings:RetentionEnabled"] = "false",
    };

    private sealed class FixedResolver : ISecretResolver
    {
        public ValueTask<string> ResolveAsync(
            SecretReference reference,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult("value");
    }

    private WebApplicationFactory<Program> _factory = null!;
    private Exception? _bootFailure;

    [SetUp]
    public void Act()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("AppSettings:Datastore", datastore);
            foreach ((string key, string value) in _settings)
            {
                builder.UseSetting(key, value);
            }

            if (resolver == "with a resolver")
            {
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<ISecretResolver, FixedResolver>()
                );
            }
        });

        try
        {
            using HttpClient client = _factory.CreateClient();
            _bootFailure = null;
        }
        catch (Exception exception)
        {
            _bootFailure = exception;
        }
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_passes_scope_validation_at_startup() => _bootFailure.Should().BeNull();

    [Test]
    public void It_creates_a_new_reader_for_each_resolution()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IConnectionStringReader>()
            .Should()
            .NotBeSameAs(scope.ServiceProvider.GetRequiredService<IConnectionStringReader>())
            .And.BeOfType<ConnectionStringReader>();
    }

    [Test]
    public void It_composes_both_repositories_over_the_reader()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        string engine = datastore == "postgresql" ? "Postgresql" : "Mssql";

        foreach (
            object repository in new object[]
            {
                scope.ServiceProvider.GetRequiredService<IDataStoreRepository>(),
                scope.ServiceProvider.GetRequiredService<IDataStoreDerivativeRepository>(),
            }
        )
        {
            repository
                .GetType()
                .Namespace.Should()
                .StartWith($"EdFi.DmsConfigurationService.Backend.{engine}");
            ReaderHeldBy(repository).Should().BeOfType<ConnectionStringReader>();
        }
    }

    /// <summary>
    /// The reader a repository captured. A primary constructor keeps a parameter as a field only when
    /// the class uses it, so a repository that stopped reading through the seam holds none.
    /// </summary>
    private static object? ReaderHeldBy(object repository) =>
        repository
            .GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(field => field.FieldType == typeof(IConnectionStringReader))
            ?.GetValue(repository);

    [Test]
    public void It_shares_one_cache_across_scopes()
    {
        using IServiceScope first = _factory.Services.CreateScope();
        using IServiceScope second = _factory.Services.CreateScope();

        first
            .ServiceProvider.GetRequiredService<SecretValueCache>()
            .Should()
            .BeSameAs(second.ServiceProvider.GetRequiredService<SecretValueCache>());
    }
}
