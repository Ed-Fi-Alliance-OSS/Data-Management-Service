// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;

namespace EdFi.DataManagementService.Backend
{
    /// <summary>
    /// Database specific options from configuration
    /// </summary>
    public class DatabaseOptions
    {

        /// <summary>
        /// IsolationLevel to use for all database transactions.
        /// </summary>
        public IsolationLevel IsolationLevel { get; set; }
    }
}
