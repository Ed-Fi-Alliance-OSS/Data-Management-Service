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

    const singleOperationGroupPlugin = window.EdFiSingleOperationGroup;

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
