// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The deployment facts a job execution needs outside a request (spec D-11): whether the service runs multi-tenant.
/// The frontend registers it as a singleton from <c>AppSettings:MultiTenancy</c>.
/// </summary>
public sealed record JobRuntimeEnvironment(bool MultiTenancy);
