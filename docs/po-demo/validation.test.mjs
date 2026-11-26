import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { hasValidGtin } = require('./validation.js');

test('accepts GTIN-8, GTIN-12, GTIN-13, and GTIN-14 with valid checksums', () => {
  for (const value of ['96385074', '036000291452', '4006381333931', '10012345000017']) {
    assert.equal(hasValidGtin(value), true, value);
  }
});

test('rejects invalid length, non-digits, and bad check digits', () => {
  for (const value of ['96385075', '4006381333932', '123456789', '400638133393A', '']) {
    assert.equal(hasValidGtin(value), false, value);
  }
});
