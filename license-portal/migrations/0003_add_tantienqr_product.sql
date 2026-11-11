-- San pham TanTienQR la san pham chinh cua portal.
-- (Migration 0001 da tao 'TanTienQR' o DB moi; migration nay dam bao cho
--  DB cu da duoc tao truoc do khi san pham mac dinh la 'DemoQR'.)

INSERT OR IGNORE INTO products (code, name) VALUES ('TanTienQR', 'Tan Tien QR');

-- Dong san pham DemoQR (chi dung de cap license cho app DemoQR).
-- Giu lai vi co the van co license dang chay tren may khach.
