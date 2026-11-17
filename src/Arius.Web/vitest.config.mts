import { defineConfig } from 'vitest/config';

// Standalone Vitest for pure TS logic (job-format et al.). The existing specs use global describe/it,
// so globals:true. Angular component tests would need @analogjs/vitest-angular — out of scope here.
export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    include: ['src/app/**/*.spec.ts'],
    // e2e specs are Playwright, not Vitest:
    exclude: ['e2e/**', 'node_modules/**'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcovonly'],
      reportsDirectory: './coverage',
      include: ['src/app/**/*.ts'],
      exclude: ['src/app/**/*.spec.ts', 'src/app/**/*.component.ts'],  // components need the Angular harness; cover pure logic
    },
  },
});
