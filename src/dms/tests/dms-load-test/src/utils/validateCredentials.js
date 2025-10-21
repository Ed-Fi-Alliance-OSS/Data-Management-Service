#!/usr/bin/env node

/**
 * Script to validate credentials using the checkCredentials library
 */

import { checkCredentials } from './checkCredentials.js';

// Get configuration from environment
const OAUTH_TOKEN_URL = process.env.OAUTH_TOKEN_URL;
const CLIENT_ID = process.env.CLIENT_ID;
const CLIENT_SECRET = process.env.CLIENT_SECRET;

if (!OAUTH_TOKEN_URL || !CLIENT_ID || !CLIENT_SECRET) {
    console.error('Missing required environment variables: OAUTH_TOKEN_URL, CLIENT_ID, CLIENT_SECRET');
    process.exit(1);
}

// Check credentials
try {
    const isValid = await checkCredentials(OAUTH_TOKEN_URL, CLIENT_ID, CLIENT_SECRET);
    if (isValid) {
        console.log('Credentials are valid');
        process.exit(0);
    } else {
        process.exit(1);
    }
} catch (error) {
    console.error('Error checking credentials:', error.message);
    process.exit(1);
}