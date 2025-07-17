#!/usr/bin/env node

/**
 * Check if the current CLIENT_ID and CLIENT_SECRET are valid
 * by attempting to get an OAuth token
 */

import http from 'http';
import https from 'https';
import { URL } from 'url';

// Get configuration from environment
const OAUTH_TOKEN_URL = process.env.OAUTH_TOKEN_URL;
const CLIENT_ID = process.env.CLIENT_ID;
const CLIENT_SECRET = process.env.CLIENT_SECRET;

if (!OAUTH_TOKEN_URL || !CLIENT_ID || !CLIENT_SECRET) {
    console.error('Missing required environment variables: OAUTH_TOKEN_URL, CLIENT_ID, CLIENT_SECRET');
    process.exit(1);
}

// Parse the OAuth URL
const oauthUrl = new URL(OAUTH_TOKEN_URL);
const isHttps = oauthUrl.protocol === 'https:';
const httpModule = isHttps ? https : http;

// Prepare the request
const postData = 'grant_type=client_credentials';
const authHeader = 'Basic ' + Buffer.from(`${CLIENT_ID}:${CLIENT_SECRET}`).toString('base64');

const options = {
    hostname: oauthUrl.hostname,
    port: oauthUrl.port || (isHttps ? 443 : 80),
    path: oauthUrl.pathname + oauthUrl.search,
    method: 'POST',
    headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
        'Content-Length': Buffer.byteLength(postData),
        'Authorization': authHeader
    }
};

// Make the request
const req = httpModule.request(options, (res) => {
    let data = '';
    
    res.on('data', (chunk) => {
        data += chunk;
    });
    
    res.on('end', () => {
        if (res.statusCode === 200) {
            try {
                const response = JSON.parse(data);
                if (response.access_token) {
                    console.log('Credentials are valid');
                    process.exit(0);
                } else {
                    console.error('No access token in response');
                    process.exit(1);
                }
            } catch (e) {
                console.error('Failed to parse response:', e.message);
                process.exit(1);
            }
        } else {
            console.error(`Authentication failed with status ${res.statusCode}: ${data}`);
            process.exit(1);
        }
    });
});

req.on('error', (e) => {
    console.error(`Request failed: ${e.message}`);
    process.exit(1);
});

// Set a timeout
req.setTimeout(10000, () => {
    req.destroy();
    console.error('Request timeout');
    process.exit(1);
});

// Send the request
req.write(postData);
req.end();