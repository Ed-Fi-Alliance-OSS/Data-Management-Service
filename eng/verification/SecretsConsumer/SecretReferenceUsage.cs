// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Secrets;

namespace SecretsConsumer;

/// <summary>
/// Pins the published shape of <see cref="SecretReference"/> as a caller sees it: positional
/// construction by parameter name, both properties readable, a nullable tenant, and value
/// equality. A rename, a reordering, or a change away from a record fails this compile.
/// </summary>
public static class SecretReferenceUsage
{
    public static SecretReference SingleTenant(string name) => new(Name: name, Tenant: null);

    public static SecretReference ForTenant(string name, string tenant) => new(Name: name, Tenant: tenant);

    public static bool IsSameSecret(SecretReference left, SecretReference right) =>
        left == right && left.Name == right.Name && left.Tenant == right.Tenant;
}
