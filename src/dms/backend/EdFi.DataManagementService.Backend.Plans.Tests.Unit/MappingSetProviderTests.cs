// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetProvider
{
    private static readonly MappingSetKey _testKey = new(new string('a', 64), SqlDialect.Pgsql, "v1");

    private static MappingSet CreateTestMappingSet(MappingSetKey key)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var resourceKeyEntry = new ResourceKeyEntry(1, resource, "5.0.0", false);
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: key.RelationalMappingVersion,
            EffectiveSchemaHash: key.EffectiveSchemaHash,
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, new string('b', 64)),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchema,
            Dialect: key.Dialect,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.0.0", false, new DbSchemaName("edfi")),
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

    private static MappingSetProvider CreateProvider(
        MappingSetProviderOptions? options = null,
        IMappingPackStore? packStore = null,
        IRuntimeMappingSetCompiler? compiler = null
    )
    {
        return new MappingSetProvider(
            packStore ?? new NoOpMappingPackStore(),
            compiler is not null ? [compiler] : [],
            Options.Create(options ?? new MappingSetProviderOptions()),
            NullLogger<MappingSetProvider>.Instance
        );
    }

    [TestFixture]
    public class Given_PacksEnabled_Is_False : Given_MappingSetProvider
    {
        [Test]
        public async Task It_skips_pack_store_and_compiles_at_runtime()
        {
            var expectedMappingSet = CreateTestMappingSet(_testKey);
            var compileCount = 0;
            var compiler = new TestRuntimeCompiler(
                _testKey,
                () =>
                {
                    Interlocked.Increment(ref compileCount);
                    return Task.FromResult(expectedMappingSet);
                }
            );

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { PacksEnabled = false },
                compiler: compiler
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
            compileCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_PacksEnabled_And_Pack_Not_Found_And_Fallback_Allowed : Given_MappingSetProvider
    {
        [Test]
        public async Task It_falls_back_to_runtime_compilation()
        {
            var expectedMappingSet = CreateTestMappingSet(_testKey);
            var compiler = new TestRuntimeCompiler(_testKey, () => Task.FromResult(expectedMappingSet));

            var provider = CreateProvider(
                options: new MappingSetProviderOptions
                {
                    PacksEnabled = true,
                    AllowRuntimeCompileFallback = true,
                },
                packStore: new NoOpMappingPackStore(),
                compiler: compiler
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
        }
    }

    [TestFixture]
    public class Given_PacksEnabled_And_PacksRequired_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { PacksEnabled = true, PacksRequired = true },
                packStore: new NoOpMappingPackStore()
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<MappingSetUnavailableException>()
                .WithMessage("*required but not found*");
        }
    }

    [TestFixture]
    public class Given_PacksEnabled_And_Fallback_Not_Allowed_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var provider = CreateProvider(
                options: new MappingSetProviderOptions
                {
                    PacksEnabled = true,
                    PacksRequired = false,
                    AllowRuntimeCompileFallback = false,
                },
                packStore: new NoOpMappingPackStore()
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<MappingSetUnavailableException>()
                .WithMessage("*runtime compilation fallback is disabled*");
        }
    }

    [TestFixture]
    public class Given_No_Compiler_Registered_For_Dialect : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var provider = CreateProvider(options: new MappingSetProviderOptions { PacksEnabled = false });

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<MappingSetUnavailableException>()
                .WithMessage("*No runtime mapping set compiler is registered*");
        }
    }

    [TestFixture]
    public class Given_Second_Call_For_Same_Key : Given_MappingSetProvider
    {
        [Test]
        public async Task It_returns_cached_mapping_set_without_recompiling()
        {
            var expectedMappingSet = CreateTestMappingSet(_testKey);
            var compileCount = 0;
            var compiler = new TestRuntimeCompiler(
                _testKey,
                () =>
                {
                    Interlocked.Increment(ref compileCount);
                    return Task.FromResult(expectedMappingSet);
                }
            );

            var provider = CreateProvider(compiler: compiler);

            var first = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);
            var second = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            first.Should().BeSameAs(expectedMappingSet);
            second.Should().BeSameAs(expectedMappingSet);
            compileCount.Should().Be(1);
        }
    }

    private sealed class TestRuntimeCompiler(MappingSetKey currentKey, Func<Task<MappingSet>> compileFunc)
        : IRuntimeMappingSetCompiler
    {
        public SqlDialect Dialect => currentKey.Dialect;

        public MappingSetKey GetCurrentKey() => currentKey;

        public Task<MappingSet> CompileAsync(MappingSetKey expectedKey, CancellationToken cancellationToken)
        {
            if (expectedKey != currentKey)
            {
                throw new InvalidOperationException(
                    $"Key mismatch: expected {expectedKey}, got {currentKey}"
                );
            }
            return compileFunc();
        }
    }
}
