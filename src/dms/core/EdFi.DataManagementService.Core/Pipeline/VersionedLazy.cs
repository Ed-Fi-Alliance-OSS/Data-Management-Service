// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// A lazy-loading cache that automatically invalidates when a version changes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the VersionedLazy class.
/// </remarks>
/// <param name="valueFactory">The delegate to invoke to produce the lazily initialized value when it is needed.</param>
/// <param name="versionProvider">The delegate to invoke to get the current version.</param>
internal class VersionedLazy<T>(Func<T> valueFactory, Func<Guid> versionProvider)
{
    private readonly Func<T> _valueFactory =
        valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));
    private readonly Func<Guid> _versionProvider =
        versionProvider ?? throw new ArgumentNullException(nameof(versionProvider));
    private readonly object _lock = new();
    private T? _cachedValue;
    private Guid _cachedVersion;
    private bool _hasValue;

    /// <summary>
    /// Gets the lazily initialized value of the current VersionedLazy instance.
    /// The value is recomputed if the version has changed.
    /// </summary>
    public T Value
    {
        get
        {
            lock (_lock)
            {
                Guid currentVersion = _versionProvider();

                // If we don't have a value yet or the version has changed, recompute
                if (!_hasValue || _cachedVersion != currentVersion)
                {
                    _cachedValue = _valueFactory();
                    _cachedVersion = currentVersion;
                    _hasValue = true;
                }

                return _cachedValue!;
            }
        }
    }

    /// <summary>
    /// Returns the cached value along with the version that produced it.
    /// </summary>
    public (T Value, Guid Version) GetValueAndVersion()
    {
        lock (_lock)
        {
            Guid currentVersion = _versionProvider();

            if (!_hasValue || _cachedVersion != currentVersion)
            {
                _cachedValue = _valueFactory();
                _cachedVersion = currentVersion;
                _hasValue = true;
            }

            return (_cachedValue!, _cachedVersion);
        }
    }
}
