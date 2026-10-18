#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
#
# Reads the PE optional header of one or more executables and reports the Subsystem field.
#
# Why this exists: the claim "double-clicking vht-server.exe opens a window and no console window
# ever appears" is a property of the image, not of the code - Subsystem = 2 (Windows GUI) or 3
# (Windows CUI). Asserting it in prose proves nothing; this prints the bytes MSBuild wrote into the
# PE header, so the claim can be checked on the shipped binaries:
#
#   python scripts/check-subsystem.py dist/portable/vht-server.exe dist/portable/vht-client.exe \
#                                    dist/portable/vht.exe
#
# Exit code 0 when every file's subsystem matches its expectation, 1 otherwise. Pass
# `--expect N[,N...]` (or one of the named presets in PRESETS) to state what is expected; with no
# expectation the report is informational and the exit code is 0.

import struct
import sys

SUBSYSTEMS = {
    0: "UNKNOWN",
    1: "NATIVE",
    2: "WINDOWS_GUI",
    3: "WINDOWS_CUI",
    5: "OS2_CUI",
    7: "POSIX_CUI",
    9: "WINDOWS_CE_GUI",
    10: "EFI_APPLICATION",
}

# What each shipped image must be. GUI images must NOT be console images: that is the whole point.
PRESETS = {
    "gui": {2},
    "console": {3},
}


def read_subsystem(path):
    with open(path, "rb") as handle:
        dos = handle.read(64)
        if len(dos) < 64 or dos[:2] != b"MZ":
            raise ValueError("not an MZ executable")

        (pe_offset,) = struct.unpack_from("<I", dos, 0x3C)
        handle.seek(pe_offset)
        header = handle.read(256)
        if len(header) < 128 or header[:4] != b"PE\0\0":
            raise ValueError("no PE signature at 0x%X" % pe_offset)

        machine, section_count = struct.unpack_from("<HH", header, 4)
        (optional_size,) = struct.unpack_from("<H", header, 20)
        (magic,) = struct.unpack_from("<H", header, 24)

        # Offsets are from the start of the PE signature. The optional header's Subsystem field sits
        # after CheckSum: at 0x44 (68) in a PE32+ image, and at 0x48 (72) in a PE32 image, which has
        # the extra 4-byte BaseOfData field the 64-bit header dropped.
        if magic == 0x10B:       # PE32
            subsystem_offset = 24 + 72
            image_base_size = 4
        elif magic == 0x20B:     # PE32+
            subsystem_offset = 24 + 68
            image_base_size = 8
        else:
            raise ValueError("unknown optional header magic 0x%X" % magic)

        (subsystem,) = struct.unpack_from("<H", header, subsystem_offset)
        (dll_characteristics,) = struct.unpack_from("<H", header, subsystem_offset + 2)

    return {
        "path": path,
        "machine": machine,
        "magic": magic,
        "sections": section_count,
        "optional_size": optional_size,
        "image_base_size": image_base_size,
        "subsystem": subsystem,
        "dll_characteristics": dll_characteristics,
    }


def expected_for(name):
    lowered = name.lower()
    if lowered in ("vht-server.exe", "vht-client.exe"):
        return PRESETS["gui"]
    if lowered in ("vht.exe",):
        return PRESETS["console"]
    return None


def main(argv):
    explicit = None
    if "--expect" in argv:
        index = argv.index("--expect")
        if index + 1 >= len(argv):
            print("--expect needs a value: a preset (%s) or comma-separated subsystem numbers"
                  % ", ".join(sorted(PRESETS)), file=sys.stderr)
            return 2
        value = argv[index + 1]
        argv = argv[:index] + argv[index + 2:]
        # Look the value up as a preset FIRST; only parse numbers when it is not one. The two
        # branches must stay exclusive - evaluating the numeric parse eagerly makes every preset
        # name raise ValueError before the lookup can match.
        if value.lower() in PRESETS:
            explicit = PRESETS[value.lower()]
        else:
            try:
                explicit = {int(part) for part in value.split(",")}
            except ValueError:
                print("--expect %r is neither a preset (%s) nor comma-separated subsystem numbers"
                      % (value, ", ".join(sorted(PRESETS))), file=sys.stderr)
                return 2

    paths = argv[1:]
    if not paths:
        print(__doc__ or "usage: check-subsystem.py <exe> [<exe> ...]", file=sys.stderr)
        return 2

    failures = 0
    for path in paths:
        name = path.replace("\\", "/").rsplit("/", 1)[-1]
        expectation = explicit if explicit is not None else expected_for(name)
        try:
            info = read_subsystem(path)
        except (OSError, ValueError) as error:
            print("%-18s ERROR %s" % (name, error))
            failures += 1
            continue

        subsystem = info["subsystem"]
        label = SUBSYSTEMS.get(subsystem, "UNKNOWN(%d)" % subsystem)
        verdict = ""
        if expectation is not None:
            ok = subsystem in expectation
            verdict = " OK" if ok else " MISMATCH (expected %s)" % ",".join(
                sorted(SUBSYSTEMS.get(value, str(value)) for value in expectation))
            if not ok:
                failures += 1

        print("%-18s machine=0x%04X magic=0x%03X sections=%d Subsystem=%d (%-11s)%s"
              % (name, info["machine"], info["magic"], info["sections"], subsystem, label, verdict))

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
