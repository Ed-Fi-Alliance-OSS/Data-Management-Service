// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdFiCustomFields = function () {

    // Helper function to create a styled note element using system React
    const createNote = (text, system) => {
        // Get React from the system parameter that Swagger UI provides
        const React = system.React || window.React;
        if (!React) {
            return null;
        }

        return React.createElement(
            "div",
            {
                style: {
                    marginLeft: "20px",
                    fontFamily: "monospace",
                    fontSize: "90%",
                    color: "#666",
                    borderLeft: "2px solid #ddd",
                    paddingLeft: "6px",
                    marginTop: "4px"
                },
            },
            text
        );
    };

    const safeGet = window.EdfiCommonHelper ? window.EdfiCommonHelper.safeGet : (schema, key) => {
        if (!schema) return undefined;
        if (typeof schema.get === 'function') return schema.get(key);
        return schema[key];
    };

    // Helper function to extract and display Ed-Fi custom fields from schema
    const extractEdFiFields = (schema, system) => {
        if (!schema) return [];

        const fields = [];

        // Check for x-Ed-Fi-isIdentity
        const isIdentity = safeGet(schema, "x-Ed-Fi-isIdentity");
        if (isIdentity !== undefined) {
            const element = createNote(`x-Ed-Fi-isIdentity: ${String(isIdentity)}`, system);
            if (element) fields.push(element);
        }

        // Check for x-Ed-Fi-isDeprecated
        const isDeprecated = safeGet(schema, "x-Ed-Fi-isDeprecated");
        if (isDeprecated !== undefined) {
            const element = createNote(`x-Ed-Fi-isDeprecated: ${String(isDeprecated)}`, system);
            if (element) fields.push(element);
        }

        // Check for x-Ed-Fi-deprecatedReasons
        const deprecatedReasons = safeGet(schema, "x-Ed-Fi-deprecatedReasons");
        if (deprecatedReasons !== undefined) {
            const reasonsText = Array.isArray(deprecatedReasons)
                ? `[${deprecatedReasons.map(r => `"${r}"`).join(", ")}]`
                : `"${deprecatedReasons}"`;
            const element = createNote(`x-Ed-Fi-deprecatedReasons: ${reasonsText}`, system);
            if (element) fields.push(element);
        }

        // Check for x-nullable
        const nullable = safeGet(schema, "x-nullable");
        if (nullable !== undefined) {
            const element = createNote(`x-nullable: ${String(nullable)}`, system);
            if (element) fields.push(element);
        }

        return fields;
    };

    return {
        wrapComponents: {

            // Wrapper for Model - inject Ed-Fi custom fields into schema
            Model: (Original, system) => (props) => {
                const React = system.React || window.React;

                if (!React) {
                    return Original(props);
                }

                const children = Original(props);

                // Extract Ed-Fi fields from schema
                const schema = props.schema;
                const edFiFields = extractEdFiFields(schema, system);

                if (edFiFields.length > 0) {
                    return React.createElement(React.Fragment, null, children, ...edFiFields);
                }

                return children;
            },

        }
    };

};
