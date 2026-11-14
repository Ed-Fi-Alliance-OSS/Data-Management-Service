// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The server-side identity of one session, captured at open (D-7b): PostgreSQL <c>pg_backend_pid()</c>
/// with <c>backend_start</c>; SQL Server <c>@@SPID</c> with <c>login_time</c>. The start time guards every
/// later lookup against id reuse (D-22).
/// </summary>
public readonly record struct DmsServerSessionId(long Id, DateTimeOffset StartedAt);
