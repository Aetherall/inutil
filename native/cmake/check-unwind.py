#!/usr/bin/env python3
"""Fail the build if any hand-written asm proc ships without x64 unwind data.

il2cpp raises managed exceptions as MSVC C++ EH, and x64 SEH unwinds EXCLUSIVELY through
.pdata/.xdata — there is no frame-pointer chain fallback. Our thunks sit between the game's caller
and the hooked original, so a proc with no .pdata entry makes RtlLookupFunctionEntry miss, the
unwinder assume a LEAF frame (return address at [rsp], wrong by the whole frame) and walk into
garbage. The symptom is a game-side try/catch around a hooked call quietly ceasing to work.

The check is phrased against the FACT, not the three procs that were once missing it: it reads the
proc list out of the .S source (`.globl` in the text section) and requires each to be covered in the
linked PE. A newly added thunk that forgets its .seh_* directives fails here, on the build that adds
it — which a check naming today's symbols could never do.

Usage: check-unwind.py <objdump> <dll> <source.S>...
"""
import re
import struct
import subprocess
import sys


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    tbl = pe + 24 + struct.unpack_from("<H", data, pe + 20)[0]
    out = {}
    for i in range(nsec):
        o = tbl + i * 40
        name = data[o : o + 8].rstrip(b"\0").decode()
        out[name] = (
            struct.unpack_from("<I", data, o + 12)[0],  # virtual address (RVA)
            struct.unpack_from("<I", data, o + 8)[0],  # virtual size
            struct.unpack_from("<I", data, o + 20)[0],  # raw file offset
        )
    return out


def declared_procs(sources):
    """The procs the source claims to define — `.globl name` with a matching `name:` label."""
    procs = {}
    for src in sources:
        text = open(src).read()
        globls = set(re.findall(r"^\s*\.globl\s+(\S+)", text, re.M))
        labels = set(re.findall(r"^(\w+):", text, re.M))
        for name in sorted(globls & labels):
            procs[name] = src
    return procs


def main():
    objdump, dll, sources = sys.argv[1], sys.argv[2], sys.argv[3:]
    procs = declared_procs(sources)
    if not procs:
        print(f"!! check-unwind: no .globl procs found in {' '.join(sources)}", file=sys.stderr)
        return 2

    data = open(dll, "rb").read()
    secs = sections(data)
    text_rva = secs[".text"][0]
    pdata_size, pdata_off = secs[".pdata"][1], secs[".pdata"][2]
    ranges = []
    for i in range(pdata_size // 12):
        begin, end, _ = struct.unpack_from("<III", data, pdata_off + i * 12)
        if begin or end:
            ranges.append((begin, end))

    # objdump -t reports .text symbols as section-relative offsets; the RVA is text_rva + offset.
    syms = {}
    dump = subprocess.run([objdump, "-t", dll], capture_output=True, text=True, check=True).stdout
    for line in dump.splitlines():
        m = re.search(r"\(sec\s+1\).*\s0x([0-9a-f]+)\s+(\S+)$", line)
        if m:
            syms[m.group(2)] = text_rva + int(m.group(1), 16)

    missing = []
    for name, src in procs.items():
        rva = syms.get(name)
        if rva is None:
            missing.append(f"{name} ({src}): not found in {dll}'s .text symbols")
        elif not any(b <= rva < e for b, e in ranges):
            missing.append(f"{name} ({src}) @ RVA {rva:#x}: NO .pdata entry — add .seh_proc/.seh_endproc")

    if missing:
        print(
            "!! check-unwind: hand-written proc(s) ship with no x64 unwind data. A managed exception\n"
            "!! thrown by a hooked original cannot unwind through these frames; a game-side try/catch\n"
            "!! around a hooked call will break. See generic_thunk_post.S's header.\n"
            + "".join(f"!!   {m}\n" for m in missing),
            file=sys.stderr,
            end="",
        )
        return 1

    print(f"-- unwind data present for all {len(procs)} asm proc(s): {', '.join(sorted(procs))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
