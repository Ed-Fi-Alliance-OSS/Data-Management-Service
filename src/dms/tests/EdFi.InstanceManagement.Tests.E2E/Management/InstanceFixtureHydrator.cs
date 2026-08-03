// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Hydrates a per-scenario <see cref="InstanceManagementContext"/> from the immutable, run-scoped
/// <see cref="InstanceFixtureState"/>. Hydration only populates lookup knowledge (tenant names, route→data
/// store mappings, credentials) and the immutable fixture ID guards; it never marks anything scenario-owned
/// and never performs a CMS operation or requires a Configuration Service token. It is idempotent, so it is
/// safe to call from a BeforeScenario hook and again from a fixture-aware setup step within the same scenario.
/// </summary>
public static class InstanceFixtureHydrator
{
    public static void HydrateAll(InstanceManagementContext context, InstanceFixtureState fixture)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fixture);

        foreach (var tenant in fixture.Tenants)
        {
            HydrateTenant(context, tenant);
        }

        foreach (var route in fixture.Routes)
        {
            context.RouteQualifierToDataStoreId[route.RouteQualifier] = route.DataStoreId;
            context.DataStoreIdToTenant[route.DataStoreId] = route.TenantName;
            if (!context.DataStoreIds.Contains(route.DataStoreId))
            {
                context.DataStoreIds.Add(route.DataStoreId);
            }
        }

        // Guard sets: knowledge only. Cleanup consults these to ensure a fixture record is never deleted.
        context.FixtureApplicationIds = fixture.ApplicationIds;
        context.FixtureDataStoreIds = fixture.DataStoreIds;
        context.FixtureVendorIds = fixture.VendorIds;

        // Legacy single-application defaults (only used by scenarios that never authenticate per-tenant, e.g.
        // the discovery oauth flow). Default to the first fixture tenant without clobbering a scenario value.
        var primary = fixture.Tenants[0];
        context.ClientKey ??= primary.ClientKey;
        context.ClientSecret ??= primary.ClientSecret;
        context.ApplicationId ??= primary.ApplicationId;
        context.VendorId ??= primary.VendorId;
    }

    /// <summary>
    /// Hydrates knowledge for a single fixture tenant (used by fixture-aware setup steps that validate a
    /// specific requested tenant before hydrating).
    /// </summary>
    public static void HydrateTenant(InstanceManagementContext context, InstanceFixtureTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);

        if (!context.TenantNames.Contains(tenant.Name))
        {
            context.TenantNames.Add(tenant.Name);
        }

        context.VendorIdsByTenant[tenant.Name] = tenant.VendorId;
        context.ApplicationIdsByTenant[tenant.Name] = tenant.ApplicationId;
        context.CredentialsByTenant[tenant.Name] = (tenant.ClientKey, tenant.ClientSecret);
    }
}
