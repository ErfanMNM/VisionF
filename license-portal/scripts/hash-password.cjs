// Sinh hash PBKDF2 cho mat khau admin (100.000 iterations — gioi han cua Workers).
// Dung: node scripts/hash-password.cjs <password>
const crypto = require('node:crypto');

const password = process.argv[2] || 'tantien512';
const iterations = 100000;
const salt = crypto.randomBytes(16);
const hash = crypto.pbkdf2Sync(password, salt, iterations, 32, 'sha256');
process.stdout.write(`pbkdf2$${iterations}$${salt.toString('base64')}$${hash.toString('base64')}`);
