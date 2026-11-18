-- Yêu cầu cấp license do ứng dụng gửi lên; chỉ quản trị viên được cấp license.
CREATE TABLE IF NOT EXISTS license_requests (
  id             TEXT PRIMARY KEY,
  hwid           TEXT NOT NULL,
  hwid_display   TEXT NOT NULL,
  product        TEXT NOT NULL,
  customer       TEXT NOT NULL DEFAULT '',
  contact        TEXT NOT NULL DEFAULT '',
  note           TEXT NOT NULL DEFAULT '',
  status         TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'issued')),
  license_key    TEXT REFERENCES licenses(license_key) ON DELETE SET NULL,
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Gửi lại khi đang chờ không tạo nhiều yêu cầu cho cùng máy và sản phẩm.
CREATE UNIQUE INDEX IF NOT EXISTS idx_license_requests_pending_machine
  ON license_requests(product, hwid) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_license_requests_status_created
  ON license_requests(status, created_at DESC);
