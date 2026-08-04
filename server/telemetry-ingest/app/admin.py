import argparse
import secrets
from pathlib import Path

from .security import hash_invite
from .store import Store


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["create-invite"])
    parser.add_argument("--data-root", default="/data")
    args = parser.parse_args()

    store = Store(Path(args.data_root))
    store.initialize()
    if args.command == "create-invite":
        raw = secrets.token_hex(16).upper()
        code = "-".join(raw[index : index + 8] for index in range(0, len(raw), 8))
        store.add_invite(hash_invite(code))
        print(code)


if __name__ == "__main__":
    main()
