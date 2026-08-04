import argparse
import re
import sqlite3
import time
from pathlib import Path


def cleanup(data_root: str, install_id: str) -> int:
    if not re.fullmatch(r"[a-f0-9]{32}", install_id):
        raise SystemExit("invalid test install id")

    root = Path(data_root).resolve()
    accepted = (root / "accepted").resolve()
    database = root / "telemetry.sqlite3"
    with sqlite3.connect(database) as connection:
        rows = connection.execute(
            "SELECT relative_path FROM captures WHERE install_id=?", (install_id,)
        ).fetchall()
        for (relative_path,) in rows:
            candidate = (root / relative_path).resolve()
            if not candidate.is_relative_to(accepted):
                raise SystemExit("refusing to remove a path outside accepted")
            candidate.unlink(missing_ok=True)
        connection.execute("DELETE FROM captures WHERE install_id=?", (install_id,))
        connection.execute("DELETE FROM nonces WHERE install_id=?", (install_id,))
        connection.execute("DELETE FROM byte_budgets WHERE subject=?", (install_id,))
        connection.execute("DELETE FROM invite_codes WHERE used_by=?", (install_id,))
        connection.execute("DELETE FROM installations WHERE install_id=?", (install_id,))
    return len(rows)


def remove_recent_unused_invites(data_root: str, minutes: int) -> int:
    if minutes <= 0 or minutes > 1440:
        raise SystemExit("unused invite cleanup window is invalid")
    root = Path(data_root).resolve()
    with sqlite3.connect(root / "telemetry.sqlite3") as connection:
        result = connection.execute(
            "DELETE FROM invite_codes WHERE used_at IS NULL AND created_at>=?",
            (int(time.time()) - minutes * 60,),
        )
        return result.rowcount


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", default="/data")
    parser.add_argument("--install-id")
    parser.add_argument("--remove-unused-invites-since-minutes", type=int)
    args = parser.parse_args()
    if args.install_id:
        count = cleanup(args.data_root, args.install_id)
        print(f"removed test installation {args.install_id} and {count} capture(s)")
    if args.remove_unused_invites_since_minutes:
        count = remove_recent_unused_invites(
            args.data_root, args.remove_unused_invites_since_minutes
        )
        print(f"removed {count} recent unused test invite(s)")
    if not args.install_id and not args.remove_unused_invites_since_minutes:
        parser.error("one cleanup action is required")


if __name__ == "__main__":
    main()
