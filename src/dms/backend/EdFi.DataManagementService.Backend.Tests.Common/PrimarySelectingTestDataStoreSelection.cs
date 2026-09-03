// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// A test-only <see cref="IDataStoreSelection"/> that assigns the primary target as soon as the data
/// store is selected, so a backend integration fixture that legitimately exercises only the primary
/// database can keep assigning one phase instead of two.
/// </summary>
/// <remarks>
/// It exists because those fixtures drive repositories directly, with no request pipeline to run the
/// target-selection step. Production never registers it: the request pipeline is exactly where the
/// choice between the primary and a derivative is made, and a production selection that quietly
/// assigned the primary on the caller's behalf would turn a missing selection step into a silent read
/// of the wrong database. Registered only by
/// <see cref="RelationalBackendIntegrationTestDataStoreExtensions.AddSelectedDataStoreIntegrationTestProvider"/>.
/// </remarks>
public sealed class PrimarySelectingTestDataStoreSelection : IDataStoreSelection
{
    private readonly DataStoreSelection _inner = new();

    /// <inheritdoc />
    public bool IsSet => _inner.IsSet;

    /// <inheritdoc />
    public bool IsEffectiveTargetSet => _inner.IsEffectiveTargetSet;

    /// <inheritdoc />
    public void SetSelectedDataStore(DataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);

        _inner.SetSelectedDataStore(dataStore);
        _inner.SetEffectiveTarget(EffectiveDataStoreTarget.Primary(dataStore.ConnectionString!));
    }

    /// <inheritdoc />
    public DataStore GetSelectedDataStore() => _inner.GetSelectedDataStore();

    /// <inheritdoc />
    public void SetEffectiveTarget(EffectiveDataStoreTarget target) => _inner.SetEffectiveTarget(target);

    /// <inheritdoc />
    public EffectiveDataStoreTarget GetEffectiveTarget() => _inner.GetEffectiveTarget();
}
