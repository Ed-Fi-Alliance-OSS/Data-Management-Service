// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Threading;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Process-local cache of compiled mapping sets keyed by <see cref="MappingSetKey" />.
/// </summary>
public sealed class MappingSetCache(Func<MappingSetKey, Task<MappingSet>> compileAsync)
{
    private readonly Func<MappingSetKey, Task<MappingSet>> _compileAsync =
        compileAsync ?? throw new ArgumentNullException(nameof(compileAsync));

    private readonly ConcurrentDictionary<MappingSetKey, Lazy<Task<MappingSet>>> _cache = new();

    /// <summary>
    /// Gets or creates a compiled mapping set for <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// Cancellation only cancels waiting for completion. Compilation continues once started.
    /// </remarks>
    public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken = default)
    {
        var compileTask = _cache
            .GetOrAdd(
                key,
                static (mappingSetKey, state) =>
                    new Lazy<Task<MappingSet>>(
                        () => state(mappingSetKey),
                        LazyThreadSafetyMode.ExecutionAndPublication
                    ),
                _compileAsync
            )
            .Value;

        return compileTask.WaitAsync(cancellationToken);
    }
}
