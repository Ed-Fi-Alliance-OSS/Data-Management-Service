// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// VERSION: Extracts school years automatically from OpenAPI spec servers array

window.EdFiSchoolYear = function () {
    let selectedYear = null;
    let schoolYears = [];
    let swaggerUIInstance = null;

    // Extract school years from OpenAPI spec servers section
    const extractSchoolYearsFromSpec = (spec) => {
        try {
            if (!spec || !spec.servers || !Array.isArray(spec.servers)) {
                console.warn('No servers found in OpenAPI spec');
                return [];
            }

            // Extract years from server URLs (e.g., "http://localhost:8080/2025/data")
            const years = [];
            const yearRegex = /\/(20\d{2})\//; // Match year pattern like /2024/

            spec.servers.forEach(server => {
                if (server.url) {
                    const match = server.url.match(yearRegex);
                    if (match && match[1]) {
                        const year = parseInt(match[1]);
                        if (!years.includes(year)) {
                            years.push(year);
                        }
                    }
                }
            });

            // Sort years in descending order (newest first)
            years.sort((a, b) => b - a);

            console.log('School years extracted from OpenAPI spec:', years);
            return years;
        } catch (error) {
            console.error('Error extracting school years from spec:', error);
            return [];
        }
    };

    // Initialize the plugin
    const initialize = async (system) => {
        swaggerUIInstance = system;

        // Wait for Swagger UI to be fully loaded
        await waitForSwaggerUI();

        // Extract school years from spec
        const spec = window.ui.spec().toJS();
        schoolYears = extractSchoolYearsFromSpec(spec);

        if (schoolYears.length === 0) {
            console.warn('No school years found in OpenAPI spec servers');
            return;
        }

        // Set default year (most recent)
        selectedYear = schoolYears[0];

        // Create the selector UI
        createSchoolYearSelector();

        // Wait for server selector to be available
        await waitForServerSelector();

        // Populate server selector
        populateServerSelector();
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

    // Create the school year selector UI
    const createSchoolYearSelector = () => {
        // Check if selector already exists
        if (document.querySelector('.school-year-selector')) {
            return;
        }

        const container = document.createElement('div');
        container.className = 'school-year-selector';
        container.style.cssText = `
            padding: 20px;
            background-color: #fafafa;
            border-bottom: 1px solid #d4d4d4;
            display: flex;
            align-items: center;
            gap: 15px;
        `;

        const label = document.createElement('label');
        label.textContent = 'School Year:';
        label.style.cssText = `
            font-weight: bold;
            font-size: 14px;
        `;

        const select = document.createElement('select');
        select.className = 'school-year-select';
        select.style.cssText = `
            padding: 8px 12px;
            font-size: 14px;
            border: 1px solid #ccc;
            border-radius: 4px;
            background-color: white;
            cursor: pointer;
        `;

        schoolYears.forEach(year => {
            const option = document.createElement('option');
            option.value = year;
            option.textContent = year;
            if (year === selectedYear) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        select.addEventListener('change', (e) => {
            selectedYear = e.target.value;
            console.log('School year changed to:', selectedYear);
            updateServerSelector();
            updateComputedUrl();
        });

        // Add computed URL display
        const computedUrlContainer = document.createElement('div');
        computedUrlContainer.className = 'computed-url-display';
        computedUrlContainer.style.cssText = `
            margin-left: auto;
            font-size: 14px;
            color: #555;
        `;
        computedUrlContainer.innerHTML = `
            <strong>Computed URL:</strong>
            <span class="computed-url-value" style="font-family: monospace; color: #0066cc;">
                http://localhost:8080/${selectedYear}/data
            </span>
        `;

        container.appendChild(label);
        container.appendChild(select);
        container.appendChild(computedUrlContainer);

        // Insert at the top of the Swagger UI
        const infoContainer = document.querySelector('.information-container');
        if (infoContainer) {
            infoContainer.parentNode.insertBefore(container, infoContainer);
        } else {
            const wrapper = document.querySelector('.swagger-ui');
            if (wrapper) {
                wrapper.insertBefore(container, wrapper.firstChild);
            }
        }
    };

    // Populate the server selector with year-based URLs
    const populateServerSelector = () => {
        const serverSelector = document.querySelector('.servers select');
        if (!serverSelector) {
            console.warn('Server selector not found');
            return;
        }

        // Clear existing options
        serverSelector.innerHTML = '';

        // Add options for each school year
        schoolYears.forEach(year => {
            const option = document.createElement('option');
            option.value = `http://localhost:8080/${year}/data`;
            option.textContent = `http://localhost:8080/{schoolYear}/data (${year})`;
            if (year === selectedYear) {
                option.selected = true;
            }
            serverSelector.appendChild(option);
        });

        console.log('Server selector populated with school years from spec');
    };

    // Update server selector when year changes
    const updateServerSelector = () => {
        const serverSelector = document.querySelector('.servers select');
        if (serverSelector) {
            const newUrl = `http://localhost:8080/${selectedYear}/data`;
            serverSelector.value = newUrl;

            // Trigger change event to update Swagger UI
            const event = new Event('change', { bubbles: true });
            serverSelector.dispatchEvent(event);
        }
    };

    // Update computed URL display
    const updateComputedUrl = () => {
        const computedUrlElement = document.querySelector('.computed-url-value');
        if (computedUrlElement) {
            computedUrlElement.textContent = `http://localhost:8080/${selectedYear}/data`;
        }
    };

    // Request interceptor to inject school year into API calls
    const requestInterceptor = (req) => {
        if (!selectedYear) {
            return req;
        }

        // Inject school year into the URL if not already present
        const yearPattern = /\/\d{4}\//;
        if (!yearPattern.test(req.url)) {
            // Insert year after the base URL
            req.url = req.url.replace(
                /(https?:\/\/[^\/]+)(\/.*)?/,
                `$1/${selectedYear}$2`
            );
        }

        // Also update OAuth token URL if present
        if (req.url.includes('/connect/token')) {
            if (!req.url.endsWith(`/${selectedYear}`)) {
                req.url = `${req.url}/${selectedYear}`;
            }
        }

        console.log('Request URL with school year:', req.url);
        return req;
    };

    return {
        statePlugins: {
            spec: {
                wrapActions: {
                    updateSpec: (oriAction, system) => (...args) => {
                        // Get the spec before Swagger processes it
                        let [spec] = args;

                        // If spec is a string, parse it
                        if (typeof spec === 'string') {
                            try {
                                spec = JSON.parse(spec);
                            } catch (e) {
                                console.error('Failed to parse spec:', e);
                                return oriAction(...args);
                            }
                        }

                        // Replace docker hostname with localhost in servers
                        if (spec && spec.servers && Array.isArray(spec.servers)) {
                            spec.servers = spec.servers.map(server => ({
                                ...server,
                                url: server.url.replace('dms-config-service', 'localhost')
                            }));
                        }

                        // Call original action with modified spec
                        return oriAction(spec);
                    }
                }
            }
        },
        fn: {
            requestInterceptor
        },
        afterLoad: initialize
    };
};
