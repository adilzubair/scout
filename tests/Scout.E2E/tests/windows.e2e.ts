import { test, expect } from 'e2e';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const evidence = path.join(root, 'artifacts/diagnostics/e2e');

async function phase(name: string, args: string[]) {
  await mkdir(evidence, { recursive: true });
  const child = spawn('pwsh', ['-NoProfile', '-File', path.join(root, 'scripts/test-e2e-phase.ps1'), ...args], {
    cwd: root, windowsHide: true, env: { ...process.env, E2E_TELEMETRY_DISABLED: '1' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const chunks: Buffer[] = [];
  child.stdout.on('data', data => chunks.push(Buffer.from(data)));
  child.stderr.on('data', data => chunks.push(Buffer.from(data)));
  const timer = setTimeout(() => {
    // Stop only the process tree this test created, including native children.
    if (child.pid) spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
  }, 570_000);
  try {
    const code = await new Promise<number | null>((resolve, reject) => {
      child.on('error', reject); child.on('close', resolve);
    });
    const output = Buffer.concat(chunks).toString('utf8');
    await writeFile(path.join(evidence, `${name}.log`), output);
    await writeFile(path.join(evidence, `${Date.now()}-${name}.log`), output);
    expect(code, output.slice(-12000)).toBe(0);
  } finally { clearTimeout(timer); }
}

test('Release native regression suite', () => phase('native', ['-Phase', 'native']));
test('Runtime source and archive packaging contracts', () => phase('packaging', ['-Phase', 'packaging']));
test('Cleanup preservation contracts', () => phase('cleanup', ['-Phase', 'cleanup']));
test('Expanded launcher, workspace and settings control interactions', () => phase('controls', ['-Phase', 'controls']));
test('Repeated PDF navigation, cancellation and malformed recovery', () => phase('pdf', ['-Phase', 'pdf']));
test('Hidden published PDF workers and failure recovery', () => phase('pdf-worker', ['-Phase', 'pdf-worker']));
test('Real WPF settings, restart, picker, launcher and previews', () => phase('application', ['-Phase', 'application']));
test('Real-model seven-family indexing, watching, pause and revocation', () => phase('scheduler', ['-Phase', 'scheduler']));
test('Python reader, model-boundary and setup contracts', () => phase('python', ['-Phase', 'python']));
test('Laya adapter and authenticated local HTTP', () => phase('laya', ['-Phase', 'laya']));
test('Ordinary search with indexing, PDF and Laya concurrency', async () => {
  test.skip(process.env.SCOUT_E2E_CONCURRENCY !== '1', 'Set SCOUT_E2E_CONCURRENCY=1 for the development-machine workload with real model indexing and Laya.');
  await phase('concurrency', ['-Phase', 'concurrency']);
});
test('CUDA full-model worker and joint audiovisual inputs', async () => {
  test.skip(process.env.SCOUT_E2E_CUDA !== '1', 'Set SCOUT_E2E_CUDA=1 on a PC with the CUDA test environment and supported GPU.');
  await phase('cuda', ['-Phase', 'cuda']);
});
test('Laya package install/retry and newly installed runtime', async () => {
  test.skip(process.env.SCOUT_E2E_INSTALL !== '1', 'Set SCOUT_E2E_INSTALL=1 to allow dependency/model downloads into an isolated artifacts package.');
  await phase('laya-install', ['-Phase', 'laya-install']);
});
