#!/usr/bin/env python3
"""Simulate a database-level attacker editing one audit row, so `GET /api/platform/audit/verify` can catch it.

The attacker here has raw write access to the SQLite file (bypassing the app, which has no update API).
They rewrite one row's `after` (or `action`) column but cannot recompute the HMAC because they do not
hold Platform:AuditChainKey, so the hash chain breaks at exactly that row.

Usage:
  scripts/tamper-audit.py [DB_PATH] [--id N] [--column after|action]
Default DB_PATH: platform/host/App_Data/platform.db ; default row: newest row with a non-empty `after`.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path

DEFAULT_DB = Path(__file__).resolve().parent.parent / "platform" / "host" / "App_Data" / "platform.db"


def quote(identifier: str) -> str:
    return '"' + identifier.replace('"', '""') + '"'


def find_audit_table(conn: sqlite3.Connection) -> tuple[str, dict[str, str]]:
    """Locate the audit table by shape (id, action, after, prev_hash/prevHash, hash), whatever EF named it."""
    tables = [r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type = 'table'")]
    required = {"id", "action", "after", "prevhash", "hash"}
    for table in tables:
        columns = {r[1].lower().replace("_", ""): r[1] for r in conn.execute(f"PRAGMA table_info({quote(table)})")}
        if required.issubset(columns):
            return table, columns
    sys.exit("error: no audit table found (expected columns id, action, after, prevHash, hash). Run the app first.")


def tamper_after(value: str | None) -> str:
    """Make a plausible malicious edit: flip a decision/outcome, or inject a field."""
    if value:
        for old, new in (("approved", "rejected"), ("approve", "reject"), ("rejected", "approved")):
            if old in value:
                return value.replace(old, new, 1)
        try:
            data = json.loads(value)
            if isinstance(data, dict):
                data["tampered"] = True
                return json.dumps(data, separators=(",", ":"))
        except json.JSONDecodeError:
            pass
    return (value or "") + " [tampered]"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db", nargs="?", default=str(DEFAULT_DB), help="SQLite DB path")
    parser.add_argument("--id", type=int, help="audit row id to edit (default: newest row with an 'after' value)")
    parser.add_argument("--column", choices=["after", "action"], default="after", help="column to edit")
    args = parser.parse_args()

    db = Path(args.db)
    if not db.is_file():
        sys.exit(f"error: database not found: {db}")

    conn = sqlite3.connect(str(db), timeout=10, isolation_level=None)
    try:
        table, cols = find_audit_table(conn)
        id_col, target = cols["id"], cols[args.column]
        if args.id is None:
            row = conn.execute(
                f"SELECT {quote(id_col)} FROM {quote(table)} "
                f"WHERE {quote(target)} IS NOT NULL AND {quote(target)} <> '' "
                f"ORDER BY {quote(id_col)} DESC LIMIT 1"
            ).fetchone()
            if row is None:
                sys.exit(f"error: no audit row with a non-empty '{args.column}' to tamper with")
            row_id = row[0]
        else:
            row_id = args.id

        found = conn.execute(
            f"SELECT {quote(target)} FROM {quote(table)} WHERE {quote(id_col)} = ?", (row_id,)
        ).fetchone()
        if found is None:
            sys.exit(f"error: audit row {row_id} not found")
        before = found[0]
        after = tamper_after(before) if args.column == "after" else (before or "") + ".tampered"

        # A real attacker with file access can also drop any append-only triggers; do that, then restore them.
        triggers = conn.execute(
            "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = ?", (table,)
        ).fetchall()
        conn.execute("BEGIN IMMEDIATE")
        try:
            for name, _ in triggers:
                conn.execute(f"DROP TRIGGER {quote(name)}")
            conn.execute(
                f"UPDATE {quote(table)} SET {quote(target)} = ? WHERE {quote(id_col)} = ?", (after, row_id)
            )
            for _, sql in triggers:
                conn.execute(sql)
            conn.execute("COMMIT")
        except sqlite3.Error:
            conn.execute("ROLLBACK")
            raise
    finally:
        conn.close()

    print(f"Tampered audit row id={row_id} in table '{table}' ({db})")
    if triggers:
        print(f"  (temporarily dropped and restored {len(triggers)} trigger(s) on the table)")
    print(f"  column : {target}")
    print(f"  before : {before}")
    print(f"  after  : {after}")
    print("The stored hash was NOT recomputed (the attacker lacks Platform:AuditChainKey).")
    print(f"Now run 'Verify audit chain' (GET /api/platform/audit/verify): expect firstBrokenId = {row_id}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
