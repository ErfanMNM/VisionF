-- Dang ky public key hien hanh cho Cloudflare License Portal.
-- Khoa rieng (private key) nam trong Worker secret, KHONG luu o day.
INSERT OR REPLACE INTO signing_keys (key_id, public_key, algorithm, active)
VALUES (
  '4D088A7B',
  'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwG0vc9bil8mhzxx9ZLQ8myeEyLoHatE1n+MWucsyBw/hNzcWGsOxQx/7TOnDTGILxTi40ii7DfLFfZcsqq+yqiZahP3VMFE32vvgvGHDjuxFayCj69ySfI3GIpkQm1qMmYD/MerwtZH959qtQYZMGSKee/YW5j6o0m0N0LWFKkkA1T42ZSV0UFU7p68wJ2GmM91Cm8Qck5qfbvPsFA2XouKVJdrK0va7ettb3OCOVsI4aELd3pBCkDcqLug8BbnkGeLAUQsMXaVLovhYAAdWIeJ1TXCzB+SiheLdiLGXU/Bq3WP3fIWQdEV1eYaoITyQJKIQcLf813OQPL//FWf/2QIDAQAB',
  'RSASSA-PKCS1-v1_5/SHA-256',
  1
);
