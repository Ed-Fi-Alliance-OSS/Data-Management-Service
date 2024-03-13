// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using Json.Schema;

namespace EdFi.DataManagementService.Api.Tests.Unit.Content
{
    public class DataModelProviderTests
    {
        public static ApiSchemaDocument SchemaDocument()
        {
            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();
        }
    }
}
