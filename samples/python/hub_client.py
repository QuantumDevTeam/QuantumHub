"""A QuantumHub client with no .NET and no dependencies, in one file.

It exists to prove the protocol belongs to no language: this is what a VS Code or
Rider plugin does, only there a protobuf library replaces the few helpers below.

    python hub_client.py [path to endpoint.json] [text to submit]
"""
import json
import os
import struct
import sys


def varint(value):
    out = bytearray()
    while value > 0x7F:
        out.append(value & 0x7F | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def field(number, value):
    """Encodes a protobuf field: an int as a varint, str/bytes as length-delimited."""
    if isinstance(value, int):
        return varint(number << 3) + varint(value)
    data = value.encode() if isinstance(value, str) else value
    return varint(number << 3 | 2) + varint(len(data)) + data


def parse(data):
    """Decodes a protobuf message into {field number: [values]}."""
    fields, i = {}, 0

    def read_varint():
        nonlocal i
        result = shift = 0
        while True:
            byte = data[i]
            i += 1
            result |= (byte & 0x7F) << shift
            shift += 7
            if byte < 0x80:
                return result

    while i < len(data):
        tag = read_varint()
        number, wire = tag >> 3, tag & 7
        if wire == 0:
            value = read_varint()
        elif wire == 2:
            size = read_varint()
            value = data[i:i + size]
            i += size
        else:
            raise ValueError(f"wire type {wire}")
        fields.setdefault(number, []).append(value)
    return fields


class Hub:
    REQUEST = 1

    def __init__(self, pipe_path, client_id):
        self.pipe = open(pipe_path, "r+b", buffering=0)
        self.client_id = client_id
        self.next_id = 0

        dependency = field(1, "qh.sample.uuid") + field(3, ">=1.0.0")
        hello = (field(1, 1) + field(2, client_id) + field(3, "Python sample") + field(4, "1.0.0")
                 + field(5, "plugin") + field(6, "stream") + field(7, dependency) + field(9, os.getpid()))
        response = self.exchange("hub", "hub.hello", hello)
        self.session = parse(response)[2][0].decode()

    def exchange(self, target, method, payload, content_type="application/x-protobuf"):
        self.next_id += 1
        envelope = (field(1, self.REQUEST) + field(2, self.next_id) + field(3, self.client_id)
                    + field(4, target) + field(5, method) + field(6, content_type) + field(7, payload))
        self.pipe.write(struct.pack("<I", len(envelope)) + envelope)

        size = struct.unpack("<I", self.read(4))[0]
        response = parse(self.read(size))
        if response.get(8, [0])[0] != 0:
            raise RuntimeError(response[9][0].decode())
        return response.get(7, [b""])[0]

    def read(self, count):
        data = b""
        while len(data) < count:
            chunk = self.pipe.read(count - len(data))
            if not chunk:
                raise EOFError("The hub closed the connection.")
            data += chunk
        return data


def main():
    default = os.path.join(os.environ["APPDATA"], "QuantumHub", "endpoint.json")
    path = sys.argv[1] if len(sys.argv) > 1 else default
    text = sys.argv[2] if len(sys.argv) > 2 else "hello from Python"

    with open(path, encoding="utf-8") as file:
        endpoint = json.load(file)

    hub = Hub(endpoint["pipePath"], "qh.sample.python")
    print("connected, session", hub.session[:8])

    body = json.dumps({"kind": "text", "value": text}).encode()
    print("backend said:", hub.exchange("qh.sample.uuid", "value.submit", body, "application/json").decode())


if __name__ == "__main__":
    main()
