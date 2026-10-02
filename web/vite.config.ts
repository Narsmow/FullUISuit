import { defineConfig } from 'vite';

// Single IIFE bundle + CSS, embedded into the plugin as Web/fullui.js and Web/fullui.css.
export default defineConfig({
  build: {
    // old TV browsers (webOS 4/5 = Chrome 53/68, Tizen 5.x): transpile modern syntax down to ES2017
    target: 'es2017',
    cssTarget: 'chrome61',
    lib: { entry: 'src/main.ts', name: 'FullUI', formats: ['iife'], fileName: () => 'fullui.js' },
    cssCodeSplit: false,
    rollupOptions: { output: { assetFileNames: 'fullui[extname]' } },
    emptyOutDir: true,
  },
  test: { environment: 'jsdom', include: ['src/**/*.test.ts'] },
});
