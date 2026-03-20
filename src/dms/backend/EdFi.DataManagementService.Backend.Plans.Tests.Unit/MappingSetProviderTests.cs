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
    public class Given_Enabled_And_Pack_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_wraps_decode_failure_in_MappingSetUnavailableException()
        {
            var packStore = new TestPackStore(new MappingPackPayload());

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true },
                packStore: packStore
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            // FromPayload is not yet implemented (deferred to DMS-968), so it throws
            // NotSupportedException internally. MappingSetProvider wraps decode failures
            // in MappingSetUnavailableException so the middleware can give actionable guidance.
            var ex = await act.Should()
                .ThrowAsync<MappingSetUnavailableException>()
                .WithMessage("*Failed to decode mapping pack*");

            ex.And.InnerException.Should().BeOfType<NotSupportedException>();
        }

        [Test]
        public async Task It_includes_key_diagnostics_on_decode_failure()
        {
            var packStore = new TestPackStore(new MappingPackPayload());
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true },
                packStore: packStore
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.EffectiveSchemaHash));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.Dialect.ToString()));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.RelationalMappingVersion));
        }
    }

    [TestFixture]
    public class Given_Enabled_Is_False : Given_MappingSetProvider
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
                options: new MappingSetProviderOptions { Enabled = false },
                compiler: compiler
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
            compileCount.Should().Be(1);
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Pack_Not_Found_And_Fallback_Allowed : Given_MappingSetProvider
    {
        [Test]
        public async Task It_falls_back_to_runtime_compilation()
        {
            var expectedMappingSet = CreateTestMappingSet(_testKey);
            var compiler = new TestRuntimeCompiler(_testKey, () => Task.FromResult(expectedMappingSet));

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, AllowRuntimeCompileFallback = true },
                packStore: new NoOpMappingPackStore(),
                compiler: compiler
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Required_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, Required = true },
                packStore: new NoOpMappingPackStore()
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            await act.Should()
                .ThrowAsync<MappingSetUnavailableException>()
                .WithMessage("*required but not found*");
        }

        [Test]
        public async Task It_includes_key_diagnostics_when_pack_required_but_missing()
        {
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, Required = true },
                packStore: new NoOpMappingPackStore()
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.EffectiveSchemaHash));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.Dialect.ToString()));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.RelationalMappingVersion));
            ex.Diagnostics.Should().Contain(d => d.Contains("required but not found"));
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Fallback_Not_Allowed_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var provider = CreateProvider(
                options: new MappingSetProviderOptions
                {
                    Enabled = true,
                    Required = false,
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
            var provider = CreateProvider(options: new MappingSetProviderOptions { Enabled = false });

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

    [TestFixture]
    public class Given_No_Compiler_Registered_Diagnostics : Given_MappingSetProvider
    {
        [Test]
        public async Task It_includes_key_diagnostics_when_no_compiler()
        {
            var provider = CreateProvider(options: new MappingSetProviderOptions { Enabled = false });

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.EffectiveSchemaHash));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.Dialect.ToString()));
            ex.Diagnostics.Should().Contain(d => d.Contains("no compiler registered"));
        }
    }

    [TestFixture]
    public class Given_Runtime_Compilation_Failure_Diagnostics : Given_MappingSetProvider
    {
        [Test]
        public async Task It_includes_key_diagnostics_when_compilation_fails()
        {
            var compiler = new TestRuntimeCompiler(
                _testKey,
                () => throw new InvalidOperationException("simulated compile error")
            );
            var provider = CreateProvider(compiler: compiler);

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.EffectiveSchemaHash));
            ex.Diagnostics.Should().Contain(d => d.Contains(_testKey.Dialect.ToString()));
            ex.Diagnostics.Should().Contain(d => d.Contains("see server logs for details"));
        }
    }

    [TestFixture]
    public class Given_Failure_For_One_Key_Does_Not_Block_Another : Given_MappingSetProvider
    {
        [Test]
        public async Task It_succeeds_for_key_B_after_key_A_fails()
        {
            var keyA = new MappingSetKey(new string('a', 64), SqlDialect.Pgsql, "v1");
            var keyB = new MappingSetKey(new string('b', 64), SqlDialect.Pgsql, "v1");
            var expectedMappingSet = CreateTestMappingSet(keyB);

            var compiler = new TestRuntimeCompiler(keyB, () => Task.FromResult(expectedMappingSet));

            // No compiler for keyA's dialect match, but the compiler only accepts keyB.
            // keyA will fail because the compiler rejects mismatched keys.
            var provider = CreateProvider(compiler: compiler);

            var actA = () => provider.GetOrCreateAsync(keyA, CancellationToken.None);
            await actA.Should().ThrowAsync<MappingSetUnavailableException>();

            // keyB should succeed despite keyA's failure
            var result = await provider.GetOrCreateAsync(keyB, CancellationToken.None);
            result.Should().BeSameAs(expectedMappingSet);
        }
    }

    [TestFixture]
    public class Given_Pack_Decode_Failure_For_One_Key_Does_Not_Block_Another : Given_MappingSetProvider
    {
        [Test]
        public async Task It_succeeds_for_key_B_via_runtime_compile_after_key_A_pack_decode_fails()
        {
            var keyA = new MappingSetKey(new string('a', 64), SqlDialect.Pgsql, "v1");
            var keyB = new MappingSetKey(new string('b', 64), SqlDialect.Pgsql, "v1");
            var expectedMappingSet = CreateTestMappingSet(keyB);

            // Pack store returns a payload only for keyA (which will fail to decode
            // since MappingSet.FromPayload is not implemented).
            var packStore = new SelectivePackStore(keyA, new MappingPackPayload());
            var compiler = new TestRuntimeCompiler(keyB, () => Task.FromResult(expectedMappingSet));

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, AllowRuntimeCompileFallback = true },
                packStore: packStore,
                compiler: compiler
            );

            // keyA: pack found but decode fails → MappingSetUnavailableException
            var actA = () => provider.GetOrCreateAsync(keyA, CancellationToken.None);
            await actA.Should().ThrowAsync<MappingSetUnavailableException>();

            // keyB: no pack → falls back to runtime compile → succeeds
            var result = await provider.GetOrCreateAsync(keyB, CancellationToken.None);
            result.Should().BeSameAs(expectedMappingSet);
        }
    }

    [TestFixture]
    public class Given_Failure_Cooldown_Prevents_Retry_Storm : Given_MappingSetProvider
    {
        [Test]
        public async Task It_returns_cached_failure_within_cooldown_period()
        {
            var compileCount = 0;
            var compiler = new TestRuntimeCompiler(
                _testKey,
                () =>
                {
                    Interlocked.Increment(ref compileCount);
                    throw new InvalidOperationException("simulated compile error");
                }
            );

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { FailureCooldownSeconds = 30 },
                compiler: compiler
            );

            // First call triggers compilation, which fails
            var act1 = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);
            await act1.Should().ThrowAsync<MappingSetUnavailableException>();

            // Second call within cooldown should see cached failure, not recompile
            var act2 = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);
            await act2.Should().ThrowAsync<MappingSetUnavailableException>();

            compileCount.Should().Be(1, "compilation should happen only once within cooldown period");
        }
    }

    private sealed class SelectivePackStore(MappingSetKey targetKey, MappingPackPayload payload)
        : IMappingPackStore
    {
        public Task<MappingPackPayload?> TryLoadPayloadAsync(
            MappingSetKey key,
            CancellationToken cancellationToken
        ) => Task.FromResult(key == targetKey ? payload : null);
    }

    private sealed class TestPackStore(MappingPackPayload? payload) : IMappingPackStore
    {
        public Task<MappingPackPayload?> TryLoadPayloadAsync(
            MappingSetKey key,
            CancellationToken cancellationToken
        ) => Task.FromResult(payload);
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
