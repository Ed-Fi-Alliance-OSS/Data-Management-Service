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
                    marginTop: "4px",
                },
            },
            text
        );
    };

    const safeGet = window.EdfiCommonHelper.safeGet;

    // Helper function to extract and display Ed-Fi custom fields from schema
    const extractEdFiFields = (schema, system, isParameter = false) => {
        if (!schema) return [];

        const fields = [];
        const noteCreator = isParameter ? createParameterNote : createNote;

        // Check for x-Ed-Fi-isIdentity
        const isIdentity = safeGet(schema, "x-Ed-Fi-isIdentity");
        if (isIdentity !== undefined) {
            const element = noteCreator(`x-Ed-Fi-isIdentity: ${String(isIdentity)}`, system);
            if (element) fields.push(element);
        }

        // Check for x-Ed-Fi-isDeprecated
        const isDeprecated = safeGet(schema, "x-Ed-Fi-isDeprecated");
        if (isDeprecated !== undefined) {
            const element = noteCreator(`x-Ed-Fi-isDeprecated: ${String(isDeprecated)}`, system);
            if (element) fields.push(element);
        }

        // Check for x-Ed-Fi-deprecatedReasons
        const deprecatedReasons = safeGet(schema, "x-Ed-Fi-deprecatedReasons");
        if (deprecatedReasons !== undefined) {
            const reasonsText = Array.isArray(deprecatedReasons)
                ? `[${deprecatedReasons.map((r) => `"${r}"`).join(", ")}]`
                : `"${deprecatedReasons}"`;
            const element = noteCreator(`x-Ed-Fi-deprecatedReasons: ${reasonsText}`, system);
            if (element) fields.push(element);
        }

        // Check for x-nullable
        const nullable = safeGet(schema, "x-nullable");
        if (nullable !== undefined) {
            const element = noteCreator(`x-nullable: ${String(nullable)}`, system);
            if (element) fields.push(element);
        }

        return fields;
    };

    // Helper function to create a simplified note element for parameters
    const createParameterNote = (text, system) => {
        const React = system.React || window.React;
        if (!React) return null;

        return React.createElement("div", {
            style: {
                fontFamily: "monospace",
                fontSize: "90%",
                color: "rgb(102, 102, 102)",
            },
        }, text);
    };

    // Helper function to create a table row for Ed-Fi fields
    const createEdFiRow = (edFiFields, system) => {
        const React = system.React || window.React;
        if (!React || edFiFields.length === 0) return null;

        return React.createElement("tr", 
            { style: { borderTop: "none", paddingTop: "0" } },
            React.createElement("td", { className: "parameters-col_name" }, ""),
            React.createElement("td", { 
                className: "parameters-col_description", 
                style: { paddingTop: "0" } 
            }, ...edFiFields)
        );
    };

    // Helper function to format extension values based on type
    const formatExtensionValue = (value) => {
        if (typeof value === 'boolean') return String(value);
        if (Array.isArray(value)) return JSON.stringify(value);
        if (typeof value === 'object' && value !== null) return JSON.stringify(value, null, 2);
        return String(value);
    };

    // Helper function to extract x-Ed-Fi-isUpdatable from operation level
    const extractOperationExtensions = (operation) => {
        const extensions = [];
        if (operation && operation['x-Ed-Fi-isUpdatable'] !== undefined) {
            extensions.push({
                field: 'x-Ed-Fi-isUpdatable',
                value: formatExtensionValue(operation['x-Ed-Fi-isUpdatable'])
            });
        }
        return extensions;
    };

    // Helper function to create Extensions section table
    const createExtensionsTable = (extensions, system) => {
        const React = system.React || window.React;
        if (!React || extensions.length === 0) return null;

        return React.createElement("div", 
            { 
                className: "responses-wrapper",
                style: { marginTop: "20px" }
            },
            React.createElement("div", 
                { className: "opblock-section-header" },
                React.createElement("h4", 
                    { className: "opblock-section-header-title" },
                    "Extensions"
                )
            ),
            React.createElement("div", 
                { className: "responses-inner" },
                React.createElement("table", 
                    { className: "responses-table" },
                    React.createElement("thead", null,
                        React.createElement("tr", 
                            { className: "response" },
                            React.createElement("td", 
                                { className: "response-col_status" }, 
                                "Field"
                            ),
                            React.createElement("td", 
                                { className: "response-col_description" }, 
                                "Value"
                            )
                        )
                    ),
                    React.createElement("tbody", null,
                        ...extensions.map((ext, index) => 
                            React.createElement("tr", 
                                { key: index, className: "response" },
                                React.createElement("td", 
                                    { 
                                        className: "response-col_status",
                                        style: { 
                                            fontFamily: "monospace",
                                            minWidth: "200px",
                                            whiteSpace: "nowrap"
                                        }
                                    }, 
                                    ext.field
                                ),
                                React.createElement("td", 
                                    { 
                                        className: "response-col_description",
                                        style: { fontFamily: "monospace" }
                                    }, 
                                    ext.value
                                )
                            )
                        )
                    )
                )
            )
        );
    };

    // Shared helper function to extract and render extensions for PUT operations
    const renderExtensionsForPutOperation = (props, system, children) => {
        const React = system.React;
        const method = props.method;
        const path = props.path;
        const specSelectors = props.specSelectors;

        // Only process PUT operations
        if (method !== 'put' || !specSelectors || !path) {
            return children;
        }

        // Extract operation directly from spec for PUT operations
        let operation = null;
        try {
            const spec = specSelectors.specJS();
            if (spec?.paths?.[path]?.[method]) {
                operation = spec.paths[path][method];
            }
        } catch (e) {
            // Silently handle errors - extensions are optional
            return children;
        }

        if (!operation) {
            return children;
        }

        const extensions = extractOperationExtensions(operation);
        
        if (extensions.length === 0) {
            return children;
        }

        const extensionsTable = createExtensionsTable(extensions, system);
        
        if (!extensionsTable) {
            return children;
        }

        // Append Extensions section after the original content
        return React.createElement(React.Fragment, null, 
            children,
            extensionsTable
        );
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

            // Wrapper for ParameterRow - inject Ed-Fi custom fields as additional table row
            parameterRow: (Original, system) => (props) => {
                const React = system.React || window.React;

                if (!React) {
                    return React.createElement(Original, props);
                }

                const children = React.createElement(Original, props);

                // Extract Ed-Fi fields from parameter with simplified styling
                const param = props.param;
                const edFiFields = extractEdFiFields(param, system, true);

                if (edFiFields.length > 0) {
                    const edFiRow = createEdFiRow(edFiFields, system);
                    return React.createElement(React.Fragment, null, children, edFiRow);
                }

                return children;
            },

            // Inject Extensions table for PUT operations in responses section
            responses: (Original, system) => (props) => {
                const React = system.React;
                const children = React.createElement(Original, props);
                return renderExtensionsForPutOperation(props, system, children);
            },
        },
    };
};
