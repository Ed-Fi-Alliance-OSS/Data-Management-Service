// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Builds an <see cref="IDmsInstanceProvider"/> stub that exposes a single in-memory
/// DMS instance pointing at a test-leased database connection string. The instance has
/// no route-qualifier context, so it is returned for any tenant the caller asks about.
/// </summary>
internal static class FakeDmsInstanceProvider
{
    public static IDmsInstanceProvider WithSingleInstance(long id, string connectionString) =>
        new SingleInstanceProvider(id, connectionString);

    private sealed class SingleInstanceProvider : IDmsInstanceProvider
    {
        private const string DefaultTenantKey = "";
        private readonly DmsInstance _instance;
        private readonly IReadOnlyList<DmsInstance> _instances;

        public SingleInstanceProvider(long id, string connectionString)
        {
            _instance = new DmsInstance(
                Id: id,
                InstanceType: "default",
                InstanceName: "integration-test",
                ConnectionString: connectionString,
                RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
            );
            _instances = [_instance];
        }

        public Task<IList<DmsInstance>> LoadDmsInstances(string? tenant = null) =>
            Task.FromResult<IList<DmsInstance>>([_instance]);

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null) => Task.CompletedTask;

        public IReadOnlyList<DmsInstance> GetAll(string? tenant = null) => _instances;

        public DmsInstance? GetById(long id, string? tenant = null) => id == _instance.Id ? _instance : null;

        public bool IsLoaded(string? tenant = null) => true;

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([DefaultTenantKey]);

        public bool TenantExists(string tenant) => true;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [DefaultTenantKey];
    }
}
