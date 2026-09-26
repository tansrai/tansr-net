// 仅合成测试资料；Node crypto 独立核验 C# 字节布局与规范 AAD，不供产品运行调用。
import { createDecipheriv, createHash } from 'node:crypto';
let input = '';
for await (const part of process.stdin) {
  input += part;
  if (input.length > 2 * 1048576) throw new Error('synthetic input limit');
}
const value = JSON.parse(input);
const key = Buffer.from(value.key, 'base64');
function canonical(data) {
  if (Array.isArray(data)) return '[' + data.map(canonical).join(',') + ']';
  if (data !== null && typeof data === 'object') {
    return '{' + Object.keys(data).sort().map(name => JSON.stringify(name) + ':' + canonical(data[name])).join(',') + '}';
  }
  return JSON.stringify(data);
}
function open(bytes, ref, offset, count) {
  const aad = Buffer.from(canonical({ format: 'sdk2-archive-encrypted-sqlite-v1',
    identity: value.identity, keyId: value.keyId, ref, offset, bytes: count }));
  const cipher = createDecipheriv('aes-256-gcm', key, bytes.subarray(0, 12));
  cipher.setAAD(aad);
  cipher.setAuthTag(bytes.subarray(12, 28));
  return Buffer.concat([cipher.update(bytes.subarray(28)), cipher.final()]);
}
const plaintexts = [];
try {
  const expectedCheck = Buffer.from('tansr.archive.key-check.v1');
  const check = open(Buffer.from(value.check, 'base64'), null, 0, expectedCheck.length);
  try { if (!check.equals(expectedCheck)) throw new Error('key check mismatch'); }
  finally { check.fill(0); }
  const encoded = Buffer.from(value.ciphertext, 'base64');
  let cursor = 0;
  for (let offset = 0; offset < value.reference.bytes; offset += 262144) {
    const count = Math.min(262144, value.reference.bytes - offset);
    plaintexts.push(open(encoded.subarray(cursor, cursor + count + 28), value.reference, offset, count));
    cursor += count + 28;
  }
  if (cursor !== encoded.length) throw new Error('ciphertext length mismatch');
  const hash = createHash('sha256');
  for (const part of plaintexts) hash.update(part);
  process.stdout.write(hash.digest('hex'));
} finally {
  key.fill(0);
  for (const part of plaintexts) part.fill(0);
}
