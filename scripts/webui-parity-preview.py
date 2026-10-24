"""Read-only service fixtures for comparing packaged WebUIs on ports 16970/16969.

Only synthetic config is mutated; no live service is contacted.
Build Torrentarr webui first; the sibling qBitrr checkout supplies its static build.
"""
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from pathlib import Path
from urllib.parse import urlparse, parse_qs, unquote
import argparse
import json
import threading
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--scenario', choices=['full', 'empty', 'no-readarr', 'offline', 'large', 'missing-images', 'login', 'setup', 'update'], default='full')
scenario = parser.parse_args().scenario
root = Path(__file__).resolve().parents[1]
s = (root / 'webui/src/config/configFields.generated.ts').read_text()
schema = json.loads(s[s.index('{'):s.rindex('}') + 1])

def defaults(section):
    out = {}
    for f in schema['sections'][section]:
        t = out
        for k in f['path'][:-1]:
            t = t.setdefault(k, {})
        t[f['path'][-1]] = f.get('default')
    return out
fixtures = json.loads((root / 'webui/fixtures/parity.json').read_text())
arrs = fixtures['arrs']
assert len({a['name'] for a in arrs}) == len(arrs)
assert {a['type'] for a in arrs} == {'radarr', 'sonarr', 'lidarr', 'readarr'}
counts, artist, album, author, book, movie, series, torrent, category = (fixtures[name] for name in ['counts', 'artist', 'album', 'author', 'book', 'movie', 'series', 'torrent', 'category'])
config = {'Settings': defaults('Settings'), 'WebUI': defaults('WebUI'), 'qBit-main': defaults('qBit')}
config['WebUI'].update(LiveArr=False, Theme='Dark', AuthDisabled=True)
for a in arrs:
    config[a['name']] = defaults('Arr')
    config[a['name']].update(Managed=True, URI='http://example.invalid', APIKey='[redacted]', Category=a['category'])
if scenario == 'empty':
    arrs = []
    config = {k: v for k, v in config.items() if k in ('Settings', 'WebUI')}
elif scenario == 'no-readarr':
    arrs = [a for a in arrs if a['type'] != 'readarr']
    config.pop('Readarr-demo')
elif scenario == 'offline':
    for a in arrs:
        a['alive'] = False


class Handler(SimpleHTTPRequestHandler):

    def log_message(self, *args):
        pass

    def reply(self, data, status=200, ctype='application/json'):
        payload = json.dumps(data).encode() if ctype == 'application/json' else data.encode()
        self.send_response(status)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        data = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))) or '{}')
        if self.path.endswith('/config'):
            for path, val in data.get('changes', {}).items():
                keys = path.split('.')
                target = config
                for key in keys[:-1]:
                    target = target.setdefault(key, {})
                if val is None:
                    target.pop(keys[-1], None)
                else:
                    target[keys[-1]] = val
            self.reply(dict(status='ok', configReloaded=False, reloadType='none', affectedInstances=[]))
        else:
            self.reply(dict(success=True, status='ok', message='Fixture connection', qualityProfiles=[dict(id=1, name='HD-1080p')]))

    def do_GET(self):
        u = urlparse(self.path)
        p = unquote(u.path)
        q = parse_qs(u.query)
        if p.startswith('/prefix/'):
            p = p[7:]
        if p.startswith('/web/') or p.startswith('/api/'):
            if scenario in ('login', 'setup') and p != '/web/meta':
                return self.reply(dict(error='Authentication required'), 401)
            if p == '/web/config/schema':
                return self.reply(schema)
            if p == '/web/config':
                return self.reply(config)
            if p == '/web/meta':
                return self.reply(dict(current_version='6.15.2', ready=True, auth_required=scenario in ('login', 'setup'), local_auth_enabled=True, setup_required=scenario == 'setup', url_base='/prefix' if u.path.startswith('/prefix/') else '', repository_url='https://github.com/Feramance/Torrentarr', installation_type='docker', auto_update_supported=False, update_available=scenario == 'update', latest_version='6.15.3', changelog='Fixture release notes'))
            if p == '/web/status':
                return self.reply(dict(ready=True, arrs=arrs, qbitInstances={'qBit-main': dict(alive=scenario != 'offline', host='qbittorrent', port=8080, version='5.1')} if scenario != 'empty' else {}))
            if p == '/web/arr':
                return self.reply(dict(ready=True, arr=arrs))
            if p == '/web/processes':
                procs = []
                for a in arrs:
                    row = dict(category=a['category'], name=a['name'], kind='worker', pid=42, alive=scenario != 'offline', restartKey=a['name'], restartable=True, searchSummary='', queueCount=1, categoryCount=2, status='Torrent: Sleeping; Search: Sleeping', torrent=dict(phase='Sleeping', startedAt='2026-10-18T12:00:00Z', wakeAt='2026-10-18T12:01:00Z'), search=dict(phase='Sleeping', startedAt='2026-10-18T12:00:00Z'))
                    if self.server.reference:
                        procs.extend([{**row, 'kind': kind} for kind in ['search', 'torrent']])
                    else:
                        procs.append(row)
                return self.reply(dict(processes=procs))
            if p == '/web/qbit/categories':
                return self.reply(dict(ready=True, categories=[] if scenario == 'empty' else [category]))
            if p == '/web/qbit/overview':
                return self.reply(dict(ready=True, instances=[] if scenario == 'empty' else ['qBit-main'], categories=[] if scenario == 'empty' else [category]))
            if p == '/web/logs':
                return self.reply(dict(files=['torrentarr.log'] if self.server.reference else [dict(name='torrentarr.log', size=2048, modified='2026-10-18T12:00:00Z')]))
            if p == '/web/logs/search':
                return self.reply(dict(query='fixture', truncated=False, matches=[], files_searched=['torrentarr.log']))
            if p.startswith('/web/logs/'):
                text = '2026-10-18 12:00:00 [INFO] Fixture ready\n2026-10-18 12:00:01 [WARNING] Waiting for search\n'
                if q.get('format') == ['json']:
                    return self.reply(dict(text=text, lines=text.splitlines(), start=0, end=2, total=2, cursor='fixture:2', rotation='fixture', rotated=False, hasMore=False))
                return self.reply(text, ctype='text/plain')
            if '/thumb' in p and scenario == 'missing-images':
                return self.reply({}, 404)
            if '/thumb' in p:
                return self.reply('<svg xmlns="http://www.w3.org/2000/svg" width="300" height="450"><rect width="300" height="450" fill="#334155"/><text x="30" y="220" fill="white">Fixture poster</text></svg>', ctype='image/svg+xml')
            if scenario == 'offline' and p.startswith(('/web/radarr/', '/web/sonarr/', '/web/lidarr/', '/web/readarr/')):
                return self.reply(dict(error='Fixture service unavailable'), 503)
            extra = dict(counts=counts, counts_tracks=counts, category='demo', total=1, page=0, page_size=50)
            if p.startswith('/web/radarr/'):
                rows = [dict(movie, id=i + 1, title=f'Fixture Movie {i + 1}') for i in range(1001)] if scenario == 'large' else [movie]
                page = int(q.get('page', [0])[0])
                size = int(q.get('page_size', [50])[0])
                extra.update(total=len(rows), page=page, page_size=size)
                return self.reply(dict(**extra, movies=rows[page * size:(page + 1) * size]))
            if p.startswith('/web/sonarr/'):
                return self.reply(dict(**extra, series=[series]))
            if p.startswith('/web/lidarr/'):
                if '/artist/' in p:
                    return self.reply(dict(**extra, artist=artist, albums=[album]))
                if p.endswith('/albums'):
                    return self.reply(dict(**extra, albums=[album]))
                return self.reply(dict(**extra, album_total=2, artists=[dict(artist=artist)]))
            if p.startswith('/web/readarr/'):
                if '/author/' in p:
                    return self.reply(dict(**extra, author=author, books=[book]))
                return self.reply(dict(**extra, book_total=2, authors=[dict(author=author)]))
            if p == '/api/torrent-clients':
                return self.reply([])
            if 'maintenance' in p:
                return self.reply([] if p.endswith('history') else dict(state='Idle', armed=False))
            return self.reply({}, 404)
        folder = root.parent / 'qBitrr/qBitrr/static' if self.server.reference else root / 'src/Torrentarr.Host/wwwroot'
        name = p.lstrip('/')
        if name in ['', 'ui', 'ui/', 'static/index.html', 'index.html']:
            return self.reply((folder / 'index.html').read_text().replace('<head>', '<head><base href="' + ('/prefix/' if u.path.startswith('/prefix/') else '/') + '">'), ctype='text/html')
        self.directory = str(folder)
        self.path = p
        return super().do_GET()
for port, reference in [(16970, False), (16969, True)]:
    server = ThreadingHTTPServer(('127.0.0.1', port), Handler)
    server.reference = reference
    threading.Thread(target=server.serve_forever, daemon=True).start()
print(f'Fixture previews ({scenario}): 16970 Torrentarr, 16969 qBitrr', flush=True)
try:
    threading.Event().wait()
except KeyboardInterrupt:
    pass
