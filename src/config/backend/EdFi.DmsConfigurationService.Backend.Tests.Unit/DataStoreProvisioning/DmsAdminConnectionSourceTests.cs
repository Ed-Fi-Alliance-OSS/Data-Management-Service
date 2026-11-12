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
/// value with the connection-string quoting; each resolution is its own snapshot and a value fetched again
/// after the cache expires appears only in the next snapshot; every failure (no resolver, a resolver
/// exception, an empty value, the bound) is reported by a fixed operation without the value, and caller
/// cancellation propagates. A fetch that reaches its bound releases the cache, so the next resolution asks the
/// resolver again, and what the abandoned call returns later never reaches the cache. A snapshot is visible
/// only to the flow that bound it, until the binding is disposed.
/// </summary>
public class DmsAdminConnectionSourceTests
{
    private const string Plain = "Host=db.example;Port=5432;Username=dms_admin;Password=plain";

    private const string Referenced = "Host=db.example;Username=dms_admin;Password=${secret:pg-admin}";

    private static readonly TimeSpan _resolveTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(30);

    /// <summary>A resolver whose answer each fixture scripts; every call is counted.</summary>
    private sealed class ScriptedResolver(Func<SecretReference, CancellationToken, Task<string>> answer)
        : ISecretResolver
    {
        private int _count;

        public List<SecretReference> Calls { get; } = [];

        public int Count => Volatile.Read(ref _count);

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(reference);
            }

            Interlocked.Increment(ref _count);
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

    private static string ConnectionOf(DmsAdminConnectionResolution resolution) =>
        resolution.Should().BeOfType<DmsAdminConnectionResolution.Resolved>().Subject.Connection.Value;

    private static string ValueOf(string connectionString, string key) =>
        (string)new DbConnectionStringBuilder { ConnectionString = connectionString }[key];

    [TestFixture]
    public class Given_a_value_without_references
    {
        private ScriptedResolver _resolver = null!;
        private DmsAdminConnectionResolution _resolution = null!;

        [SetUp]
        public async Task Setup()
        {
            _resolver = new ScriptedResolver((_, _) => Task.FromResult("unused"));
            _resolution = await Source(Plain, _resolver).ResolveAsync(CancellationToken.None);
        }

        [Test]
        public void It_uses_the_configured_text_unchanged() => ConnectionOf(_resolution).Should().Be(Plain);

        [Test]
        public void It_never_asks_the_resolver() => _resolver.Calls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_references_the_resolver_answers
    {
        private const string Quoted = "p;w'd=\"x\"";

        private ScriptedResolver _resolver = null!;
        private string _connection = null!;

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
            _connection = ConnectionOf(
                await Source(
                        "Host=db.example;Username=${secret:pg-user}_${secret:pg-role};Password=${secret:pg-admin}",
                        _resolver
                    )
                    .ResolveAsync(CancellationToken.None)
            );
        }

        [Test]
        public void It_quotes_a_resolved_value_the_syntax_would_otherwise_split() =>
            ValueOf(_connection, "Password").Should().Be(Quoted);

        [Test]
        public void It_substitutes_every_reference_inside_one_value() =>
            ValueOf(_connection, "Username").Should().Be("dms_admin");

        [Test]
        public void It_keeps_the_keys_without_references() =>
            ValueOf(_connection, "Host").Should().Be("db.example");

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
            _first = ValueOf(ConnectionOf(await source.ResolveAsync(CancellationToken.None)), "Password");
            _second = ValueOf(ConnectionOf(await source.ResolveAsync(CancellationToken.None)), "Password");
        }

        [Test]
        public void It_gives_each_resolution_its_own_snapshot() =>
            (_first, _second).Should().Be(("before", "after"));
    }

    [TestFixture]
    public class Given_snapshots_bound_to_flows
    {
        private Action _unbound = null!;
        private string _inner = null!;
        private string _afterInner = null!;
        private Action _afterOuter = null!;
        private string _otherFlow = null!;
        private string _thisFlowMeanwhile = null!;

        [SetUp]
        public async Task Setup()
        {
            DmsAdminConnectionSource source = Source(Plain, resolver: null);
            _unbound = () => _ = source.Current;
            using (source.Bind(new DmsAdminConnectionString("Host=outer")))
            {
                using (source.Bind(new DmsAdminConnectionString("Host=inner")))
                {
                    _inner = source.Current;
                }

                _afterInner = source.Current;

                TaskCompletionSource otherBound = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<string> other = Task.Run(async () =>
                {
                    using IDisposable binding = source.Bind(new DmsAdminConnectionString("Host=other"));
                    otherBound.SetResult();
                    await release.Task;
                    return source.Current;
                });
                await otherBound.Task.WaitAsync(_bound);
                _thisFlowMeanwhile = source.Current;
                release.SetResult();
                _otherFlow = await other.WaitAsync(_bound);
            }

            _afterOuter = () => _ = source.Current;
        }

        [Test]
        public void It_has_no_value_outside_a_binding() =>
            (_unbound, _afterOuter)
                .Should()
                .Match<(Action Before, Action After)>(pair => Throws(pair.Before) && Throws(pair.After));

        [Test]
        public void It_restores_the_outer_binding_when_the_inner_one_ends() =>
            (_inner, _afterInner).Should().Be(("Host=inner", "Host=outer"));

        [Test]
        public void It_keeps_each_flow_on_its_own_binding() =>
            (_thisFlowMeanwhile, _otherFlow).Should().Be(("Host=outer", "Host=other"));

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    [TestFixture]
    public class Given_resolution_fails
    {
        private DmsAdminConnectionResolution _noResolver = null!;
        private DmsAdminConnectionResolution _thrown = null!;
        private DmsAdminConnectionResolution _empty = null!;

        [SetUp]
        public async Task Setup()
        {
            _noResolver = await Source(Referenced, resolver: null).ResolveAsync(CancellationToken.None);
            _thrown = await Source(
                    Referenced,
                    new ScriptedResolver(
                        (_, _) => Task.FromException<string>(new InvalidOperationException("secret text"))
                    )
                )
                .ResolveAsync(CancellationToken.None);
            _empty = await Source(Referenced, new ScriptedResolver((_, _) => Task.FromResult(string.Empty)))
                .ResolveAsync(CancellationToken.None);
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
        public void It_reports_a_resolver_exception_by_its_type_only() =>
            _thrown
                .Should()
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
                .Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.EmptyValueStep)
                    )
                );
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
            CancellationToken resolverToken = await called.Task.WaitAsync(_bound);
            time.Advance(_resolveTimeout);
            _timedOut = await pending.WaitAsync(_bound);
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
            await calledAgain.Task.WaitAsync(_bound);
            await caller.CancelAsync();
            _cancelled = () => cancelledCall.WaitAsync(_bound);
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

    /// <summary>
    /// Step 3.9 review: a resolver that ignores cancellation must not hold the cache. The first resolution
    /// times out while the call is still pending; the next resolution asks the resolver again and succeeds;
    /// then the abandoned call returns a value, which must not replace the one the retry cached.
    /// </summary>
    [TestFixture]
    public class Given_a_fetch_times_out_and_the_resolver_answers_late
    {
        private DmsAdminConnectionResolution _timedOut = null!;
        private string _retry = null!;
        private string _afterLateAnswer = null!;
        private int _callsAfterRetry;
        private int _callsAtEnd;

        [SetUp]
        public async Task Setup()
        {
            FakeTimeProvider time = new();
            TaskCompletionSource<string> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            ScriptedResolver resolver = new(
                (_, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        firstCall.TrySetResult();
                        return late.Task;
                    }

                    return Task.FromResult("fresh");
                }
            );
            DmsAdminConnectionSource source = Source(Referenced, resolver, time);

            Task<DmsAdminConnectionResolution> pending = source.ResolveAsync(CancellationToken.None);
            await firstCall.Task.WaitAsync(_bound);
            time.Advance(_resolveTimeout);
            _timedOut = await pending.WaitAsync(_bound);

            _retry = ValueOf(
                ConnectionOf(await source.ResolveAsync(CancellationToken.None).WaitAsync(_bound)),
                "Password"
            );
            _callsAfterRetry = resolver.Count;

            late.SetResult("late");
            await source.AbandonedCallObserved.WaitAsync(_bound);
            _afterLateAnswer = ValueOf(
                ConnectionOf(await source.ResolveAsync(CancellationToken.None).WaitAsync(_bound)),
                "Password"
            );
            _callsAtEnd = resolver.Count;
        }

        [Test]
        public void It_times_out_the_first_resolution() =>
            _timedOut
                .Should()
                .Be(
                    new DmsAdminConnectionResolution.Failed(
                        DmsProvisioningDiagnostic.ForOperation(DmsAdminConnectionSource.TimedOutStep)
                    )
                );

        [Test]
        public void It_asks_the_resolver_again_on_retry_and_succeeds() =>
            (_retry, _callsAfterRetry).Should().Be(("fresh", 2));

        [Test]
        public void It_drops_the_late_answer_and_keeps_serving_the_cached_retry() =>
            (_afterLateAnswer, _callsAtEnd).Should().Be(("fresh", 2));
    }
}
