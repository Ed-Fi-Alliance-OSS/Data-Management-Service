// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Mssql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Postgresql.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture("postgresql")]
[TestFixture("mssql")]
public class Given_managed_runtime_connection_snapshots(string providerName)
{
    private IManagedDataStoreRuntimeConnectionProvider Provider =>
        providerName == "mssql"
            ? new MssqlManagedRuntimeConnectionProvider()
            : new PostgresqlManagedRuntimeConnectionProvider();

    private string Connection(
        string host = "db.example",
        string password = "runtime-only",
        string database = "ignored"
    )
    {
        var builder = Provider.CreateBuilder(providerName == "mssql" ? $"Server=tcp:{host}" : $"Host={host}");
        builder["User ID"] = "runtime_user";
        builder["Password"] = password;
        builder["Database"] = database;
        builder["Application Name"] = "DMS runtime";
        builder["Pooling"] = false;
        return builder.ConnectionString;
    }

    private ManagedDataStoreEndpoint Endpoint()
    {
        Provider.TryParse(Connection(), out var endpoint).Should().BeTrue();
        return endpoint!;
    }

    private static DmsTargetDatabaseName Target()
    {
        DmsTargetDatabaseName
            .TryCreate("EdFi_Ods_Runtime_Minimal", ProtectedDatabaseNames.Create("cms", []), out var target)
            .Should()
            .Be(DmsTargetNameValidation.Valid);
        return target;
    }

    private ManagedDataStoreRuntimeConnectionBuilder Builder(
        ManagedDataStoreRuntimeOptions options,
        ISecretResolver? resolver = null,
        IManagedDataStoreRuntimeConnectionProvider? provider = null,
        TimeProvider? time = null
    )
    {
        var secrets = Options.Create(
            new SecretsOptions { CacheExpirationSeconds = 0, ResolveTimeoutSeconds = 5 }
        );
        return new(
            Options.Create(options),
            provider ?? Provider,
            new SecretValueCache(secrets, time),
            secrets,
            time ?? TimeProvider.System,
            resolver
        );
    }

    private sealed class Resolver(Func<SecretReference, CancellationToken, Task<string>> resolve)
        : ISecretResolver
    {
        public int Calls { get; private set; }

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            Calls++;
            return new(resolve(reference, cancellationToken));
        }
    }

    private static DmsProvisioningFailure Failure(
        ManagedDataStoreRuntimeConnectionResult result,
        DmsProvisioningFailureCode code
    )
    {
        var failure = result
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Failure>()
            .Subject.Detail;
        failure.Code.Should().Be(code);
        return failure;
    }

    [Test]
    public async Task It_changes_only_database_and_maps_the_target_provider()
    {
        string configured = Connection(password: "private;quoted=\"value\"");
        var resolver = new Resolver((_, _) => throw new InvalidOperationException("must not resolve"));
        var result = (
            await Builder(new() { RuntimeConnectionString = configured }, resolver)
                .BuildAsync(Endpoint(), Target(), default)
        )
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Success>()
            .Subject;
        var expected = Provider.CreateBuilder(configured);
        expected["Database"] = Target().Value;
        // Boolean assertions keep plaintext out of failure diagnostics.
        (result.ConnectionString == expected.ConnectionString)
            .Should()
            .BeTrue();
        result.CatalogProvider.Should().Be(providerName == "mssql" ? "sqlserver" : "postgresql");
        result.ToString().Should().Be("Success");
        resolver.Calls.Should().Be(0);
    }

    [Test]
    public async Task It_refuses_endpoint_rotation_before_capture()
    {
        var result = await Builder(new() { RuntimeConnectionString = Connection("other.example") })
            .BuildAsync(Endpoint(), Target(), default);
        Failure(result, DmsProvisioningFailureCode.TargetEndpointChanged)
            .Category.Should()
            .Be(DmsProvisioningFailureCategory.OperatorInspectionRequired);
    }

    [Test]
    public async Task It_validates_the_resolved_endpoint_instead_of_an_earlier_configuration()
    {
        var resolver = new Resolver((_, _) => Task.FromResult("other.example"));
        var result = await Builder(new() { RuntimeConnectionString = Connection("${secret:host}") }, resolver)
            .BuildAsync(Endpoint(), Target(), default);
        Failure(result, DmsProvisioningFailureCode.TargetEndpointChanged);
        resolver.Calls.Should().Be(1);
    }

    private sealed class ObservedProvider(IManagedDataStoreRuntimeConnectionProvider inner, Action observed)
        : IManagedDataStoreRuntimeConnectionProvider
    {
        public string Provider => inner.Provider;

        public bool TryParse(string snapshot, [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint)
        {
            observed();
            return inner.TryParse(snapshot, out endpoint);
        }

        public DbConnectionStringBuilder CreateBuilder(string snapshot) => inner.CreateBuilder(snapshot);
    }

    [Test]
    public async Task It_keeps_the_captured_snapshot_after_configuration_and_secret_rotation()
    {
        string password = "first-version;quoted";
        var resolver = new Resolver((_, _) => Task.FromResult(password));
        var options = new ManagedDataStoreRuntimeOptions
        {
            RuntimeConnectionString = Connection(password: "${secret:password}"),
        };
        var observed = new ObservedProvider(
            Provider,
            () =>
            {
                options.RuntimeConnectionString = Connection("other.example");
                password = "second-version";
            }
        );
        var builder = Builder(options, resolver, observed);
        var result = (await builder.BuildAsync(Endpoint(), Target(), default))
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Success>()
            .Subject;
        var parsed = Provider.CreateBuilder(result.ConnectionString);
        ((string)parsed["Password"] == "first-version;quoted").Should().BeTrue();
        Provider.TryParse(result.ConnectionString, out var actual).Should().BeTrue();
        actual.Should().Be(Endpoint());
        ((string)parsed["Database"] == Target().Value).Should().BeTrue();
        resolver.Calls.Should().Be(1);
        Failure(
            await builder.BuildAsync(Endpoint(), Target(), default),
            DmsProvisioningFailureCode.TargetEndpointChanged
        );
    }

    [Test]
    public async Task It_allows_credential_only_rotation_without_changing_the_previous_snapshot()
    {
        string password = "one";
        var resolver = new Resolver((_, _) => Task.FromResult(password));
        var builder = Builder(
            new() { RuntimeConnectionString = Connection(password: "${secret:password}") },
            resolver
        );
        var first = (await builder.BuildAsync(Endpoint(), Target(), default))
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Success>()
            .Subject;
        password = "two";
        var second = (await builder.BuildAsync(Endpoint(), Target(), default))
            .Should()
            .BeOfType<ManagedDataStoreRuntimeConnectionResult.Success>()
            .Subject;
        ((string)Provider.CreateBuilder(first.ConnectionString)["Password"] == "one").Should().BeTrue();
        ((string)Provider.CreateBuilder(second.ConnectionString)["Password"] == "two").Should().BeTrue();
        resolver.Calls.Should().Be(2);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not a connection string")]
    public async Task It_refuses_missing_or_malformed_runtime_configuration(string? configured)
    {
        Failure(
            await Builder(new() { RuntimeConnectionString = configured })
                .BuildAsync(Endpoint(), Target(), default),
            DmsProvisioningFailureCode.TargetEndpointUnverifiable
        );
    }

    [Test]
    public async Task It_refuses_unsupported_routing()
    {
        string configured = providerName == "mssql" ? "Server=not-explicit-tcp" : "Host=one,two";
        Failure(
            await Builder(new() { RuntimeConnectionString = configured })
                .BuildAsync(Endpoint(), Target(), default),
            DmsProvisioningFailureCode.TargetEndpointUnverifiable
        );
    }

    [Test]
    public async Task It_refuses_a_provider_mismatch_before_resolving()
    {
        var resolver = new Resolver((_, _) => throw new InvalidOperationException("must not resolve"));
        var expected = Endpoint() with { Provider = providerName == "mssql" ? "postgresql" : "mssql" };
        Failure(
            await Builder(
                    new() { RuntimeConnectionString = Connection(password: "${secret:password}") },
                    resolver
                )
                .BuildAsync(expected, Target(), default),
            DmsProvisioningFailureCode.TargetEndpointChanged
        );
        resolver.Calls.Should().Be(0);
    }

    [TestCase("missing")]
    [TestCase("empty")]
    [TestCase("throws")]
    public async Task It_reports_secret_failures_without_resolver_text(string mode)
    {
        const string secret = "DO_NOT_EXPOSE_secret_or_connection";
        ISecretResolver? resolver =
            mode == "missing"
                ? null
                : new Resolver(
                    (_, _) =>
                        mode == "empty" ? Task.FromResult("") : throw new InvalidOperationException(secret)
                );
        var result = await Builder(
                new() { RuntimeConnectionString = Connection(password: "${secret:password}") },
                resolver
            )
            .BuildAsync(Endpoint(), Target(), default);
        var failure = Failure(result, DmsProvisioningFailureCode.SecretResolutionFailed);
        failure.Category.Should().Be(DmsProvisioningFailureCategory.Transient);
        failure.Diagnostic.Operation.Should().Be(ManagedDataStoreRuntimeConnectionBuilder.ResolveStep);
        result
            .ToString()
            .Should()
            .NotContain(secret)
            .And.NotContain("runtime_user")
            .And.NotContain("${secret:");
    }

    [Test]
    public async Task It_refuses_secret_references_returned_by_the_resolver()
    {
        var resolver = new Resolver((_, _) => Task.FromResult("${secret:another}"));
        Failure(
            await Builder(
                    new() { RuntimeConnectionString = Connection(password: "${secret:password}") },
                    resolver
                )
                .BuildAsync(Endpoint(), Target(), default),
            DmsProvisioningFailureCode.TargetEndpointUnverifiable
        );
        resolver.Calls.Should().Be(1);
    }

    [Test]
    public async Task It_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var action = () =>
            Builder(new() { RuntimeConnectionString = Connection() })
                .BuildAsync(Endpoint(), Target(), cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_bounds_secret_resolution_with_the_existing_deadline()
    {
        FakeTimeProvider time = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(
            (_, _) =>
            {
                started.SetResult();
                return finish.Task;
            }
        );
        var task = Builder(
                new() { RuntimeConnectionString = Connection(password: "${secret:password}") },
                resolver,
                time: time
            )
            .BuildAsync(Endpoint(), Target(), default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(5));
            Failure(
                await task.WaitAsync(TimeSpan.FromSeconds(5)),
                DmsProvisioningFailureCode.SecretResolutionFailed
            )
                .Category.Should()
                .Be(DmsProvisioningFailureCategory.Transient);
        }
        finally
        {
            finish.TrySetResult("late-value");
        }
    }
}
