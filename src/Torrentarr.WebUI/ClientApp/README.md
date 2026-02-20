# Torrentarr React Frontend

This directory contains the React-based frontend for Torrentarr WebUI.

## Development

### Prerequisites
- Node.js 18+ (LTS recommended)
- npm or yarn

### Setup

```bash
# Install dependencies
npm install

# Start development server (proxies API calls to http://localhost:5000)
npm start
```

The development server will start on http://localhost:3000 and proxy all API calls to the backend running on port 5000.

### Building for Production

```bash
# Build optimized production bundle
npm run build
```

This creates an optimized build in the `build/` directory, which the ASP.NET Core backend will serve.

## Project Structure

```
ClientApp/
├── public/
│   └── index.html          # HTML template
├── src/
│   ├── components/         # React components
│   │   ├── Dashboard.js    # Dashboard view
│   │   ├── Movies.js       # Movies list
│   │   ├── Episodes.js     # Episodes list
│   │   └── Torrents.js     # Torrents list
│   ├── App.js              # Main app component
│   ├── index.js            # Entry point
│   └── index.css           # Global styles
├── package.json
└── README.md
```

## Features

- **Dashboard**: System status, qBittorrent and Arr instance health, statistics
- **Movies**: Paginated movies list from Radarr
- **Episodes**: Paginated episodes list from Sonarr
- **Torrents**: Real-time torrent tracking with auto-refresh

## API Integration

The frontend communicates with the backend via REST API:

- `GET /api/status` - System status
- `GET /api/stats` - Statistics
- `GET /api/movies` - Movies list
- `GET /api/episodes` - Episodes list
- `GET /api/torrents` - Torrents list

All endpoints support pagination via `?page=1&pageSize=50` query parameters.

## Styling

The app uses a custom dark theme with:
- Background: `#1a1a1a`
- Cards: `#2a2a2a`
- Accent: `#4CAF50` (green)
- Text: `#ffffff` / `#888888`

## Future Enhancements

- [ ] SignalR integration for real-time updates
- [ ] Advanced filtering and sorting
- [ ] Torrent details modal
- [ ] Configuration editor
- [ ] Process management UI
- [ ] Charts and graphs for statistics
- [ ] Dark/light theme toggle
