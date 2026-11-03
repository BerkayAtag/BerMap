import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'path';

export default defineConfig(({ mode }) => {
  // BERMAP_API_URL may come from the shell or a .env file; defaults to the local API.
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = process.env.BERMAP_API_URL || env.BERMAP_API_URL || 'http://localhost:5294';

  return {
    plugins: [
      react(),
      tailwindcss(),
    ],
    resolve: {
      alias: {
        '@': path.resolve(__dirname, './src'),
      },
    },
    server: {
      port: 3000,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
          secure: false,
        },
      },
    },
    build: {
      outDir: 'dist',
      sourcemap: false,
      rollupOptions: {
        output: {
          manualChunks: {
            vendor: ['react', 'react-dom', 'react-router-dom'],
            query: ['@tanstack/react-query'],
            charts: ['echarts', 'echarts-for-react'],
            flow: ['@xyflow/react'],
          },
        },
      },
    },
  };
});
