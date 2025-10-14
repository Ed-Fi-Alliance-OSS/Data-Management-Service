// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.SchemaGenerator.Abstractions;

namespace EdFi.DataManagementService.SchemaGenerator.Cli.Services
{
    public class ApiSchemaLoader
    {
        public ApiSchema Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"ApiSchema file not found: {path}");
            }

            var json = File.ReadAllText(path);
            var schema = JsonSerializer.Deserialize<ApiSchema>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (schema == null)
            {
                throw new InvalidDataException("Failed to deserialize ApiSchema.");
            }

            return schema;
        }
    }
}
