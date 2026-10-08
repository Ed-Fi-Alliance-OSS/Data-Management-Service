// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Secrets;

/// <summary>
/// The name of a secret, and the tenant it is being resolved for.
/// </summary>
/// <param name="Name">The name of the secret to resolve.</param>
/// <param name="Tenant">
/// The tenant the secret is being resolved for: <see langword="null"/> in a single-tenant
/// deployment, and the tenant name in a multi-tenant one.
/// </param>
public sealed record SecretReference(string Name, string? Tenant);
