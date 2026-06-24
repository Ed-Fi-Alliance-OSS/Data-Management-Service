// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetProvider
{
    private static readonly MappingSetKey _testKey = new(new string('a', 64), SqlDialect.Pgsql, "v1");

    private static string ExpectedKeyForMessage(MappingSetKey key) =>
        $"EffectiveSchemaHash '{key.EffectiveSchemaHash}', Dialect '{key.Dialect}', RelationalMappingVersion '{key.RelationalMappingVersion}'";

    private static string[] ExpectedKeyDiagnostics(MappingSetKey key) =>
        [
            $"EffectiveSchemaHash: {key.EffectiveSchemaHash}",
            $"Dialect: {key.Dialect}",
            $"RelationalMappingVersion: {key.RelationalMappingVersion}",
        ];

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
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static MappingSetProvider CreateProvider(
        MappingSetProviderOptions? options = null,
        IMappingPackStore? packStore = null,
        IRuntimeMappingSetCompiler? compiler = null,
        ILogger<MappingSetProvider>? logger = null
    )
    {
        return new MappingSetProvider(
            packStore ?? new NoOpMappingPackStore(),
            compiler is not null ? [compiler] : [],
            Options.Create(options ?? new MappingSetProviderOptions()),
            logger ?? NullLogger<MappingSetProvider>.Instance
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
            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Message.Should()
                .Be(
                    $"Failed to decode mapping pack for {ExpectedKeyForMessage(_testKey)}. The pack file may be corrupt or incompatible with the current version."
                );
            ex.InnerException.Should().BeOfType<NotSupportedException>();
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
            string[] expectedDiagnostics =
            [
                .. ExpectedKeyDiagnostics(_testKey),
                "Pack status: found but failed to decode",
                "Suggested action: Rebuild the .mpack file or enable AllowRuntimeCompileFallback.",
            ];

            ex.Diagnostics.Should().Equal(expectedDiagnostics);
        }

        [Test]
        public async Task It_logs_loaded_pack_before_decode_failure()
        {
            var packStore = new TestPackStore(new MappingPackPayload());
            var logger = new CapturingLogger<MappingSetProvider>();
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true },
                packStore: packStore,
                logger: logger
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            await act.Should().ThrowAsync<MappingSetUnavailableException>();

            logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Level == LogLevel.Information
                    && record.Message
                        == $"Loaded mapping pack for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}"
                );
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
            var logger = new CapturingLogger<MappingSetProvider>();
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
                compiler: compiler,
                logger: logger
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
            compileCount.Should().Be(1);
            LogRecord[] expectedLogs =
            [
                new(
                    LogLevel.Information,
                    $"Compiling runtime mapping set for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}",
                    null
                ),
                new(
                    LogLevel.Information,
                    $"Runtime mapping set compiled successfully for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}",
                    null
                ),
            ];

            logger.Records.Should().Equal(expectedLogs);
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Pack_Not_Found_And_Fallback_Allowed : Given_MappingSetProvider
    {
        [Test]
        public async Task It_falls_back_to_runtime_compilation()
        {
            var expectedMappingSet = CreateTestMappingSet(_testKey);
            var logger = new CapturingLogger<MappingSetProvider>();
            var compiler = new TestRuntimeCompiler(_testKey, () => Task.FromResult(expectedMappingSet));

            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, AllowRuntimeCompileFallback = true },
                packStore: new NoOpMappingPackStore(),
                compiler: compiler,
                logger: logger
            );

            var result = await provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            result.Should().BeSameAs(expectedMappingSet);
            LogRecord[] expectedLogs =
            [
                new(
                    LogLevel.Information,
                    $"Mapping pack not found for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}; falling back to runtime compilation",
                    null
                ),
                new(
                    LogLevel.Information,
                    $"Compiling runtime mapping set for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}",
                    null
                ),
                new(
                    LogLevel.Information,
                    $"Runtime mapping set compiled successfully for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}",
                    null
                ),
            ];

            logger.Records.Should().Equal(expectedLogs);
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Required_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var logger = new CapturingLogger<MappingSetProvider>();
            var provider = CreateProvider(
                options: new MappingSetProviderOptions { Enabled = true, Required = true },
                packStore: new NoOpMappingPackStore(),
                logger: logger
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Message.Should()
                .Be($"Mapping pack is required but not found for {ExpectedKeyForMessage(_testKey)}.");
            logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Level == LogLevel.Warning
                    && record.Message
                        == $"Mapping pack required but not found for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}, RelationalMappingVersion {_testKey.RelationalMappingVersion}"
                );
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
            string[] expectedDiagnostics =
            [
                .. ExpectedKeyDiagnostics(_testKey),
                "Pack status: required but not found",
                "Suggested action: Provide a matching .mpack file or set Required=false.",
            ];

            ex.Diagnostics.Should().Equal(expectedDiagnostics);
        }
    }

    [TestFixture]
    public class Given_Enabled_And_Fallback_Not_Allowed_And_Pack_Not_Found : Given_MappingSetProvider
    {
        [Test]
        public async Task It_throws_MappingSetUnavailableException()
        {
            var logger = new CapturingLogger<MappingSetProvider>();
            var provider = CreateProvider(
                options: new MappingSetProviderOptions
                {
                    Enabled = true,
                    Required = false,
                    AllowRuntimeCompileFallback = false,
                },
                packStore: new NoOpMappingPackStore(),
                logger: logger
            );

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Message.Should()
                .Be(
                    $"Mapping pack not found for {ExpectedKeyForMessage(_testKey)}, and runtime compilation fallback is disabled."
                );
            string[] expectedDiagnostics =
            [
                .. ExpectedKeyDiagnostics(_testKey),
                "Pack status: not found, fallback disabled",
                "Suggested action: Provide a matching .mpack file or enable AllowRuntimeCompileFallback.",
            ];

            ex.Diagnostics.Should().Equal(expectedDiagnostics);
            logger
                .Records.Should()
                .ContainSingle(record =>
                    record.Level == LogLevel.Warning
                    && record.Message
                        == $"Mapping pack not found and runtime compilation fallback is disabled for EffectiveSchemaHash {_testKey.EffectiveSchemaHash}, Dialect {_testKey.Dialect}"
                );
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

            ex.Message.Should()
                .Be($"No runtime mapping set compiler is registered for dialect '{_testKey.Dialect}'.");
            string[] expectedDiagnostics =
            [
                .. ExpectedKeyDiagnostics(_testKey),
                "Compiler status: no compiler registered for dialect",
                "Suggested action: Ensure the backend for the target dialect is configured.",
            ];

            ex.Diagnostics.Should().Equal(expectedDiagnostics);
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

            ex.Message.Should()
                .Be(
                    $"Runtime compilation failed for {ExpectedKeyForMessage(_testKey)}: simulated compile error"
                );
            ex.InnerException.Should().BeOfType<InvalidOperationException>();
            string[] expectedDiagnostics =
            [
                .. ExpectedKeyDiagnostics(_testKey),
                "Compilation error: see server logs for details.",
                "Suggested action: Check server logs for the full stack trace.",
            ];

            ex.Diagnostics.Should().Equal(expectedDiagnostics);
        }
    }

    [TestFixture]
    public class Given_Runtime_Compiler_Raises_MappingSetUnavailableException : Given_MappingSetProvider
    {
        [Test]
        public async Task It_preserves_the_original_exception()
        {
            var expected = new MappingSetUnavailableException(
                "compiler classified failure",
                ["compiler diagnostic"]
            );
            var compiler = new TestRuntimeCompiler(_testKey, () => throw expected);
            var provider = CreateProvider(compiler: compiler);

            var act = () => provider.GetOrCreateAsync(_testKey, CancellationToken.None);

            var ex = (await act.Should().ThrowAsync<MappingSetUnavailableException>()).And;

            ex.Should().BeSameAs(expected);
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogRecord> _records = [];

        public IReadOnlyList<LogRecord> Records => _records;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _records.Add(new LogRecord(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);
}
