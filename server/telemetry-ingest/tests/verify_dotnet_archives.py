"""Read real net48/net10 CaptureArchiveWriter output with the server reader."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.capture_archive import CaptureArchive


def main(root):
    root = Path(root)
    expected = {path.name: path.read_bytes() for path in (root / "expected").iterdir()}
    for name in ("legacy.zip", "zstd.zip"):
        with CaptureArchive(root / name) as archive:
            for member, data in expected.items():
                if archive.read(member) != data:
                    raise AssertionError(f"{name}: bytes changed for {member}")
        print(f"PASS Python read of real C# writer: {name} ({len(expected)} members)")


if __name__ == "__main__":
    main(sys.argv[1])
