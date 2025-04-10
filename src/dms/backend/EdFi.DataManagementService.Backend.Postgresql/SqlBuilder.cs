// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql.Operation;

namespace EdFi.DataManagementService.Backend.Postgresql
{
    public static class SqlBuilder
    {
        public static string SqlFor(LockOption lockOption)
        {
            return lockOption switch
            {
                LockOption.None => "",
                LockOption.BlockUpdateDelete => "FOR NO KEY UPDATE",
                _ => throw new InvalidOperationException("Unknown lock option type"),
            };
        }
    }
}
