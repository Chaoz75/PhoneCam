"""
Standalone verification of the OSC wire-format logic used in src/Osc/OscParser.cs.

The sandbox this was written in has no .NET/Mono toolchain and no network access to install
one, so the parser's byte-level logic (string padding, big-endian float decoding, address /
type-tag extraction) is re-implemented here line-for-line and checked against synthetic
packets built to match LOTA's documented wire format. This does not compile or execute the
actual C# file - treat it as a logic cross-check, and re-verify with the real assembly once
you can build the project on Windows against your CarX install.

Run: python3 tests/osc_wire_format_check.py
"""

import struct


def build_osc_message(address, type_tags, values):
    def pad4(b):
        n = len(b)
        pad = (4 - (n % 4)) % 4
        if pad == 0:
            pad = 4  # OSC strings always end with >=1 NUL, total length a multiple of 4
        return b + b"\x00" * pad

    addr_bytes = pad4(address.encode("ascii"))
    tags_bytes = pad4(("," + type_tags).encode("ascii"))

    arg_bytes = b""
    for tag, v in zip(type_tags, values):
        if tag == "f":
            arg_bytes += struct.pack(">f", v)
        elif tag == "i":
            arg_bytes += struct.pack(">i", v)
        else:
            raise ValueError("unsupported tag in test builder")

    return addr_bytes + tags_bytes + arg_bytes


def parse_osc_message(buf):
    """Mirrors OscParser.TryParseMessage's algorithm exactly: NUL-terminated padded strings,
    big-endian float/int args, 4-byte alignment throughout."""
    length = len(buf)

    def read_padded_string(offset):
        start = offset
        i = start
        while i < length and buf[i] != 0:
            i += 1
        if i >= length:
            raise ValueError("no NUL terminator")
        s = buf[start:i].decode("ascii")
        consumed = (i - start) + 1
        padded = (consumed + 3) & ~3
        new_offset = start + padded
        if new_offset > length:
            raise ValueError("truncated")
        return s, new_offset

    address, offset = read_padded_string(0)
    if not address.startswith("/"):
        raise ValueError("bad address")

    if offset >= length:
        return address, []

    type_tags, offset = read_padded_string(offset)
    if not type_tags.startswith(","):
        raise ValueError("bad type tag string")

    args = []
    for tag in type_tags[1:]:
        if tag == "f":
            v = struct.unpack(">f", buf[offset:offset + 4])[0]
            offset += 4
            args.append(v)
        elif tag == "i":
            v = struct.unpack(">i", buf[offset:offset + 4])[0]
            offset += 4
            args.append(v)
        else:
            raise ValueError(f"unhandled tag {tag} in test")

    return address, args


def run():
    pos_packet = build_osc_message("/lota/camera/position", "fff", [0.125, -1.5, 3.0])
    addr, args = parse_osc_message(pos_packet)
    assert len(pos_packet) % 4 == 0
    assert addr == "/lota/camera/position"
    assert args == [0.125, -1.5, 3.0], args
    print("PASS: /lota/camera/position ->", addr, args)

    rot_packet = build_osc_message("/lota/camera/rotation", "ffff", [0.0, 0.7071068, 0.0, 0.7071068])
    addr2, args2 = parse_osc_message(rot_packet)
    assert addr2 == "/lota/camera/rotation" and len(args2) == 4
    print("PASS: /lota/camera/rotation ->", addr2, args2)

    mode_packet = build_osc_message("/lota/mode", "i", [2])
    addr3, args3 = parse_osc_message(mode_packet)
    assert addr3 == "/lota/mode" and args3 == [2]
    print("PASS: /lota/mode ->", addr3, args3)

    edge_packet = build_osc_message("/lota/camera/position", "fff", [-0.0001, 0.0, 12.3456])
    addr4, args4 = parse_osc_message(edge_packet)
    assert abs(args4[0] - (-0.0001)) < 1e-6
    assert args4[1] == 0.0
    assert abs(args4[2] - 12.3456) < 1e-4
    print("PASS: edge float values ->", addr4, args4)

    try:
        parse_osc_message(pos_packet[:6])
        raise AssertionError("truncated packet should have raised")
    except (ValueError, struct.error, IndexError):
        print("PASS: truncated packet correctly rejected")

    print("\nAll OSC wire-format checks passed.")


if __name__ == "__main__":
    run()
