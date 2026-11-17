#!/usr/bin/env python3
"""Tiny Modbus TCP client for testing the Stationeers PLC mod. Standard library only.

  python modbus_test.py list                      show the first points of every table
  python modbus_test.py di|coil|ir|hr ADDR [N]    read N values (default 1)
  python modbus_test.py setcoil ADDR 0|1          write a coil
  python modbus_test.py sethr ADDR VALUE          write a holding register (raw 16-bit)
Options: --host 127.0.0.1 --port 5020
"""
import argparse, socket, struct, sys

FC = {"coil": 1, "di": 2, "hr": 3, "ir": 4}


def call(sock, tid, pdu):
    sock.sendall(struct.pack(">HHHB", tid, 0, len(pdu) + 1, 1) + pdu)
    hdr = b""
    while len(hdr) < 7:
        chunk = sock.recv(7 - len(hdr))
        if not chunk:
            raise ConnectionError("server closed the connection")
        hdr += chunk
    n = struct.unpack(">H", hdr[4:6])[0] - 1
    body = b""
    while len(body) < n:
        body += sock.recv(n - len(body))
    if body[0] & 0x80:
        raise RuntimeError("Modbus exception %d (1=function, 2=address, 3=value)" % body[1])
    return body


def read(sock, table, addr, n):
    body = call(sock, 1, struct.pack(">BHH", FC[table], addr, n))
    if table in ("coil", "di"):
        return [(body[2 + i // 8] >> (i % 8)) & 1 for i in range(n)]
    return list(struct.unpack(">%dh" % n, body[2:2 + 2 * n]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd")
    ap.add_argument("args", nargs="*")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5020)
    a = ap.parse_args()

    s = socket.create_connection((a.host, a.port), timeout=3)
    if a.cmd == "list":
        for t in ("di", "coil", "ir", "hr"):
            try:
                print(t.upper(), read(s, t, 0, 20))
            except RuntimeError as e:
                print(t.upper(), e)
    elif a.cmd in FC:
        n = int(a.args[1]) if len(a.args) > 1 else 1
        print(read(s, a.cmd, int(a.args[0]), n))
    elif a.cmd == "setcoil":
        call(s, 2, struct.pack(">BHH", 5, int(a.args[0]), 0xFF00 if int(a.args[1]) else 0))
        print("ok")
    elif a.cmd == "sethr":
        call(s, 2, struct.pack(">BHh", 6, int(a.args[0]), int(a.args[1])))
        print("ok")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
