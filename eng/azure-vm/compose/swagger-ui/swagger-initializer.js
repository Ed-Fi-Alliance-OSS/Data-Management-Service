// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Swagger UI for the security-review gateway (/swagger/). Lists the DMS specs each stack advertises
// and both Configuration Service specs, all as same-origin paths through the gateway.

window.EdFiReviewSwagger = (function () {
    const configurationServiceScopes = {
        "edfi_admin_api/full_access": "Read and write the Management API",
        "edfi_admin_api/readonly_access": "Read the Management API"
    };

    // Advertised endpointUri values carry the public host; keep only the path and query so the
    // UI stays same-origin with whichever host it was opened on.
    function toPath(uri) {
        const parsed = new URL(uri, "https://placeholder.invalid");
        return parsed.pathname + parsed.search;
    }

    // When a stack's specification list is unavailable (e.g. the DMS is still starting), fall back to
    // its Resources and Descriptors specs so the stack stays listed; they load once it answers.
    function dmsDefinitions(label, basePath, specifications) {
        const advertised = (Array.isArray(specifications) ? specifications : [])
            .filter(spec => spec && spec.name && spec.name !== "Discovery" && spec.endpointUri);
        const listed = advertised.length > 0 ? advertised : [
            { name: "Resources", endpointUri: `${basePath}/metadata/specifications/resources-spec.json` },
            { name: "Descriptors", endpointUri: `${basePath}/metadata/specifications/descriptors-spec.json` }
        ];
        return listed.map(spec => ({ name: `${label}: ${spec.name}`, url: toPath(spec.endpointUri) }));
    }

    // The multi-tenant DMS specs describe tenant and school year as server variables, so one set of
    // definitions covers every tenant. The multi-tenant Configuration Service needs one per tenant,
    // because the tenant travels in a header the spec does not describe.
    function buildDefinitions(singleTenantSpecifications, multiTenantSpecifications, tenants, schoolYear) {
        const multiTenantBase = `/mt-dms/${encodeURIComponent(tenants[0] || "tenant1")}/${encodeURIComponent(schoolYear)}`;
        return [
            ...dmsDefinitions("Single-tenant DMS", "/st-dms", singleTenantSpecifications),
            ...dmsDefinitions("Multi-tenant DMS", multiTenantBase, multiTenantSpecifications),
            { name: "Single-tenant Configuration Service", url: "/st-config/openapi/v1.json" },
            ...tenants.map(tenant => ({
                name: `Multi-tenant Configuration Service (${tenant})`,
                url: `/mt-config/openapi/v1.json?tenant=${encodeURIComponent(tenant)}`
            }))
        ];
    }

    function configurationServicePrefix(url) {
        const match = toPath(url || "").match(/^\/(st|mt)-config\//);
        return match ? `/${match[1]}-config` : null;
    }

    // The Configuration Service spec declares no security scheme, so Swagger UI would offer no
    // Authorize button and every call would get 401. Add its real flow: client credentials (in the
    // request body) posted to <prefix>/connect/token with one of the two Management API scopes.
    function withConfigurationServiceSecurity(spec, specUrl, origin) {
        const prefix = configurationServicePrefix(specUrl);
        if (!prefix || !spec || typeof spec !== "object") {
            return spec;
        }
        spec.components = spec.components || {};
        spec.components.securitySchemes = spec.components.securitySchemes || {};
        if (!spec.components.securitySchemes.oauth2_client_credentials) {
            spec.components.securitySchemes.oauth2_client_credentials = {
                type: "oauth2",
                description: "Configuration Service client credentials: enter the client id and secret and tick a scope.",
                flows: { clientCredentials: { tokenUrl: `${origin}${prefix}/connect/token`, scopes: configurationServiceScopes } }
            };
        }
        if (!Array.isArray(spec.security) || spec.security.length === 0) {
            spec.security = [{ oauth2_client_credentials: [] }];
        }
        return spec;
    }

    // Every multi-tenant Configuration Service call needs a Tenant header. The spec download carries
    // the tenant in its own query string; API and token calls take it from the selected definition.
    function tenantForRequest(requestUrl, selectedSpecUrl) {
        if (configurationServicePrefix(requestUrl) !== "/mt-config") {
            return null;
        }
        const request = new URL(requestUrl, "https://placeholder.invalid");
        if (request.pathname.startsWith("/mt-config/openapi/") && request.searchParams.get("tenant")) {
            return request.searchParams.get("tenant");
        }
        if (configurationServicePrefix(selectedSpecUrl) !== "/mt-config") {
            return null;
        }
        return new URL(selectedSpecUrl, "https://placeholder.invalid").searchParams.get("tenant");
    }

    // Swagger UI's client-credentials flow sends the client id and secret only as an HTTP Basic
    // header, but the Configuration Service token endpoint reads them from the form body (400
    // without them). Move them into the form for that endpoint; the DMS token endpoint takes Basic.
    function withClientCredentialsInBody(request) {
        const headers = request.headers || {};
        const headerName = Object.keys(headers).find(name => name.toLowerCase() === "authorization");
        const authorization = headerName ? headers[headerName] : null;
        const path = new URL(request.url || "", "https://placeholder.invalid").pathname;
        if (!/^\/(st|mt)-config\/connect\/token$/.test(path) || typeof authorization !== "string" ||
            !authorization.startsWith("Basic ") || typeof request.body !== "string") {
            return request;
        }
        const credentials = atob(authorization.slice("Basic ".length));
        const separator = credentials.indexOf(":");
        if (separator < 0) {
            return request;
        }
        const form = new URLSearchParams(request.body);
        form.set("client_id", credentials.slice(0, separator));
        form.set("client_secret", credentials.slice(separator + 1));
        request.body = form.toString();
        delete headers[headerName];
        return request;
    }

    // The DMS specs give pageSize a default, and Swagger UI sends every default, but the DMS
    // rejects pageSize without a pageToken (400). Drop the default so "Try it out" works as
    // offered; pageSize stays available for cursor paging.
    function withoutPageSizeDefault(spec, specUrl) {
        if (!/^\/(st|mt)-dms\//.test(toPath(specUrl || "")) || !spec || typeof spec !== "object") {
            return spec;
        }
        const operationParameters = Object.values(spec.paths || {}).flatMap(item =>
            Object.values(item || {}).flatMap(operation => Array.isArray(operation) ? operation : (operation && operation.parameters) || []));
        for (const parameter of [...Object.values((spec.components || {}).parameters || {}), ...operationParameters]) {
            if (parameter && parameter.name === "pageSize" && parameter.in === "query" && parameter.schema) {
                delete parameter.schema.default;
            }
        }
        return spec;
    }

    return { buildDefinitions, withConfigurationServiceSecurity, tenantForRequest, withClientCredentialsInBody, withoutPageSizeDefault };
})();

window.onload = function () {
    const helpers = window.EdFiReviewSwagger;
    const tenants = [window.EDFI_REVIEW_TENANT_1, window.EDFI_REVIEW_TENANT_2].filter(Boolean);
    const schoolYear = window.EDFI_REVIEW_SCHOOL_YEAR || "2025";

    async function specificationList(path) {
        try {
            const response = await fetch(path, { cache: "no-store" });
            return response.ok ? await response.json() : [];
        }
        catch (error) {
            console.warn("Could not read", path, error);
            return [];
        }
    }

    // Applies withConfigurationServiceSecurity and withoutPageSizeDefault to every spec as it loads.
    function specPatchPlugin() {
        return {
            statePlugins: {
                spec: {
                    wrapActions: {
                        updateSpec: (originalAction, system) => (spec, ...rest) => {
                            const isString = typeof spec === "string";
                            let parsed;
                            try {
                                parsed = isString ? JSON.parse(spec) : spec;
                            }
                            catch (error) {
                                return originalAction(spec, ...rest);
                            }
                            const url = system.specSelectors.url();
                            const patched = helpers.withoutPageSizeDefault(
                                helpers.withConfigurationServiceSecurity(parsed, url, window.location.origin), url);
                            return originalAction(isString ? JSON.stringify(patched) : patched, ...rest);
                        }
                    }
                }
            }
        };
    }

    const plugins = [specPatchPlugin, window.EdFiSingleOperationGroup, window.EdFiCustomFields];
    if (window.EdFiCustomDomains) {
        plugins.push(window.EdFiCustomDomains);
    }

    const firstTenant = encodeURIComponent(tenants[0] || "tenant1");
    Promise.all([
        specificationList("/st-dms/metadata/specifications"),
        specificationList(`/mt-dms/${firstTenant}/${encodeURIComponent(schoolYear)}/metadata/specifications`)
    ]).then(([singleTenant, multiTenant]) => {
        window.ui = SwaggerUIBundle({
            urls: helpers.buildDefinitions(singleTenant, multiTenant, tenants, schoolYear),
            dom_id: "#swagger-ui",
            presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
            plugins: plugins,
            layout: "StandaloneLayout",
            docExpansion: "none",
            requestInterceptor: (request) => {
                helpers.withClientCredentialsInBody(request);
                const selected = window.ui && window.ui.specSelectors ? window.ui.specSelectors.url() : null;
                const tenant = helpers.tenantForRequest(request.url, selected);
                if (tenant) {
                    request.headers = request.headers || {};
                    request.headers["Tenant"] = tenant;
                }
                return request;
            }
        });
    });

    document.title = "Ed-Fi Security Review API Documentation";
};
