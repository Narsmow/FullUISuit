import { defineConfig } from 'vite';

// Single IIFE bundle + CSS, embedded into the plugin as Web/fullui.js and Web/fullui.css.
export default defineConfig({
  build: {
    lib: { entry: 'src/main.ts', name: 'FullUI', formats: ['iife'], fileName: () => 'fullui.js' },
    cssCodeSplit: false,
    rollupOptions: { output: { assetFileNames: 'fullui[extname]' } },
    emptyOutDir: true,
  },
  test: { environment: 'jsdom', include: ['src/**/*.test.ts'] },
});
