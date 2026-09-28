import { defineConfig } from 'vite'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  base: '/olaf/',
  plugins: [tailwindcss()],
  build: {
    outDir: 'dist'
  }
})
