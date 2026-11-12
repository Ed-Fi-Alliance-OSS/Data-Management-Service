// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>What the engine reads about the target server before an operation (D-9).</summary>
/// <param name="MajorVersion">The server's major version, compared with the manifest's engine major.</param>
/// <param name="DefaultDataPath">SQL Server: <c>InstanceDefaultDataPath</c>; null on PostgreSQL.</param>
/// <param name="DefaultLogPath">SQL Server: <c>InstanceDefaultLogPath</c>; null on PostgreSQL.</param>
public sealed record DmsServerFacts(int MajorVersion, string? DefaultDataPath, string? DefaultLogPath);
