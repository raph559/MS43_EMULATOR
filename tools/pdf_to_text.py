"""Extract the text of a PDF manual, one "=== page N ===" header per page.

    python -E sim/tools/pdf_to_text.py IN.pdf OUT.txt

Needs pypdf. Run it with an interpreter that has pypdf in its user site (hence -E rather
than -I). The output feeds sim/tools/extract_sfr_table.py and is grepped for the flag and
timing rules the CPU tests pin.
"""
import sys

import pypdf


def main(src, dst):
    reader = pypdf.PdfReader(src)
    with open(dst, "w", encoding="utf-8") as out:
        for number, page in enumerate(reader.pages, start=1):
            out.write("\n=== page %d ===\n" % number)
            out.write(page.extract_text() or "")
    print("%d pages" % len(reader.pages))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
