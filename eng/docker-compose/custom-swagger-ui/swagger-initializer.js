// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.onload = function () {
    const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set

    // Configuration for Ed-Fi Custom Domains plugin from environment variable
    const enableCustomDomains = (window.DMS_SWAGGER_UI_ENABLE_CUSTOM_DOMAINS || "true") === "true";

    // Configure plugins based on settings
    const plugins = [window.EdFiCustomFields];
    if (enableCustomDomains && window.EdFiCustomDomains) {
        plugins.push(window.EdFiCustomDomains);
        console.log('Ed-Fi Custom Domains plugin enabled');
    }

    // Add School Year plugin if available
    if (window.EdFiSchoolYear) {
        plugins.push(window.EdFiSchoolYear);
        console.log('Ed-Fi School Year plugin enabled');
    }

    window.ui = SwaggerUIBundle({
        urls: [
            { url: `http://localhost:${dmsPort}/metadata/specifications/resources-spec.json`, name: "Resources" },
            { url: `http://localhost:${dmsPort}/metadata/specifications/descriptors-spec.json`, name: "Descriptors" }
        ],
        dom_id: '#swagger-ui',
        presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
        plugins: plugins,
        layout: "StandaloneLayout",
        docExpansion: "none",
        requestInterceptor: (req) => {
            // Get the current selected year from the DOM selector
            const schoolYearSelect = document.querySelector('.school-year-select');
            const currentYear = schoolYearSelect ? schoolYearSelect.value : null;

            console.log('Request interceptor - Current year:', currentYear, 'Original URL:', req.url);

            if (currentYear && req.url) {
                // Replace dms-config-service with localhost for CORS
                if (req.url.includes('dms-config-service')) {
                    req.url = req.url.replace(
                        /http:\/\/dms-config-service:(\d+)/g,
                        'http://localhost:$1'
                    );
                    console.log('Hostname replaced in request URL:', req.url);
                }

                // Add school year to data requests
                if (req.url.includes('/data/') && !req.url.includes('/metadata/')) {
                    if (!req.url.match(/\/\d{4}\/data\//)) {
                        req.url = req.url.replace(/\/data\//, `/${currentYear}/data/`);
                        console.log('School year added to data request:', req.url);
                    }
                }

                // Add school year to OAuth token requests
                if (req.url.includes('/connect/token')) {
                    if (!req.url.match(/\/connect\/token\/\d{4}/)) {
                        req.url = req.url.replace(/\/connect\/token$/, `/connect/token/${currentYear}`);
                        console.log('Token request final URL:', req.url);
                    }
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

    // Update the title of the page
    document.title = "Ed-Fi DMS API Documentation";

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

