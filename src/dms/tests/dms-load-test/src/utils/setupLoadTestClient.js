#!/usr/bin/env node
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.

/**
 * Setup script to create a properly authorized client for load testing
 * This mimics the E2E test approach of creating clients with appropriate claim sets
 */

import https from 'https';
import http from 'http';
import { promises as fs } from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { dirname } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

// Configuration
const CONFIG_SERVICE_PORT = process.env.CONFIG_SERVICE_PORT || '8081';
const CONFIG_SERVICE_HOST = process.env.CONFIG_SERVICE_HOST || 'localhost';
const DMS_PORT = process.env.DMS_PORT || '8080';
const DMS_HOST = process.env.DMS_HOST || 'localhost';

// Helper function to make HTTP requests
function httpRequest(options, data = null) {
    return new Promise((resolve, reject) => {
        const protocol = options.port === 443 || options.protocol === 'https:' ? https : http;
        const req = protocol.request(options, (res) => {
            let body = '';
            res.on('data', (chunk) => body += chunk);
            res.on('end', () => {
                resolve({
                    statusCode: res.statusCode,
                    headers: res.headers,
                    body: body
                });
            });
        });

        req.on('error', reject);

        if (data) {
            req.write(data);
        }

        req.end();
    });
}

// Step 1: Register sys-admin client
async function registerSysAdminClient() {
    console.log('📝 Registering sys-admin client...');

    const clientId = `load-test-admin-${Date.now()}`;
    const clientSecret = 'LoadTest123!';

    const formData = new URLSearchParams({
        ClientId: clientId,
        ClientSecret: clientSecret,
        DisplayName: 'Load Test Admin'
    });

    const response = await httpRequest({
        hostname: CONFIG_SERVICE_HOST,
        port: CONFIG_SERVICE_PORT,
        path: '/config/connect/register',
        method: 'POST',
        headers: {
            'Content-Type': 'application/x-www-form-urlencoded',
            'Content-Length': formData.toString().length
        }
    }, formData.toString());

    if (response.statusCode !== 200) {
        throw new Error(`Failed to register sys-admin: ${response.statusCode} - ${response.body}`);
    }

    console.log('✅ Sys-admin client registered');
    return { clientId, clientSecret };
}

// Step 2: Get sys-admin token
async function getSysAdminToken(clientId, clientSecret) {
    console.log('🔑 Getting sys-admin token...');

    const basicAuth = Buffer.from(`${clientId}:${clientSecret}`).toString('base64');
    const formData = new URLSearchParams({
        grant_type: 'client_credentials',
        scope: 'edfi_admin_api/full_access'
    });

    const response = await httpRequest({
        hostname: 'localhost',
        port: '8045',
        path: '/realms/edfi/protocol/openid-connect/token',
        method: 'POST',
        headers: {
            'Authorization': `Basic ${basicAuth}`,
            'Content-Type': 'application/x-www-form-urlencoded',
            'Content-Length': formData.toString().length
        }
    }, formData.toString());

    if (response.statusCode !== 200) {
        throw new Error(`Failed to get sys-admin token: ${response.statusCode} - ${response.body}`);
    }

    const tokenData = JSON.parse(response.body);
    console.log('✅ Sys-admin token obtained');
    return tokenData.access_token;
}

// Step 3: Create vendor
async function createVendor(token) {
    console.log('🏢 Creating vendor...');

    const vendorData = JSON.stringify({
        company: 'Load Test Company',
        contactName: 'Load Test Contact',
        contactEmailAddress: 'loadtest@example.com',
        namespacePrefixes: 'uri://ed-fi.org'
    });

    const response = await httpRequest({
        hostname: CONFIG_SERVICE_HOST,
        port: CONFIG_SERVICE_PORT,
        path: '/config/v2/vendors',
        method: 'POST',
        headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json',
            'Content-Length': vendorData.length
        }
    }, vendorData);

    if (response.statusCode !== 201 && response.statusCode !== 200) {
        throw new Error(`Failed to create vendor: ${response.statusCode} - ${response.body}`);
    }

    // Get vendor ID from response or location header
    let vendorId;

    if (response.statusCode === 200) {
        // Vendor already exists, parse ID from response
        const responseJson = JSON.parse(response.body);
        vendorId = responseJson.id;
        console.log('✅ Using existing vendor with ID:', vendorId);
    } else {
        // New vendor created, get from location header
        const vendorLocation = response.headers.location;
        const getResponse = await httpRequest({
            hostname: CONFIG_SERVICE_HOST,
            port: CONFIG_SERVICE_PORT,
            path: vendorLocation,
            method: 'GET',
            headers: {
                'Authorization': `Bearer ${token}`
            }
        });

        const vendor = JSON.parse(getResponse.body);
        vendorId = vendor.id;
        console.log('✅ Vendor created with ID:', vendorId);
    }

    return vendorId;
}

// Step 4: Create application with claim set
async function createApplication(token, vendorId) {
    console.log('📱 Creating application with E2E-NoFurtherAuthRequiredClaimSet...');

    const appData = JSON.stringify({
        vendorId: vendorId,
        applicationName: 'DMS Load Test',
        claimSetName: 'E2E-NoFurtherAuthRequiredClaimSet',
        educationOrganizationIds: []
    });

    const response = await httpRequest({
        hostname: CONFIG_SERVICE_HOST,
        port: CONFIG_SERVICE_PORT,
        path: '/config/v2/applications',
        method: 'POST',
        headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json',
            'Content-Length': appData.length
        }
    }, appData);

    if (response.statusCode !== 201) {
        throw new Error(`Failed to create application: ${response.statusCode} - ${response.body}`);
    }

    const app = JSON.parse(response.body);
    console.log('✅ Application created');
    return {
        clientId: app.key,
        clientSecret: app.secret
    };
}

// Step 5: Save credentials to .env.load-test
async function saveCredentials(credentials) {
    console.log('💾 Saving credentials to .env.load-test...');

    const envContent = `# DMS Load Test Client Credentials
# Generated: ${new Date().toISOString()}

# API Configuration
CLIENT_ID=${credentials.clientId}
CLIENT_SECRET=${credentials.clientSecret}
`;

    const envPath = path.join(__dirname, '../../.env.load-test');
    await fs.writeFile(envPath, envContent, 'utf8');
    console.log('✅ Credentials saved to .env.load-test');
}

// Main execution
async function main() {
    try {
        console.log('🚀 Setting up DMS Load Test Client');
        console.log('==================================\n');

        // Create sys-admin client
        const sysAdmin = await registerSysAdminClient();

        // Get sys-admin token
        const sysAdminToken = await getSysAdminToken(sysAdmin.clientId, sysAdmin.clientSecret);

        // Create vendor
        const vendorId = await createVendor(sysAdminToken);

        // Create application with proper claim set
        const appCredentials = await createApplication(sysAdminToken, vendorId);

        // Save credentials
        await saveCredentials(appCredentials);

        console.log('\n✅ Load test client setup complete!');
        console.log('   Client ID:', appCredentials.clientId);
        console.log('   Use .env.load-test for your tests');

    } catch (error) {
        console.error('\n❌ Setup failed:', error.message);
        process.exit(1);
    }
}

// Run if called directly
if (import.meta.url === `file://${process.argv[1]}`) {
    main();
}

export { main };
