import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The sample UI served by the WebApp server (server/Program.cs statically serves client/dist).
// Dev mode proxies REST + WebSocket to the .NET server on :5210 so the client can be iterated
// without rebuilding the server.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://localhost:5210", changeOrigin: true },
      "/ws": { target: "ws://localhost:5210", ws: true },
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: true,
  },
});
