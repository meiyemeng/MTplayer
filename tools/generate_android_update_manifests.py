from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


ASSETS = {
    "leanback": {
        "arm64_v8a": "MTPlayer-Android-leanback-arm64_v8a.apk",
        "armeabi_v7a": "MTPlayer-Android-leanback-armeabi_v7a.apk",
    },
    "mobile": {
        "arm64_v8a": "MTPlayer-Android-mobile-arm64_v8a.apk",
        "armeabi_v7a": "MTPlayer-Android-mobile-armeabi_v7a.apk",
    },
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--code", type=int, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    for mode, variants in ASSETS.items():
        paths = {abi: args.artifacts / filename for abi, filename in variants.items()}
        missing = [str(path) for path in paths.values() if not path.is_file()]
        if missing:
            raise FileNotFoundError(", ".join(missing))
        label = "电视" if mode == "leanback" else "手机"
        manifest = {
            "name": args.version,
            "code": args.code,
            "desc": f"基于 TVBox fish2018 260720-16：保留原版{label}界面与操作逻辑；加入 MT播放器品牌、账号云端上传下载、会员资源推送和 GitHub 在线更新。",
            "urls": {
                abi: f"https://github.com/meiyemeng/MTplayer/releases/latest/download/{path.name}"
                for abi, path in paths.items()
            },
            "sizes": {abi: path.stat().st_size for abi, path in paths.items()},
            "sha256s": {abi: sha256(path) for abi, path in paths.items()},
        }
        (args.output / f"{mode}.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
        )


if __name__ == "__main__":
    main()
