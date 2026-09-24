"""Enable the tested local runtime without changing search sources or personal data."""
import argparse
import json
from pathlib import Path
import sqlite3

parser = argparse.ArgumentParser()
parser.add_argument('--database', type=Path, required=True)
parser.add_argument('--python', type=Path, required=True)
parser.add_argument('--backup', type=Path, required=True)
parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
parser.add_argument('--semantic-helper', action='store_true')
args = parser.parse_args()
if not args.database.is_file() or not args.python.is_file():
    raise SystemExit('Existing Scout database and Python runtime are required.')
with sqlite3.connect(args.database, timeout=5) as database:
    row = database.execute('SELECT json FROM settings WHERE id=1').fetchone()
    settings = json.loads(row[0]) if row else {}
    args.backup.parent.mkdir(parents=True, exist_ok=True)
    args.backup.write_text(json.dumps(settings, indent=2), encoding='utf-8')
    settings.update(AiEnabled=True, PythonPath=str(args.python.resolve()), Model='english', Device=args.device)
    if args.semantic_helper:
        settings['SemanticHelper'] = True
    database.execute('INSERT INTO settings(id,json) VALUES(1,?) ON CONFLICT(id) DO UPDATE SET json=excluded.json', (json.dumps(settings),))
print('Scout configured for local English Laya on ' + args.device + '. Search sources, pins, history and workspaces preserved.')
if args.semantic_helper:
    print('Optional local semantic helper enabled.')
