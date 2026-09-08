// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdfiCommonHelper = {
    // Helper function to safely get values from schema
    safeGet: (schema, key) => {
        if (!schema) {
            return undefined;
        }
        if (typeof schema.get === 'function') {
            return schema.get(key);
        }
        // Fallback for plain objects
        return schema[key];
    }
};
