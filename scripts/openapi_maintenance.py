"""Torrentarr maintenance extensions, kept separate from the pinned upstream API."""
from copy import deepcopy
from pathlib import Path
import re

OPERATIONS = ['OrphanScan', 'HardlinkAudit', 'UnregisteredCleanup', 'CategoryReconcile',
              'AutomaticManagement', 'PrivateTagging', 'TrackerErrorTagging', 'RepairPaused',
              'RecycleRetention', 'ErrorRecovery']
KINDS = ['QuarantineFile', 'AddTag', 'RemoveTag', 'SetCategory', 'SetAutomaticManagement',
         'Recheck', 'Resume', 'RecycleContent', 'DeleteExpiredRecycleItem', 'RegisterRecycleEntry']
EXTENSION_PATHS = {f'{prefix}/maintenance/{name}' for prefix in ('/web', '/api')
                   for name in ('status', 'history', 'preview', 'apply', 'arm', 'disarm', 'run', 'cancel')} | {f'{prefix}/torrent-automation/status' for prefix in ('/web', '/api')}


def apply(spec):
    root = Path(__file__).resolve().parents[1]
    schemas = spec.setdefault('components', {}).setdefault('schemas', {})
    sources = [root / 'src/Torrentarr.Core/Models/MaintenanceModels.cs', root / 'src/Torrentarr.Core/Models/ProtectionDecision.cs']
    classes = {}
    records = {}
    for path in sources:
        for record in re.finditer(r'public sealed record (\w+)\(([^;]+)\);', path.read_text(), re.S):
            classes[record[1]] = ""
            records[record[1]] = [part.strip().split(" = ")[0].split() for part in record[2].split(",")]
        for match in re.finditer(r'public sealed class (\w+)\s*\{(.*?)(?=\npublic |\Z)', path.read_text(), re.S):
            classes[match[1]] = match[2]

    def wire_type(name):
        nullable = name.endswith('?')
        name = name.rstrip('?')
        if name.startswith('List<'):
            result = {'type': 'array', 'items': wire_type(name[5:-1])}
        elif name.startswith('Dictionary<string, '):
            result = {'type': 'object', 'additionalProperties': wire_type(name[19:-1].replace('Torrentarr.Core.Configuration.', ''))}
        elif name == 'string': result = {'type': 'string'}
        elif name == 'bool': result = {'type': 'boolean'}
        elif name in ('int', 'long'): result = {'type': 'integer', 'format': 'int64' if name == 'long' else 'int32'}
        elif name == 'double': result = {'type': 'number', 'format': 'double'}
        elif name == 'DateTimeOffset': result = {'type': 'string', 'format': 'date-time'}
        elif name == 'MaintenanceOperation': result = {'oneOf': [{'type': 'string', 'enum': OPERATIONS}, {'type': 'integer'}], 'description': 'Unknown numeric IDs are retained in historical results; preview accepts all ten defined maintenance operations.'}
        elif name == 'MaintenanceActionKind': result = {'oneOf': [{'type': 'string', 'enum': KINDS}, {'type': 'integer'}], 'description': 'Numeric retired IDs remain readable in history.'}
        elif name in classes: result = {'$ref': f'#/components/schemas/{name}'}
        else: result = {'type': 'object', 'additionalProperties': True}
        if nullable: result = {**result, 'nullable': True}
        return result

    for name, body in classes.items():
        if name == 'ProtectionObservation': continue
        props = {}
        for prop in re.finditer(r'public ([\w<>?,. ]+) (\w+)\s*(?:\{|=>)', body):
            if prop[2] == 'InspectionCursors': continue
            props[prop[2][0].lower() + prop[2][1:]] = wire_type(prop[1].strip())
        for field_type, field_name in records.get(name, []):
            props[field_name[0].lower() + field_name[1:]] = wire_type(field_type)
        schemas[name] = {'type': 'object', 'properties': props}
    for name in ('armed', 'armedOperations'):
        schemas['MaintenanceStatus']['properties'][name].update({
            'deprecated': True, 'description': 'Compatibility field; always false/empty. Use per-operation executionMode and authorized status.'})
    schemas['MaintenanceOperationStatus']['properties']['executionMode']['enum'] = ['manual', 'automatic']
    for prefix in ('/web', '/api'):
        for name, method, request, response in [
            ('status', 'get', None, 'MaintenanceStatus'),
            ('history', 'get', None, 'MaintenanceRunSummary'),
            ('preview', 'post', 'MaintenancePreviewRequest', 'MaintenancePlan'),
            ('apply', 'post', 'MaintenanceApplyRequest', 'MaintenanceRunSummary'),
            ('arm', 'post', 'MaintenanceArmRequest', 'MaintenanceStatus'),
            ('disarm', 'post', 'MaintenanceClientsRequest', 'MaintenanceStatus'),
            ('run', 'post', 'MaintenanceClientsRequest', 'MaintenanceRunSummary'),
            ('cancel', 'post', None, None),
        ]:
            result = {'$ref': f'#/components/schemas/{response}'}
            if name == 'history': result = {'type': 'array', 'items': result}
            op = {'tags': ['Torrentarr Maintenance'], 'summary': f'Maintenance {name}',
                  'description': 'Manage supports all ten maintenance operations. Draft previews cannot execute. Saved plans expire at their configured TTL and execute once. Manual applies a reviewed preview once. Automatic runs on its saved schedule and persists across restarts; saving never runs a tool immediately. /api requires a Bearer token; /web follows configured login requirements.',
                  'responses': {'202' if name == 'cancel' else '200': {'description': 'Accepted' if name == 'cancel' else 'OK',
                      **({'content': {'application/json': {'schema': result}}} if response else {})},
                      '401': {'description': 'Authentication required'}}}
            if prefix == '/api': op['security'] = [{'bearerAuth': []}]
            if request:
                op['requestBody'] = {'required': True, 'content': {'application/json': {'schema': {'$ref': f'#/components/schemas/{request}'}}}}
                op['responses']['400'] = {'description': 'Invalid selection or configuration'}
            if name == 'apply':
                op['responses'].update({'404': {'description': 'No executable saved plan'}, '409': {'description': 'Stale, expired, consumed, unauthorized, or concurrent plan'}})
            if name in ('arm', 'disarm'):
                op['deprecated'] = True
                op['description'] = 'Temporary session authorization has been removed. Save Automatic mode to enable scheduling, or Manual mode to stop scheduling.'
                op.pop('requestBody', None)
                op['responses'] = {'410': {'description': op['description']}, '401': {'description': 'Authentication required'}}
            if name == 'history': op['parameters'] = [{'name': 'limit', 'in': 'query', 'schema': {'type': 'integer', 'default': 50, 'minimum': 1, 'maximum': 500}}]
            spec['paths'][f'{prefix}/maintenance/{name}'] = {method: deepcopy(op)}

        op = {'tags': ['Torrentarr Automation'], 'summary': 'Protection and recovery status',
              'responses': {'200': {'description': 'Observed status; never authorizes mutation', 'content': {'application/json': {'schema': {'$ref': '#/components/schemas/TorrentAutomationStatus'}}}}, '401': {'description': 'Authentication required'}}}
        if prefix == '/api': op['security'] = [{'bearerAuth': []}]
        spec['paths'][f'{prefix}/torrent-automation/status'] = {'get': op}
