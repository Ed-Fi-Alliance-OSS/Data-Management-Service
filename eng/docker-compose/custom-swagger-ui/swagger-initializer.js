// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Pure helpers behind the local Swagger UI, exposed on `window` for
// eng/docker-compose/tests/LocalSwaggerUi.Tests.ps1, which loads this file under Node.
window.EdFiLocalSwagger = (function () {
    const routeContextVariable = "DMS_SWAGGER_UI_ROUTE_CONTEXT";

    // With multi-tenancy on, the DMS metadata routes answer only under the tenant and every configured
    // route qualifier value, so those segments precede /metadata in every URL the UI fetches.
    // "DistrictA/2024" becomes "/DistrictA/2024", each segment URL-encoded; empty stays empty, which
    // keeps the unqualified routes a single-tenant stack serves.
    function routeContextPrefix(routeContext) {
        const segments = String(routeContext || "")
            .split("/")
            .map(segment => segment.trim())
            .filter(segment => segment.length > 0)
            .map(segment => encodeURIComponent(segment));
        return segments.length > 0 ? `/${segments.join("/")}` : "";
    }

    function metadataBaseUrl(port, routeContext) {
        return `http://localhost:${port}${routeContextPrefix(routeContext)}/metadata`;
    }

    // The definitions a listing advertises, in either payload shape ({ specifications: [...] } or
    // [...]) and without Discovery. When the listing is missing, malformed or empty: the Resources and
    // Descriptors documents under the same metadata base, so the UI still loads once DMS answers.
    function definitionsFrom(listing, metadataBase) {
        const entries = Array.isArray(listing)
            ? listing
            : (listing && Array.isArray(listing.specifications) ? listing.specifications : []);
        const advertised = entries
            .filter(spec =>
                spec
                && typeof spec.name === "string"
                && spec.name.length > 0
                && spec.name !== "Discovery"
                && typeof spec.endpointUri === "string"
                && spec.endpointUri.length > 0
            )
            .map(spec => ({ name: spec.name, url: spec.endpointUri }));
        return advertised.length > 0 ? advertised : [
            { name: "Resources", url: `${metadataBase}/specifications/resources-spec.json` },
            { name: "Descriptors", url: `${metadataBase}/specifications/descriptors-spec.json` }
        ];
    }

    // What the page shows when DMS answers the listing with 404: the DMS problem detail, when there is
    // one, then the setting that supplies the route context.
    function routeContextNotice(problemDetail) {
        const detail = typeof problemDetail === "string" && problemDetail.trim().length > 0
            ? `${problemDetail.trim()} `
            : "";
        return `${detail}Set ${routeContextVariable} in the environment file to the tenant and route qualifier `
            + "values to load, for example DistrictA/2024, then start the stack again so the swagger-ui "
            + "container picks it up.";
    }

    return { routeContextVariable, routeContextPrefix, metadataBaseUrl, definitionsFrom, routeContextNotice };
})();

window.onload = function () {
    const helpers = window.EdFiLocalSwagger;
    const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set
    const routeContext = window.DMS_SWAGGER_UI_ROUTE_CONTEXT || "";
    const metadataBase = helpers.metadataBaseUrl(dmsPort, routeContext);
    const defaultUrls = helpers.definitionsFrom(null, metadataBase);

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

    // Shown above the UI when DMS answers the listing with 404, which under multi-tenancy means the
    // route context is missing or names a tenant or qualifier value DMS has not loaded.
    function showNotice(message) {
        const notice = document.createElement('div');
        notice.className = 'edfi-route-context-notice';
        notice.setAttribute('role', 'alert');
        notice.style.cssText = 'margin: 16px; padding: 12px 16px; border: 1px solid #f5c2c7; border-radius: 4px; background-color: #f8d7da; color: #58151c; font-family: sans-serif; font-size: 14px;';
        notice.textContent = message;
        const target = document.getElementById('swagger-ui');
        if (target && target.parentNode) {
            target.parentNode.insertBefore(notice, target);
        } else {
            document.body.insertBefore(notice, document.body.firstChild);
        }
    }

    // The problem detail of a failed listing answer, when DMS sent problem details.
    async function problemDetailOf(response) {
        const contentType = response.headers.get('content-type') || '';
        if (!contentType.includes('application/problem+json')) {
            return null;
        }
        try {
            const problem = await response.json();
            return problem && typeof problem.detail === 'string' ? problem.detail : null;
        }
        catch (ex) {
            return null;
        }
    }

    // Begin dynamic discovery of available API definitions
    async function buildUrlsList() {
        const specEndpoint = `${metadataBase}/specifications`;

        try {
            console.log('Fetching metadata specifications from', specEndpoint);
            const resp = await fetch(specEndpoint, { cache: 'no-store' });
            if (!resp.ok) {
                console.warn('Failed to fetch', specEndpoint, '; status', resp.status);
                if (resp.status === 404) {
                    const notice = helpers.routeContextNotice(await problemDetailOf(resp));
                    console.warn(notice);
                    showNotice(`Could not load the API definitions from ${specEndpoint} (HTTP 404). ${notice}`);
                }
                return defaultUrls;
            }

            const urls = helpers.definitionsFrom(await resp.json(), metadataBase);
            console.log('Discovered API definitions for Swagger UI:', urls.map(url => url.name));
            return urls;
        }
        catch (ex) {
            console.warn('Error discovering', specEndpoint, ex);
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
