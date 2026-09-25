// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The lease owner a hosted service claims or leases as.</summary>
public static class JobLeaseOwner
{
    /// <summary>The longest owner the <c>LeaseOwner</c> columns hold.</summary>
    public const int MaxLength = 200;

    /// <summary><c>&lt;machine&gt;:&lt;process id&gt;:&lt;guid&gt;</c>, unique per call and at most 200 characters.</summary>
    public static string Create()
    {
        string owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return owner.Length > MaxLength ? owner[^MaxLength..] : owner;
    }
}
