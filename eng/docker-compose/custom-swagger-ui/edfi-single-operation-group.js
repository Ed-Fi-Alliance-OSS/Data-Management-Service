// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Swagger UI plugin shared by the local stack and the security-review gateway: a spec with a single
// untagged operation (e.g. Change-Queries' /availableChangeVersions) is grouped under its path
// instead of Swagger UI's "default" group.
window.EdFiSingleOperationGroup = function singleOperationGroupPlugin() {
    const operationMethods = new Set(['delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'trace']);

    const addGroupForSingleUntaggedOperation = (specification) => {
        if (!specification || typeof specification !== 'object' || !specification.paths) {
            return specification;
        }

        const operations = [];
        Object.entries(specification.paths).forEach(([path, pathItem]) => {
            if (!pathItem || typeof pathItem !== 'object') {
                return;
            }

            Object.entries(pathItem).forEach(([method, operation]) => {
                if (operationMethods.has(method.toLowerCase()) && operation && typeof operation === 'object') {
                    operations.push({ method, operation, path });
                }
            });
        });

        if (operations.length !== 1 || (Array.isArray(operations[0].operation.tags) && operations[0].operation.tags.length > 0)) {
            return specification;
        }

        const { method, operation, path } = operations[0];
        const groupName = path.replace(/^\//, '');
        if (!groupName) {
            return specification;
        }

        const description = operation.description || operation.summary;
        operation.tags = [groupName];

        const tags = Array.isArray(specification.tags) ? specification.tags : [];
        if (!tags.some(tag => tag && typeof tag === 'object' && tag.name === groupName)) {
            specification.tags = [
                ...tags,
                {
                    name: groupName,
                    ...(description ? { description } : {}),
                },
            ];
        }

        specification.paths[path][method] = operation;
        return specification;
    };

    return {
        statePlugins: {
            spec: {
                wrapActions: {
                    updateSpec: (oriAction) => (...args) => {
                        let specification = args[0];
                        const originalWasString = typeof specification === 'string';

                        if (originalWasString) {
                            try {
                                specification = JSON.parse(specification);
                            } catch (error) {
                                return oriAction(...args);
                            }
                        }

                        specification = addGroupForSingleUntaggedOperation(specification);
                        args[0] = originalWasString ? JSON.stringify(specification) : specification;
                        return oriAction(...args);
                    },
                },
            },
        },
    };
};
