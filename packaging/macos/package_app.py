from __future__ import annotations

import argparse
import os
from pathlib import Path
import zipfile


def main() -> None:
    parser = argparse.ArgumentParser(description="Package the MTPlayer macOS app bundle with Unix permissions.")
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    app = args.app.resolve()
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(app.rglob("*")):
            if not path.is_file():
                continue
            relative = Path(app.name) / path.relative_to(app)
            info = zipfile.ZipInfo(relative.as_posix())
            info.create_system = 3
            executable = path.parent.name == "MacOS"
            info.external_attr = ((0o100755 if executable else 0o100644) << 16)
            info.compress_type = zipfile.ZIP_DEFLATED
            with path.open("rb") as source:
                archive.writestr(info, source.read(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)

    print(output)


if __name__ == "__main__":
    main()
