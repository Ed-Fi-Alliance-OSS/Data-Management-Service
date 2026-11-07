// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// 7.3 step 1 and 6.1 step 3: the administrative connection string is used as configured when it holds no
/// reference; each reference is resolved through the registered resolver and substituted into its key's
/// value with the connection-string quoting; a value fetched again after the cache expires replaces the
/// current one; and every failure (no resolver, a resolver exception, an empty value, the resolution bound)
/// is reported by a fixed operation without the value, leaving the current value as it was, while caller
/// cancellation propagates.
/// </summary>
public class DmsAdminConnectionSourceTests
{
    private const string Plain = "Host=db.example;Port=5432;Username=dms_admin;Password=plain";

    private const string Referenced = "Host=db.example;Username=dms_admin;Password=${secret:pg-admin}";

    private static readonly TimeSpan _resolveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A resolver whose answer each fixture scripts; every call is counted.</summary>
    private sealed class ScriptedResolver(Func<SecretReference, CancellationToken, Task<string>> answer)
        : ISecretResolver
    {
        public List<SecretReference> Calls { get; } = [];

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(reference);
            }

            return new ValueTask<string>(answer(reference, cancellationToken));
        }
    }

    private static DmsAdminConnectionSource Source(
        string configured,
        ISecretResolver? resolver,
        TimeProvider? time = null,
        int cacheExpirationSeconds = 300
    )
    {
        SecretsOptions secrets = new()
        {
            CacheExpirationSeconds = cacheExpirationSeconds,
            ResolveTimeoutSeconds = (int)_resolveTimeout.TotalSeconds,
        };
        TimeProvider clock = time ?? TimeProvider.System;
        return new DmsAdminConnectionSource(
            Options.Create(new DmsDataStoreProvisioningOptions { AdminConnectionString = configured }),
            new SecretValueCache(Options.Create(secrets), clock),
            Options.Create(secrets),
            clock,
            resolver
        );
    }

    private static string ValueOf(string connectionString, string key) =>
        (string)new DbConnectionStringBuilder { ConnectionString = connectionString }[key];

    [TestFixture]
    public class Given_a_value_without_references
    {
        private ScriptedResolver _resolver = null!;
        private DmsAdminConnectionResolution _resolution = null!;
        private string _current = null!;

        [SetUp]
        public async Task Setup()
        {
            _resolver = new ScriptedResolver((_, _) => Task.FromResult("unused"));
            DmsAdminConnectionSource source = Source(Plain, _resolver);
            _resolution = await source.ResolveAsync(CancellationToken.None);
            _current = source.Current;
        }

        [Test]
        public void It_resolves() => _resolution.Should().BeOfType<DmsAdminConnectionResolution.Resolved>();

        [Test]
        public void It_uses_the_configured_text_unchanged() => _current.Should().Be(Plain);

        [Test]
        public void It_never_asks_the_resolver() => _resolver.Calls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_references_the_resolver_answers
    {
        private const string Quoted = "p;w'd=\"x\"";

        private ScriptedResolver _resolver = null!;
        private DmsAdminConnectionResolution _resolution = null!;
        private string _current = null!;

        [SetUp]
        public async Task Setup()
        {
            _resolver = new ScriptedResolver(
                (reference, _) =>
                    Task.FromResult(
                        reference.Name switch
                        {
                            "pg-admin" => Quoted,
                            "pg-user" => "dms",
                            _ => "admin",
                        }
                    )
            );
            DmsAdminConnectionSource source = Source(
                "Host=db.example;Username=${secret:pg-user}_${secret:pg-role};Password=${secret:pg-admin}",
                _resolver
            );
            _resolution = await source.ResolveAsync(CancellationToken.None);
            _current = source.Current;
        }

        [Test]
        public void It_resolves() => _resolution.Should().BeOfType<DmsAdminConnectionResolution.Resolved>();

        [Test]
        public void It_quotes_a_resolved_value_the_syntax_would_otherwise_split() =>
            ValueOf(_current, "Password").Should().Be(Quoted);

        [Test]
        public void It_substitutes_every_reference_inside_one_value() =>
            ValueOf(_current, "Username").Should().Be("dms_admin");

        [Test]
        public void It_keeps_the_keys_without_references() =>
            ValueOf(_current, "Host").Should().Be("db.example");

        [Test]
        public void It_asks_for_each_name_without_a_tenant() =>
            _resolver
                .Calls.Should()
                .BeEquivalentTo([
                    new SecretReference("pg-user", null),
                    new SecretReference("pg-role", null),
                    new SecretReference("pg-admin", null),
                ]);
    }

    [TestFixture]
    public class Given_the_secret_rotates_after_the_cache_expires
    {
        private string _first = null!;
        private string _second = null!;

        [SetUp]
        public async Task Setup()
        {
            int calls = 0;
            ScriptedResolver resolver = new(
                (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? "before" : "after")
            );
            DmsAdminConnectionSource source = Source(Referenced, resolver, cacheExpirationSeconds: 0);
            await source.ResolveAsync(CancellationToken.None);
            _first = ValueOf(source.Current, "Password");
            await source.ResolveAsync(CancellationToken.None);
            _second = ValueOf(source.Current, "Password");
        }

        [Test]
        public void It_connects_with_the_value_of_the_latest_resolution() =>
            (_first, _second).Should().Be(("before", "after"));
    }

    [TestFixture]
    public class Given_resolution_fails
    {
        private static async Task<(
            DmsAdminConnectionResolution Resolution,
            string Current
        )> AfterAFailureAsync(Func<SecretReference, CancellationToken, Task<string>> second)
        {
            int calls = 0;
            ScriptedResolver resolver = new(
                (reference, token) =>
                    Interlocked.Increment(ref calls) == 1
                        ? Task.FromResult("first")
                        : second(reference, token)
            );
            DmsAdminConnectionSource source = Source(Referenced, resolver, cacheExpirationSeconds: 0);
            await source.ResolveAsync(CancellationToken.None);
            DmsAdminConnectionResolution resolution = await source.ResolveAsync(CancellationToken.None);
            return (resolution, ValueOf(source.Current, "Password"));
        }

        private DmsAdminConnectionResolution _noResolver = null!;
        private Action _currentWithoutResolution = null!;
        private (DmsAdminConnectionResolution Resolution, string Current) _thrown;
        private (DmsAdminConnectionResolution Resolution, string Current) _empty;

        [SetUp]
        public async Task Setup()
        {
            DmsAdminConnectionSource noResolver = Source(Referenced, resolver: null);
            _noResolver = await noResolver.ResolveAsync(CancellationToken.None);
            _currentWithoutResolution = () => _ = noResolver.Current;
            _thrown = await AfterAFailureAsync(
                (_, _) => Task.FromException<string>(new InvalidOperationException("secret text"))
            );
            _empty = await AfterAFailureAsync((_, _) => Task.FromResult(string.Empty));
        }

        [Test]
        public void It_refuses_references_without_a_registered_resolver() =>
            _noResolver
                .Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.NoResolverStep)
                    )
                );

        [Test]
        public void It_has_no_current_value_before_a_resolution_succeeds() =>
            _currentWithoutResolution.Should().Throw<InvalidOperationException>();

        [Test]
        public void It_reports_a_resolver_exception_by_its_type_only() =>
            _thrown
                .Resolution.Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        new DmsProvisioningDiagnostic(
                            "System.InvalidOperationException",
                            null,
                            DmsAdminConnectionSource.ResolverFailedStep
                        )
                    )
                );

        [Test]
        public void It_refuses_an_empty_value() =>
            _empty
                .Resolution.Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.EmptyValueStep)
                    )
                );

        [Test]
        public void It_keeps_the_previous_value_after_a_failure() =>
            (_thrown.Current, _empty.Current).Should().Be(("first", "first"));
    }

    [TestFixture]
    public class Given_the_resolver_does_not_return
    {
        private DmsAdminConnectionResolution _timedOut = null!;
        private Func<Task> _cancelled = null!;
        private bool _resolverTokenCancelled;

        [SetUp]
        public async Task Setup()
        {
            FakeTimeProvider time = new();
            TaskCompletionSource<CancellationToken> called = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            ScriptedResolver hanging = new(
                (_, token) =>
                {
                    called.TrySetResult(token);
                    return new TaskCompletionSource<string>().Task;
                }
            );
            DmsAdminConnectionSource source = Source(Referenced, hanging, time);
            Task<DmsAdminConnectionResolution> pending = source.ResolveAsync(CancellationToken.None);
            CancellationToken resolverToken = await called.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(_resolveTimeout);
            _timedOut = await pending.WaitAsync(TimeSpan.FromSeconds(30));
            _resolverTokenCancelled = resolverToken.IsCancellationRequested;

            TaskCompletionSource calledAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ScriptedResolver blocking = new(
                (_, _) =>
                {
                    calledAgain.TrySetResult();
                    return new TaskCompletionSource<string>().Task;
                }
            );
            using CancellationTokenSource caller = new();
            Task<DmsAdminConnectionResolution> cancelledCall = Source(
                    Referenced,
                    blocking,
                    new FakeTimeProvider()
                )
                .ResolveAsync(caller.Token);
            await calledAgain.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await caller.CancelAsync();
            _cancelled = () => cancelledCall.WaitAsync(TimeSpan.FromSeconds(30));
        }

        [Test]
        public void It_reports_the_bound_as_timed_out() =>
            _timedOut
                .Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.TimedOutStep)
                    )
                );

        [Test]
        public void It_cancels_the_token_it_gave_the_resolver() => _resolverTokenCancelled.Should().BeTrue();

        [Test]
        public async Task It_propagates_the_caller_cancellation() =>
            await _cancelled.Should().ThrowAsync<OperationCanceledException>();
    }
}
