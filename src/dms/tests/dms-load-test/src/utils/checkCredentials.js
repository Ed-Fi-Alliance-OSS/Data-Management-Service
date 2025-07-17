/**
 * Check if the CLIENT_ID and CLIENT_SECRET are valid
 * by attempting to get an OAuth token
 */

import http from 'http';
import https from 'https';
import { URL } from 'url';

/**
 * Validates OAuth credentials by attempting to get a token
 * @param {string} oauthTokenUrl - The OAuth token endpoint URL
 * @param {string} clientId - The client ID
 * @param {string} clientSecret - The client secret
 * @returns {Promise<boolean>} - True if credentials are valid, false otherwise
 */
export async function checkCredentials(oauthTokenUrl, clientId, clientSecret) {
    if (!oauthTokenUrl || !clientId || !clientSecret) {
        throw new Error('Missing required parameters: oauthTokenUrl, clientId, clientSecret');
    }

    return new Promise((resolve, reject) => {
        // Parse the OAuth URL
        const oauthUrl = new URL(oauthTokenUrl);
        const isHttps = oauthUrl.protocol === 'https:';
        const httpModule = isHttps ? https : http;

        // Prepare the request
        const postData = 'grant_type=client_credentials';
        const authHeader = 'Basic ' + Buffer.from(`${clientId}:${clientSecret}`).toString('base64');

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
                            resolve(true);
                        } else {
                            console.error('No access token in response');
                            resolve(false);
                        }
                    } catch (e) {
                        console.error('Failed to parse response:', e.message);
                        resolve(false);
                    }
                } else {
                    console.error(`Authentication failed with status ${res.statusCode}: ${data}`);
                    resolve(false);
                }
            });
        });

        req.on('error', (e) => {
            console.error(`Request failed: ${e.message}`);
            resolve(false);
        });

        // Set a timeout
        req.setTimeout(10000, () => {
            req.destroy();
            console.error('Request timeout');
            resolve(false);
        });

        // Send the request
        req.write(postData);
        req.end();
    });
}