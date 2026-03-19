// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetCache
{
    [Test]
    public async Task It_should_compile_only_once_per_key_under_concurrency()
    {
        var key = CreateMappingSetKey(new string('a', 64), SqlDialect.Pgsql, "v1");
        var compiledMappingSet = CreateMappingSet(key);
        var compilationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCompilation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (cacheKey, _) =>
            {
                Interlocked.Increment(ref compileInvocationCount);
                cacheKey.Should().Be(key);
                compilationStarted.TrySetResult(true);
                await releaseCompilation.Task;
                return compiledMappingSet;
            }
        );

        var callers = Enumerable
            .Range(0, 20)
            .Select(_ => cache.GetOrCreateAsync(key, CancellationToken.None))
            .ToArray();

        await compilationStarted.Task;
        releaseCompilation.SetResult(true);

        var results = await Task.WhenAll(callers);

        compileInvocationCount.Should().Be(1);
        results.Should().OnlyContain(result => ReferenceEquals(result, compiledMappingSet));

        var cachedResult = await cache.GetOrCreateAsync(key, CancellationToken.None);
        cachedResult.Should().BeSameAs(compiledMappingSet);
        compileInvocationCount.Should().Be(1);
    }

    [Test]
    public async Task It_should_evict_faulted_entry_and_retry_compilation()
    {
        var key = CreateMappingSetKey(new string('b', 64), SqlDialect.Mssql, "v1");
        var compiledMappingSet = CreateMappingSet(key);
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (_, _) =>
            {
                var invocationCount = Interlocked.Increment(ref compileInvocationCount);
                await Task.Yield();

                if (invocationCount == 1)
                {
                    throw new InvalidOperationException("failed to compile mapping set");
                }

                return compiledMappingSet;
            },
            failureCooldown: TimeSpan.Zero
        );

        var firstAct = async () => await cache.GetOrCreateAsync(key, CancellationToken.None);
        await firstAct
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("failed to compile mapping set");

        // Allow the fire-and-forget eviction (Task.Delay(Zero)) to complete.
        await Task.Delay(50);

        var secondResult = await cache.GetOrCreateAsync(key, CancellationToken.None);
        secondResult.Should().BeSameAs(compiledMappingSet);

        compileInvocationCount.Should().Be(2);
    }

    [Test]
    public async Task It_should_share_a_single_faulted_compile_before_retrying()
    {
        var key = CreateMappingSetKey(new string('e', 64), SqlDialect.Pgsql, "v1");
        var compiledMappingSet = CreateMappingSet(key);
        var compilationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCompilation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (_, _) =>
            {
                var invocationCount = Interlocked.Increment(ref compileInvocationCount);
                compilationStarted.TrySetResult(true);
                await releaseCompilation.Task;

                if (invocationCount == 1)
                {
                    throw new InvalidOperationException("failed to compile mapping set");
                }

                return compiledMappingSet;
            },
            failureCooldown: TimeSpan.Zero
        );

        var firstCallers = Enumerable
            .Range(0, 8)
            .Select(_ => cache.GetOrCreateAsync(key, CancellationToken.None))
            .ToArray();

        await compilationStarted.Task;
        releaseCompilation.SetResult(true);

        foreach (var caller in firstCallers)
        {
            var failedAct = async () => await caller;
            await failedAct
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("failed to compile mapping set");
        }

        compileInvocationCount.Should().Be(1);

        // Allow the fire-and-forget eviction (Task.Delay(Zero)) to complete.
        await Task.Delay(50);

        var retryResult = await cache.GetOrCreateAsync(key, CancellationToken.None);
        retryResult.Should().BeSameAs(compiledMappingSet);
        compileInvocationCount.Should().Be(2);
    }

    [Test]
    public async Task It_should_cancel_waiting_without_canceling_or_poisoning_compilation()
    {
        var key = CreateMappingSetKey(new string('c', 64), SqlDialect.Pgsql, "v1");
        var compiledMappingSet = CreateMappingSet(key);
        var compilationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCompilation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (_, _) =>
            {
                Interlocked.Increment(ref compileInvocationCount);
                compilationStarted.TrySetResult(true);
                await releaseCompilation.Task;
                return compiledMappingSet;
            }
        );

        using var cancellationSource = new CancellationTokenSource();

        var canceledCallerTask = cache.GetOrCreateAsync(key, cancellationSource.Token);

        await compilationStarted.Task;
        await cancellationSource.CancelAsync();

        var canceledAct = async () => await canceledCallerTask;
        await canceledAct.Should().ThrowAsync<OperationCanceledException>();

        releaseCompilation.SetResult(true);

        var completedResult = await cache.GetOrCreateAsync(key, CancellationToken.None);
        completedResult.Should().BeSameAs(compiledMappingSet);
        compileInvocationCount.Should().Be(1);
    }

    [Test]
    public async Task It_should_evict_and_retry_after_OperationCanceledException()
    {
        var key = CreateMappingSetKey(new string('f', 64), SqlDialect.Pgsql, "v1");
        var compiledMappingSet = CreateMappingSet(key);
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (_, _) =>
            {
                var invocationCount = Interlocked.Increment(ref compileInvocationCount);
                await Task.Yield();

                if (invocationCount == 1)
                {
                    throw new OperationCanceledException("simulated cancellation");
                }

                return compiledMappingSet;
            }
        );

        var firstAct = async () => await cache.GetOrCreateAsync(key, CancellationToken.None);
        await firstAct.Should().ThrowAsync<OperationCanceledException>();

        // OperationCanceledException evicts immediately (no cooldown), so retry works right away.
        var secondResult = await cache.GetOrCreateAsync(key, CancellationToken.None);
        secondResult.Should().BeSameAs(compiledMappingSet);

        compileInvocationCount.Should().Be(2);
    }

    [Test]
    public async Task It_should_return_cached_failure_during_cooldown_period()
    {
        var key = CreateMappingSetKey(new string('g', 64), SqlDialect.Pgsql, "v1");
        var compileInvocationCount = 0;

        var cache = new MappingSetCache(
            async (_, _) =>
            {
                Interlocked.Increment(ref compileInvocationCount);
                await Task.Yield();
                throw new InvalidOperationException("permanent failure");
            },
            failureCooldown: TimeSpan.FromSeconds(30)
        );

        var firstAct = async () => await cache.GetOrCreateAsync(key, CancellationToken.None);
        await firstAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("permanent failure");

        // Within the 30s cooldown, the faulted entry is still cached.
        // The second call should see the cached failure, not trigger recompilation.
        var secondAct = async () => await cache.GetOrCreateAsync(key, CancellationToken.None);
        await secondAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("permanent failure");

        compileInvocationCount.Should().Be(1, "compilation should happen only once during cooldown");
    }

    private static MappingSetKey CreateMappingSetKey(
        string effectiveSchemaHash,
        SqlDialect dialect,
        string relationalMappingVersion
    )
    {
        return new MappingSetKey(
            EffectiveSchemaHash: effectiveSchemaHash,
            Dialect: dialect,
            RelationalMappingVersion: relationalMappingVersion
        );
    }

    private static MappingSet CreateMappingSet(MappingSetKey key)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var resourceKeyEntry = new ResourceKeyEntry(101, resource, "5.2.0", false);
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2",
            RelationalMappingVersion: key.RelationalMappingVersion,
            EffectiveSchemaHash: key.EffectiveSchemaHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: CreateResourceKeySeedHash(),
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "5.2.0",
                    IsExtensionProject: false,
                    ProjectHash: new string('d', 64)
                ),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: key.Dialect,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "5.2.0",
                    IsExtensionProject: false,
                    PhysicalSchema: new DbSchemaName("edfi")
                ),
            ],
            ConcreteResourcesInNameOrder: [],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: key,
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKeyEntry.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKeyEntry.ResourceKeyId] = resourceKeyEntry,
            }
        );
    }

    private static byte[] CreateResourceKeySeedHash()
    {
        var hash = new byte[32];

        for (var index = 0; index < hash.Length; index++)
        {
            hash[index] = (byte)index;
        }

        return hash;
    }
}
