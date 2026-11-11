export interface Env {
  DB: D1Database;
  ASSETS: Fetcher;
  // Worker secrets (wrangler secret put):
  LICENSE_PRIVATE_KEY_PKCS8: string; // base64 PKCS#8 RSA private key
  LICENSE_KEY_ID: string;           // 8 hex, khop voi app
  ADMIN_USERNAME: string;
  ADMIN_PASSWORD_HASH: string;      // "pbkdf2$iterations$salt$hash"
  SESSION_SECRET: string;           // ky cookie session
  APP_NAME: string;
  DEFAULT_PRODUCT: string;
}
