// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Extracts tenants automatically from OpenAPI spec servers array
// and provides a tenant selector dropdown for multi-tenant environments.

window.EdFiTenant = function () {

    const dmsPort = window.DMS_HTTP_PORTS || "8080";

    let selectedTenant = null;
    let tenants = [];
    let swaggerUIInstance = null;

    // Extract tenants from OpenAPI spec servers section
    const extractTenantsFromSpec = (spec) => {
        try {
            if (!spec || !spec.servers || !Array.isArray(spec.servers)) {
                console.warn('No servers found in OpenAPI spec');
                return [];
            }

            const tenantList = [];

            spec.servers.forEach(server => {
                // Check if server has variables defined (OpenAPI 3.0 server variables)
                if (server.variables) {
                    // Look for Tenant variable (case-insensitive search)
                    const tenantVar = Object.keys(server.variables).find(key =>
                        key.toLowerCase().includes('tenant')
                    );

                    if (tenantVar && server.variables[tenantVar].enum) {
                        // Extract tenants from the enum array
                        server.variables[tenantVar].enum.forEach(tenant => {
                            if (tenant && !tenantList.includes(tenant)) {
                                tenantList.push(tenant);
                            }
                        });
                    }
                }
            });

            // Sort tenants alphabetically
            tenantList.sort((a, b) => a.localeCompare(b));

            return tenantList;
        } catch (error) {
            console.error('Error extracting tenants from spec:', error);
            return [];
        }
    };

    // Initialize the plugin
    const initialize = async (system) => {
        swaggerUIInstance = system;

        // Wait for Swagger UI to be fully loaded
        await waitForSwaggerUI();

        // Wait a bit more for spec to be fully processed
        await new Promise(resolve => setTimeout(resolve, 500));

        try {
            // Extract tenants from spec
            const specWrapper = window.ui.spec();

            // Get the actual spec - try both .toJS() and .json properties
            let spec = null;
            if (specWrapper && typeof specWrapper.toJS === 'function') {
                const jsObject = specWrapper.toJS();
                spec = jsObject.json || jsObject;
            } else if (specWrapper && specWrapper.json) {
                spec = specWrapper.json;
            } else {
                spec = specWrapper;
            }

            // If spec is still a string, parse it
            if (typeof spec === 'string') {
                try {
                    spec = JSON.parse(spec);
                } catch (e) {
                    console.error('Failed to parse spec string:', e);
                }
            }

            tenants = extractTenantsFromSpec(spec);

            if (tenants.length === 0) {
                console.log('No tenants found in OpenAPI spec servers - single-tenant mode');
                return;
            }

            // Use first tenant as default
            selectedTenant = tenants[0];

            // Create the selector UI
            createTenantSelector();

            // Wait for server selector to be available
            await waitForServerSelector();

            // Show the selector container
            const container = document.querySelector('.tenant-selector');
            if (container) {
                container.style.display = 'flex';
            }

            console.log('Tenant selector initialized with tenants:', tenants);
        } catch (error) {
            console.error('Error initializing tenant plugin:', error);
        }
    };

    // Wait for Swagger UI to be fully initialized
    const waitForSwaggerUI = () => {
        return new Promise((resolve) => {
            const checkSwaggerUI = () => {
                if (window.ui && window.ui.spec && window.ui.specActions && window.ui.specSelectors) {
                    resolve();
                } else {
                    setTimeout(checkSwaggerUI, 100);
                }
            };
            checkSwaggerUI();
        });
    };

    // Wait for server selector to be available in DOM
    const waitForServerSelector = () => {
        return new Promise((resolve) => {
            const checkSelector = () => {
                const serverSelector = document.querySelector('.servers select');
                if (serverSelector) {
                    resolve();
                } else {
                    setTimeout(checkSelector, 100);
                }
            };
            checkSelector();
        });
    };

    // Create the tenant selector UI
    const createTenantSelector = () => {
        // Check if selector already exists
        if (document.querySelector('.tenant-selector')) {
            return;
        }

        const container = document.createElement('div');
        container.className = 'tenant-selector';
        container.style.cssText = `
            padding: 20px;
            background-color: #e8f4e8;
            border-bottom: 1px solid #b8d4b8;
            display: none;
            align-items: center;
            gap: 15px;
        `;

        const label = document.createElement('label');
        label.textContent = 'Tenant:';
        label.style.cssText = `
            font-weight: bold;
            font-size: 14px;
        `;

        const select = document.createElement('select');
        select.className = 'tenant-select';
        select.style.cssText = `
            padding: 8px 12px;
            font-size: 14px;
            border: 1px solid #4a9d4a;
            border-radius: 4px;
            background-color: white;
            cursor: pointer;
        `;

        tenants.forEach(tenant => {
            const option = document.createElement('option');
            option.value = tenant;
            option.textContent = tenant;
            if (tenant === selectedTenant) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        select.addEventListener('change', (e) => {
            selectedTenant = e.target.value;
            updateComputedUrl();
            console.log('Tenant changed to:', selectedTenant);
        });

        // Add computed URL display
        const computedUrlContainer = document.createElement('div');
        computedUrlContainer.className = 'tenant-computed-url-display';
        computedUrlContainer.style.cssText = `
            margin-left: auto;
            font-size: 14px;
            color: #555;
        `;

        computedUrlContainer.innerHTML = `
            <strong>Tenant:</strong>
            <span class="tenant-computed-url-value" style="font-family: monospace; color: #2d862d;">
                ${selectedTenant}
            </span>
        `;

        container.appendChild(label);
        container.appendChild(select);
        container.appendChild(computedUrlContainer);

        // Insert at the top of the Swagger UI, before school year selector if it exists
        const schoolYearSelector = document.querySelector('.school-year-selector');
        if (schoolYearSelector) {
            schoolYearSelector.parentNode.insertBefore(container, schoolYearSelector);
        } else {
            const infoContainer = document.querySelector('.information-container');
            if (infoContainer) {
                infoContainer.parentNode.insertBefore(container, infoContainer);
            } else {
                const wrapper = document.querySelector('.swagger-ui');
                if (wrapper) {
                    wrapper.insertBefore(container, wrapper.firstChild);
                }
            }
        }
    };

    // Update computed URL display
    const updateComputedUrl = () => {
        const computedUrlElement = document.querySelector('.tenant-computed-url-value');
        if (computedUrlElement) {
            computedUrlElement.textContent = selectedTenant || '(none)';
        }
    };

    // Get the currently selected tenant (exposed for other plugins)
    const getSelectedTenant = () => selectedTenant;

    // Request interceptor to inject tenant into API calls
    const requestInterceptor = (req) => {
        if (!selectedTenant) {
            return req;
        }

        // Skip if URL already contains the tenant
        if (req.url.includes(`/${selectedTenant}/`)) {
            return req;
        }

        // Inject tenant into data requests
        // Pattern: /data/ -> /{tenant}/data/
        if (req.url.includes('/data/') && !req.url.includes('/metadata/')) {
            const tenantPattern = new RegExp(`/(${tenants.join('|')})/`);
            if (!tenantPattern.test(req.url)) {
                req.url = req.url.replace(/\/data\//, `/${selectedTenant}/data/`);
                console.log('Tenant added to data request:', req.url);
            }
        }

        return req;
    };

    return {
        statePlugins: {
            spec: {
                wrapActions: {
                    updateSpec: (oriAction, system) => (...args) => {
                        // Call original action first
                        const result = oriAction(...args);

                        // Get the spec after Swagger processes it
                        let [spec] = args;

                        // If spec is a string, parse it
                        if (typeof spec === 'string') {
                            try {
                                spec = JSON.parse(spec);
                            } catch (e) {
                                console.error('Failed to parse spec:', e);
                                return result;
                            }
                        }

                        // Replace docker hostname with localhost in servers
                        if (spec && spec.servers && Array.isArray(spec.servers)) {
                            spec.servers = spec.servers.map(server => ({
                                ...server,
                                url: server.url.replace('dms-config-service', 'localhost')
                            }));
                        }

                        return result;
                    }
                }
            }
        },
        fn: {
            requestInterceptor,
            getSelectedTenant
        },
        afterLoad: initialize
    };
};
