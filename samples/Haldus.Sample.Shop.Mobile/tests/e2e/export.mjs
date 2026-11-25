// Uygulamanın web dışa aktarımını ekran testleri için üretir (dist-e2e). Backend'in adresi derlenirken koda gömülür;
// burada bir yer tutucu verilir ve testlerin sunucusu betiği sunarken onu kendi adresiyle değiştirir.
import { spawnSync } from 'node:child_process';

const result = spawnSync(
  'pnpm',
  ['exec', 'expo', 'export', '--platform', 'web', '--output-dir', 'dist-e2e'],
  {
    stdio: 'inherit',
    shell: true,
    env: { ...process.env, EXPO_PUBLIC_API_URL: 'http://haldus-e2e.invalid' },
  }
);

process.exit(result.status ?? 1);
