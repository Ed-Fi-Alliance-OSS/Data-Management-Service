// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Core.Startup;

internal sealed class EffectiveSchemaSetProvider : IEffectiveSchemaSetProvider
{
    private readonly object _initLock = new();
    private EffectiveSchemaSet? _effectiveSchemaSet;
    private volatile bool _isInitialized;

    public EffectiveSchemaSet EffectiveSchemaSet
    {
        get
        {
            EnsureInitialized();
            return _effectiveSchemaSet!;
        }
    }

    public bool IsInitialized => _isInitialized;

    public void Initialize(EffectiveSchemaSet effectiveSchemaSet)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

        lock (_initLock)
        {
            if (_isInitialized)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSetProvider has already been initialized."
                );
            }

            _effectiveSchemaSet = effectiveSchemaSet;
            _isInitialized = true;
        }
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSetProvider has not been initialized. Ensure the API schema initialization tasks have completed before backend mapping initialization."
            );
        }
    }
}
