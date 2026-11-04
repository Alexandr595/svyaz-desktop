// Export committed Git objects for the connected GitHub Git Data API.
// Runtime data and downloaded tool binaries must never be included.
const fs = require('node:fs');
const cp = require('node:child_process');
const path = require('node:path');
const commit = process.argv[2] || 'HEAD';
const output = process.argv[3] || 'artifacts/github-tree.json';
const git = (...args) => cp.execFileSync('git', args, { maxBuffer: 8 * 1024 * 1024 });
const entries = git('ls-tree', '-rz', '--full-tree', commit).toString('utf8').split('\0').filter(Boolean);
const files = entries.map(entry => {
  const match = /^(\d+) blob ([a-f0-9]+)\t(.+)$/.exec(entry);
  if (!match) throw new Error('Unexpected Git tree entry');
  const [, mode, sha, name] = match;
  if (/^(artifacts|vendor|\.git|\.codex|\.aws)\//.test(name) || /(^|\/)data\//.test(name) || /\.(exe|dll|sys)$/.test(name))
    throw new Error('Refusing runtime/private/downloaded file: ' + name);
  const bytes = git('cat-file', 'blob', sha);
  const binary = /\.(png|ico|bmp|bin)$/.test(name);
  return { path: name, mode, type: 'blob', localSha: sha, ...(binary ? { base64: bytes.toString('base64') } : { content: bytes.toString('utf8') }) };
});
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify({ commit: git('rev-parse', commit).toString().trim(), tree: git('rev-parse', commit + '^{tree}').toString().trim(), files }));
console.log(`Exported ${files.length} committed source files (${files.filter(f => f.base64).length} binary assets).`);
