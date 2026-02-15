SteamDepotDownloaderService Frontend (QEStudio)
==============================================

This is an independent frontend for the SteamDepotDownloaderService HTTP API + WebSocket.

## Requirements

- Node.js 18+ recommended

## Setup

```bash
cd frontend
npm install
```

## Development

Option A (recommended): use Vite proxy to avoid CORS

```bash
cd frontend
VITE_PROXY_TARGET=http://127.0.0.1:18080 npm run dev
```

In this mode, you can set API Base URL to `http://127.0.0.1:5173` in the UI (same origin).

Option B: direct connect

- Start service with `STEAMDDS_CORS_ORIGINS=http://localhost:5173,http://127.0.0.1:5173`
- Set API Base URL to your service address (for example `http://127.0.0.1:18080`)

## Build

```bash
cd frontend
npm run build
```
