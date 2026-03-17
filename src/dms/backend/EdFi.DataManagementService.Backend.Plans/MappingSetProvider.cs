// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Backend.External.LogSanitizer;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Coordinates mapping set selection: pack loading (when enabled), runtime compilation
/// fallback (when allowed), and in-process caching via <see cref="MappingSetCache"/>.
/// </summary>
public sealed class MappingSetProvider : IMappingSetProvider
{
    private readonly IMappingPackStore _packStore;
    private readonly FrozenDictionary<SqlDialect, IRuntimeMappingSetCompiler> _compilersByDialect;
    private readonly MappingSetProviderOptions _options;
    private readonly MappingSetCache _cache;
    private readonly ILogger<MappingSetProvider> _logger;

    public MappingSetProvider(
        IMappingPackStore packStore,
        IEnumerable<IRuntimeMappingSetCompiler> compilers,
        IOptions<MappingSetProviderOptions> options,
        ILogger<MappingSetProvider> logger
    )
    {
        _packStore = packStore ?? throw new ArgumentNullException(nameof(packStore));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _compilersByDialect = (
            compilers ?? throw new ArgumentNullException(nameof(compilers))
        ).ToFrozenDictionary(c => c.Dialect);

        _cache = new MappingSetCache(LoadOrCompileAsync);
    }

    /// <inheritdoc />
    public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync(key, cancellationToken);
    }

    private async Task<MappingSet> LoadOrCompileAsync(MappingSetKey key)
    {
        if (_options.PacksEnabled)
        {
            var payload = await _packStore
                .TryLoadPayloadAsync(key, CancellationToken.None)
                .ConfigureAwait(false);

            if (payload is not null)
            {
                _logger.LogInformation(
                    "Loaded mapping pack for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
                    SanitizeForLog(key.EffectiveSchemaHash),
                    key.Dialect,
                    SanitizeForLog(key.RelationalMappingVersion)
                );
                return MappingSet.FromPayload(payload);
            }

            if (_options.PacksRequired)
            {
                throw new MappingSetUnavailableException(
                    $"Mapping pack is required but not found for EffectiveSchemaHash '{key.EffectiveSchemaHash}', "
                        + $"Dialect '{key.Dialect}', RelationalMappingVersion '{key.RelationalMappingVersion}'. "
                        + "Ensure a matching .mpack file is available in the configured pack root path, "
                        + "or set PacksRequired=false to allow runtime compilation fallback."
                );
            }

            if (!_options.AllowRuntimeCompileFallback)
            {
                throw new MappingSetUnavailableException(
                    $"Mapping pack not found for EffectiveSchemaHash '{key.EffectiveSchemaHash}', "
                        + $"Dialect '{key.Dialect}', RelationalMappingVersion '{key.RelationalMappingVersion}', "
                        + "and runtime compilation fallback is disabled. "
                        + "Provide a matching .mpack file or enable AllowRuntimeCompileFallback."
                );
            }

            _logger.LogInformation(
                "Mapping pack not found for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}; falling back to runtime compilation",
                SanitizeForLog(key.EffectiveSchemaHash),
                key.Dialect
            );
        }

        return await RuntimeCompileAsync(key).ConfigureAwait(false);
    }

    private async Task<MappingSet> RuntimeCompileAsync(MappingSetKey key)
    {
        if (!_compilersByDialect.TryGetValue(key.Dialect, out var compiler))
        {
            throw new MappingSetUnavailableException(
                $"No runtime mapping set compiler is registered for dialect '{key.Dialect}'. "
                    + "Ensure the backend for the target dialect is configured."
            );
        }

        _logger.LogInformation(
            "Compiling runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
            SanitizeForLog(key.EffectiveSchemaHash),
            key.Dialect,
            SanitizeForLog(key.RelationalMappingVersion)
        );

        return await compiler.CompileAsync(key, CancellationToken.None).ConfigureAwait(false);
    }
}
