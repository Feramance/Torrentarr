# WebUI Development

The React and TypeScript frontend is in `webui/`. Start the Host from the repository root to provide the backend API.

```bash
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

In another terminal, run the frontend.

```bash
cd webui
npm ci
npm run dev
```

Open `http://localhost:3000`. Vite reloads frontend changes and proxies `/api` and `/web` to `http://localhost:6969`. If the backend listens on a different port, update the proxy targets in your local `webui/vite.config.ts`.

## Where to make changes

- Route and application layout code lives in `webui/src/App.tsx` and `webui/src/AppShell.tsx`.
- Pages, shared components, and API calls live in `webui/src/pages/`, `webui/src/components/`, and `webui/src/api/`.
- Frontend tests live in `webui/src/__tests__/`. See [Testing](testing.md) for test and fixture guidance.

## Validate and build

Run these commands from `webui/`.

```bash
npm run lint
npm test -- --run
npm run build
```

The build type-checks the project and writes the production bundle directly to `src/Torrentarr.Host/wwwroot/`. Build the frontend before building or publishing the Host so it includes the current assets. Regenerate the [OpenAPI spec](release-process.md#regenerating-the-openapi-spec) when changing API endpoints.
