// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.onload = function () {
    const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set
    const defaultUrls = [
        { url: `http://localhost:${dmsPort}/metadata/specifications/resources-spec.json`, name: "Resources" },
        { url: `http://localhost:${dmsPort}/metadata/specifications/descriptors-spec.json`, name: "Descriptors" }
    ];

    function singleOperationGroupPlugin() {
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
    }

    // Configuration for Ed-Fi Custom Domains plugin from environment variable
    const enableCustomDomains = (window.DMS_SWAGGER_UI_ENABLE_CUSTOM_DOMAINS || "true") === "true";

    // Configure plugins based on settings
    const plugins = [singleOperationGroupPlugin, window.EdFiCustomFields];
    if (enableCustomDomains && window.EdFiCustomDomains) {
        plugins.push(window.EdFiCustomDomains);
        console.log('Ed-Fi Custom Domains plugin enabled');
    }

    if (window.EdFiRouteContext) {
        plugins.push(window.EdFiRouteContext);
        console.log('Ed-Fi Route Context plugin enabled');
    }

    // Begin dynamic discovery of available API definitions
    async function buildUrlsList() {
        const specEndpoint = `http://localhost:${dmsPort}/metadata/specifications`;

        try {
            console.log('Fetching metadata specifications from', specEndpoint);
            const resp = await fetch(specEndpoint, { cache: 'no-store' });
            if (!resp.ok) {
                console.warn('Failed to fetch /metadata/specifications; status', resp.status);
                return defaultUrls;
            }

            const json = await resp.json();
            if (!json || !Array.isArray(json.specifications) && !Array.isArray(json)) {
                // Support both { specifications: [...] } or [...] payload shapes
                console.warn('Unexpected /metadata/specifications shape; falling back to defaults');
                return defaultUrls;
            }

            const entries = Array.isArray(json.specifications) ? json.specifications : json;

            const urls = entries
                .filter(spec =>
                    spec
                    && typeof spec.name === 'string'
                    && spec.name.length > 0
                    && spec.name !== 'Discovery'
                    && typeof spec.endpointUri === 'string'
                    && spec.endpointUri.length > 0
                )
                .map(spec => ({ name: spec.name, url: spec.endpointUri }));

            if (urls.length === 0) {
                console.log('No advertised specs found; using default Resources/Descriptors');
                return defaultUrls;
            }

            console.log('Discovered API definitions for Swagger UI:', urls.map(url => url.name));
            return urls;
        }
        catch (ex) {
            console.warn('Error discovering /metadata/specifications:', ex);
            return defaultUrls;
        }
    }

    function initializeSwaggerUi(urls) {
        window.ui = SwaggerUIBundle({
            urls: urls,
            dom_id: '#swagger-ui',
            presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
            plugins: plugins,
            layout: "StandaloneLayout",
            docExpansion: "none",
            requestInterceptor: (req) => {
                const routeState = window.__edfiRouteContextState || null;
                const selections = routeState && typeof routeState.getSelections === 'function'
                    ? routeState.getSelections()
                    : {};
                const routePrefix = routeState && typeof routeState.getRoutePrefix === 'function'
                    ? routeState.getRoutePrefix()
                    : '';

                const currentTenant = selections && selections.tenant ? selections.tenant : null;

                console.log('Request interceptor - Route prefix:', routePrefix || '(none)', 'Tenant:', currentTenant || '(none)', 'Original URL:', req.url);

                if (req.url && routeState && typeof routeState.rewriteRequestUrl === 'function') {
                    const originalUrl = req.url;
                    req.url = routeState.rewriteRequestUrl(req.url);
                    if (req.url !== originalUrl) {
                        console.log('Request URL rewritten:', req.url);
                    }
                }
                return req;
            },
            onComplete: function () {
                console.log('Swagger UI loaded successfully');
            },
            onFailure: function (data) {
                console.log('Swagger UI failed to load:', data);
            }
        });
    }

    // Build the UI after resolving URLs
    buildUrlsList().then(initializeSwaggerUi).catch(err => {
        console.error('Unexpected error building Swagger UI urls:', err);
        initializeSwaggerUi(defaultUrls);
    });
    // End dynamic discovery

    // Update the title of the page
    document.title = "Ed-Fi API Documentation";

    // Update the label
    const updateLabel = () => {
        const labels = document.querySelectorAll('.download-url-wrapper .select-label');
        labels.forEach(label => {
            const span = label.querySelector('span');
            if (span && span.textContent.includes("Select a definition")) {
                span.textContent = "API Section";
            }
        });
    };

    const observer = new MutationObserver(updateLabel);
    observer.observe(document.body, { childList: true, subtree: true });

    let attempts = 0;
    const intervalId = setInterval(() => {
        updateLabel();
        if (++attempts > 10) {
            clearInterval(intervalId);
        }
    }, 300);
};
