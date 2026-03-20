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

        _cache = new MappingSetCache(
            LoadOrCompileAsync,
            _logger,
            TimeSpan.FromSeconds(_options.FailureCooldownSeconds)
        );
    }

    /// <inheritdoc />
    public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        return _cache.GetOrCreateAsync(key, cancellationToken);
    }

    // Sanitized single-line format for Exception.Message and log entries.
    // Distinct from BuildKeyDiagnostics which produces structured unsanitized entries.
    private static string FormatKeyForMessage(MappingSetKey key) =>
        $"EffectiveSchemaHash '{SanitizeForLog(key.EffectiveSchemaHash)}', "
        + $"Dialect '{key.Dialect}', RelationalMappingVersion '{SanitizeForLog(key.RelationalMappingVersion)}'";

    // Diagnostics are intentionally unsanitized: they carry verbatim key fields for
    // operator troubleshooting in the HTTP response body. Log messages use SanitizeForLog.
    private static string[] BuildKeyDiagnostics(MappingSetKey key) =>
        [
            $"EffectiveSchemaHash: {key.EffectiveSchemaHash}",
            $"Dialect: {key.Dialect}",
            $"RelationalMappingVersion: {key.RelationalMappingVersion}",
        ];

    private async Task<MappingSet> LoadOrCompileAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            var payload = await _packStore.TryLoadPayloadAsync(key, cancellationToken).ConfigureAwait(false);

            if (payload is not null)
            {
                _logger.LogInformation(
                    "Loaded mapping pack for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
                    SanitizeForLog(key.EffectiveSchemaHash),
                    key.Dialect,
                    SanitizeForLog(key.RelationalMappingVersion)
                );

                try
                {
                    return MappingSet.FromPayload(payload);
                }
                catch (Exception ex)
                {
                    throw new MappingSetUnavailableException(
                        $"Failed to decode mapping pack for {FormatKeyForMessage(key)}. "
                            + "The pack file may be corrupt or incompatible with the current version.",
                        [
                            .. BuildKeyDiagnostics(key),
                            "Pack status: found but failed to decode",
                            "Suggested action: Rebuild the .mpack file or enable AllowRuntimeCompileFallback.",
                        ],
                        ex
                    );
                }
            }

            if (_options.Required)
            {
                _logger.LogWarning(
                    "Mapping pack required but not found for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
                    SanitizeForLog(key.EffectiveSchemaHash),
                    key.Dialect,
                    SanitizeForLog(key.RelationalMappingVersion)
                );

                throw new MappingSetUnavailableException(
                    $"Mapping pack is required but not found for {FormatKeyForMessage(key)}.",
                    [
                        .. BuildKeyDiagnostics(key),
                        "Pack status: required but not found",
                        "Suggested action: Provide a matching .mpack file or set Required=false.",
                    ]
                );
            }

            if (!_options.AllowRuntimeCompileFallback)
            {
                _logger.LogWarning(
                    "Mapping pack not found and runtime compilation fallback is disabled for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}",
                    SanitizeForLog(key.EffectiveSchemaHash),
                    key.Dialect
                );

                throw new MappingSetUnavailableException(
                    $"Mapping pack not found for {FormatKeyForMessage(key)}, "
                        + "and runtime compilation fallback is disabled.",
                    [
                        .. BuildKeyDiagnostics(key),
                        "Pack status: not found, fallback disabled",
                        "Suggested action: Provide a matching .mpack file or enable AllowRuntimeCompileFallback.",
                    ]
                );
            }

            _logger.LogInformation(
                "Mapping pack not found for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}; falling back to runtime compilation",
                SanitizeForLog(key.EffectiveSchemaHash),
                key.Dialect
            );
        }

        return await RuntimeCompileAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MappingSet> RuntimeCompileAsync(MappingSetKey key, CancellationToken cancellationToken)
    {
        if (!_compilersByDialect.TryGetValue(key.Dialect, out var compiler))
        {
            throw new MappingSetUnavailableException(
                $"No runtime mapping set compiler is registered for dialect '{key.Dialect}'.",
                [
                    .. BuildKeyDiagnostics(key),
                    "Compiler status: no compiler registered for dialect",
                    "Suggested action: Ensure the backend for the target dialect is configured.",
                ]
            );
        }

        _logger.LogInformation(
            "Compiling runtime mapping set for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
            SanitizeForLog(key.EffectiveSchemaHash),
            key.Dialect,
            SanitizeForLog(key.RelationalMappingVersion)
        );

        try
        {
            var result = await compiler.CompileAsync(key, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Runtime mapping set compiled successfully for EffectiveSchemaHash {EffectiveSchemaHash}, Dialect {Dialect}, RelationalMappingVersion {RelationalMappingVersion}",
                SanitizeForLog(key.EffectiveSchemaHash),
                key.Dialect,
                SanitizeForLog(key.RelationalMappingVersion)
            );

            return result;
        }
        catch (MappingSetUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MappingSetUnavailableException(
                $"Runtime compilation failed for {FormatKeyForMessage(key)}: {ex.Message}",
                [
                    .. BuildKeyDiagnostics(key),
                    $"Compilation error: {ex.Message}",
                    "Suggested action: Check server logs for the full stack trace.",
                ],
                ex
            );
        }
    }
}
