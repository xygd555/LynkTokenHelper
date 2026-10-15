from __future__ import annotations

import html
import json
from pathlib import Path
import re


def main() -> None:
    source = Path(__file__).resolve().parent
    output = source.parents[1] / "test-results" / "ui-directions" / "round-01"
    output.mkdir(parents=True, exist_ok=True)
    content = json.loads((source / "content.json").read_text(encoding="utf-8"))
    for name in ("minimal", "graphite", "guided"):
        template = (source / f"{name}.html").read_text(encoding="utf-8")
        rendered = re.sub(r"\{\{(\w+)\}\}", lambda match: html.escape(content[match[1]]), template)
        (output / f"{name}.html").write_text(rendered, encoding="utf-8")
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
