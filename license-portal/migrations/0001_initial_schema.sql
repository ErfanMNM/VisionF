-- Bang san pham: cac san pham duoc phep cap license.
CREATE TABLE IF NOT EXISTS products (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  code          TEXT NOT NULL UNIQUE,          -- trung voi "product" trong payload license
  name          TEXT NOT NULL,
  active        INTEGER NOT NULL DEFAULT 1,
  created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Bang license da cap. "data" va "sig" luu dang base64 nhu file license.lic.
CREATE TABLE IF NOT EXISTS licenses (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  license_key    TEXT NOT NULL UNIQUE,         -- khoa nghiep vu, dung de tra cuu/revoke
  customer       TEXT NOT NULL,
  product        TEXT NOT NULL,
  hwid           TEXT NOT NULL,                -- 32 ky tu hex (da chuan hoa)
  hwid_display   TEXT NOT NULL,                -- dang XXXX-XXXX-... de hien thi
  days_valid     INTEGER NOT NULL,
  issued_at      TEXT NOT NULL,                -- ISO 8601 UTC
  expires_at     TEXT NOT NULL,                -- ISO 8601 UTC
  key_id         TEXT NOT NULL,
  data_b64       TEXT NOT NULL,
  sig_b64        TEXT NOT NULL,
  revoked_at     TEXT,                         -- NULL = con hieu luc
  revoke_reason  TEXT,
  note           TEXT,
  created_by     TEXT NOT NULL,
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_licenses_customer ON licenses(customer);
CREATE INDEX IF NOT EXISTS idx_licenses_product  ON licenses(product);
CREATE INDEX IF NOT EXISTS idx_licenses_hwid     ON licenses(hwid);
CREATE INDEX IF NOT EXISTS idx_licenses_created  ON licenses(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_licenses_active   ON licenses(revoked_at, expires_at);

-- Nhat ky thao tac (audit).
CREATE TABLE IF NOT EXISTS audit_log (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  actor       TEXT NOT NULL,
  action      TEXT NOT NULL,
  detail      TEXT,
  created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_audit_created ON audit_log(created_at DESC);

-- Bang admin duoc dung de dang nhap.
CREATE TABLE IF NOT EXISTS admins (
  username       TEXT PRIMARY KEY,
  password_hash  TEXT NOT NULL,                -- PBKDF2-SHA256, dang "pbkdf2$iterations$salt$hash"
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_login_at  TEXT
);

-- Bang khoa ky: luu public key + kid de hien thi, private key nam trong Worker secret.
CREATE TABLE IF NOT EXISTS signing_keys (
  key_id       TEXT PRIMARY KEY,               -- 8 ky tu hex dau SHA256(SPKI)
  public_key   TEXT NOT NULL,                 -- base64 SPKI
  algorithm    TEXT NOT NULL DEFAULT 'RSASSA-PKCS1-v1_5/SHA-256',
  active       INTEGER NOT NULL DEFAULT 1,
  created_at   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Bang san pham mac dinh.
INSERT OR IGNORE INTO products (code, name) VALUES ('TanTienQR', 'Tan Tien QR');
