-- Entitlement module được ký cùng payload license và hiển thị trong portal.
ALTER TABLE licenses ADD COLUMN features_json TEXT NOT NULL DEFAULT '[]';
