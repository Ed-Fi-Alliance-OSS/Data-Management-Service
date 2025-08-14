// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.onload = function () {
    const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set

    window.ui = SwaggerUIBundle({
        urls: [
            { url: `http://localhost:${dmsPort}/metadata/specifications/resources-spec.json`, name: "Resources" },
            { url: `http://localhost:${dmsPort}/metadata/specifications/descriptors-spec.json`, name: "Descriptors" }
        ],
        dom_id: '#swagger-ui',
        presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
        plugins: [window.EdFiCustomFields, window.EdFiCustomDomains],
        layout: "StandaloneLayout",
        docExpansion: "none",
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

