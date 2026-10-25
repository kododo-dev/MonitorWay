import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { wayModule } from '@kododo-dev/way-shell/vite'

// `npm run build`: src/way.tsx as a Way.Shell module (dist/way.js, dist/way.css), embedded by MonitorWay.UI.
// `npm run dev`: src/main.tsx with the module in WayDevHost; the API goes to the demo app (dotnet run in
// MonitorWay.Demo.Web).
export default defineConfig({
  plugins: [react(), wayModule()],
  server: { proxy: { '/monitor/api': process.env.MONITORWAY_BACKEND ?? 'http://localhost:5080' } },
})
