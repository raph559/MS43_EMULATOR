"""Linear-sweep listing of an MS43 image with tools/c167dis.py, for the C# decoder cross-check.

    python -I sim/tools/decode_listing.py BIN > py.txt
    dotnet run --project sim/src/Ms43Sim.Cli -- decode --listing BIN > cs.txt
    diff --strip-trailing-cr py.txt cs.txt

Each line is "%06X %d %s": file offset, size, mnemonic ("db?" where c167dis has no
instruction). The regions are the code regions of tools/xref.py.
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools"))
from c167dis import decode  # noqa: E402

REGIONS = [(0x0000, 0x3C00), (0x8000, 0xBA34), (0x10000, 0x6EA7A)]


def main(path):
    buf = open(path, "rb").read()
    out = []
    for start, end in REGIONS:
        off = start
        while off < end:
            insn = decode(buf, off, off)
            if insn is None:
                out.append("%06X 2 db?" % off)
                off += 2
                continue
            out.append("%06X %d %s" % (off, insn.size, insn.mnem))
            off += insn.size
    sys.stdout.write("\n".join(out) + "\n")


if __name__ == "__main__":
    main(sys.argv[1])
