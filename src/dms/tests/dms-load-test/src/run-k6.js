#!/usr/bin/env node

import { config } from 'dotenv';
import { spawn } from 'child_process';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

// Load environment variables from .env file
const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
config({ path: join(__dirname, '..', '.env') });

// Get command line arguments
const args = process.argv.slice(2);

// Spawn k6 with environment variables
const k6 = spawn('k6', args, {
    stdio: 'inherit',
    env: { ...process.env }
});

k6.on('close', (code) => {
    process.exit(code);
});