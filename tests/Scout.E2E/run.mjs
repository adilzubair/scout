import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const project = path.dirname(fileURLToPath(import.meta.url));
const cli = path.join(path.dirname(fileURLToPath(import.meta.resolve('e2e'))), 'cli/bin.js');
const runId = randomUUID();
const output = `.e2e/runs/${runId}`;
const child = spawn(process.execPath, [cli, 'run', ...process.argv.slice(2)], {
  cwd: project, windowsHide: true, stdio: 'inherit',
  env: { ...process.env, E2E_TELEMETRY_DISABLED: '1', SCOUT_E2E_OUTPUT: output },
});
const stop = () => {
  if (child.pid) spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
};
process.once('SIGINT', stop);
try {
  const code = await new Promise((resolve, reject) => {
    child.once('error', reject); child.once('close', resolve);
  });
  const archive = path.resolve(project, '../../artifacts/diagnostics/e2e/runs', runId);
  await mkdir(archive, { recursive: true });
  for (const name of ['report.json', 'junit.xml', 'summary.md']) {
    try { await copyFile(path.join(project, output, name), path.join(archive, name)); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  console.log(`Retained run evidence: ${archive}`);
  process.exitCode = typeof code === 'number' ? code : 1;
} finally { process.removeListener('SIGINT', stop); }
