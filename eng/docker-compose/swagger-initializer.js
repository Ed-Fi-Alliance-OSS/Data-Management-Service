// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// swagger-initializer.js
const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set

window.onload = () => {
    window.ui = SwaggerUIBundle({
        urls: [
            { url: `http://localhost:${dmsPort}/metadata/specifications/resources-spec.json`, name: "Resources" },
            { url: `http://localhost:${dmsPort}/metadata/specifications/descriptors-spec.json`, name: "Descriptors" }
        ],
        dom_id: '#swagger-ui',
        presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
        layout: "StandaloneLayout"
    });

    // Update the label text in the topbar
    const updateLabel = () => {
        const labels = document.querySelectorAll('.select-label');
        labels.forEach(label => {
            if (label.textContent.includes("Select a definition")) {
                label.textContent = "API Section";
            }
        });
    };

    //  MutationObserver to detect when Swagger UI update the DOM from topbar
    const topbar = document.querySelector('.topbar');
    if (topbar) {
        const observer = new MutationObserver(() => {
            updateLabel();
        });
        observer.observe(topbar, { childList: true, subtree: true });
    } else {
        // If the topbar is not present, retry after a short delay
        const retry = () => {
            const topbarRetry = document.querySelector('.topbar');
            if (topbarRetry) {
                const observer = new MutationObserver(() => {
                    updateLabel();
                });
                observer.observe(topbarRetry, { childList: true, subtree: true });
                updateLabel();
            } else {
                setTimeout(retry, 100);
            }
        };
        retry();
    }

    // Inyect estyle CSS for label y select
    const injectStyle = () => {
        const style = document.createElement('style');
        style.type = 'text/css';
        style.innerHTML = `
      /* Ajusta selector para el label o texto que quieres colorear */
      .topbar select + span,
      .topbar label span {
        color: #4A90E2 !important;
        font-weight: bold !important;
      }

      .topbar select {
        border: 1px solid #4A90E2 !important;
        background-color: #ffffff !important;
        color: #4A90E2 !important;
      }
    `;
        document.head.appendChild(style);
    };
    injectStyle();

};
