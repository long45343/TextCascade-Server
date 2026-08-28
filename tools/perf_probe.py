#!/usr/bin/env python3
"""TextCascade performance probe.

Stdlib-only asyncio WebSocket (WSS) client used to measure the deployed
textcascade-server: idle connection holds, clip latency, and slow-consumer
isolation. See perf.md at the repository root for the scenario definitions.

Subcommands:
  hold     open N connections, answer pings, hold for S seconds
  latency  2 connections (sender A + receiver B), record ack_rtt/broadcast_lag
  slow     baseline window, then a stalled receiver, then measure A's p95
"""
import argparse
import asyncio
import base64
import json
import os
import ssl
import time
import urllib.request

HOST = "127.0.0.1"
PORT = 8443


def percentile(values, p):
    if not values:
        return None
    vs = sorted(values)
    k = min(len(vs) - 1, max(0, int(round(len(vs) * p)) - 1))
    return round(vs[k], 3)


def login(user, password):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    req = urllib.request.Request(
        f"https://{HOST}:{PORT}/api/v1/login",
        data=json.dumps({"username": user, "password": password}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, context=ctx, timeout=15) as resp:
        return json.loads(resp.read())["token"]


class WS:
    def __init__(self, reader, writer):
        self.reader = reader
        self.writer = writer

    async def send_text(self, text):
        data = text.encode()
        mask = os.urandom(4)
        n = len(data)
        if n < 126:
            header = bytes([0x81, 0x80 | n])
        elif n < 65536:
            header = bytes([0x81, 0x80 | 126]) + n.to_bytes(2, "big")
        else:
            header = bytes([0x81, 0x80 | 127]) + n.to_bytes(8, "big")
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        self.writer.write(header + mask + masked)
        await self.writer.drain()

    async def read_frame(self):
        b1, b2 = await self.reader.readexactly(2)
        opcode = b1 & 0x0F
        masked = b2 & 0x80
        ln = b2 & 0x7F
        if ln == 126:
            ln = int.from_bytes(await self.reader.readexactly(2), "big")
        elif ln == 127:
            ln = int.from_bytes(await self.reader.readexactly(8), "big")
        mask_key = await self.reader.readexactly(4) if masked else None
        payload = await self.reader.readexactly(ln) if ln else b""
        if mask_key:
            payload = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))
        if opcode == 0x8:
            raise ConnectionResetError("close frame")
        return opcode, payload


async def connect(token, client_id):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    reader, writer = await asyncio.open_connection(
        HOST, PORT, ssl=ctx, limit=2 ** 22)
    key = base64.b64encode(os.urandom(16)).decode()
    request = (
        f"GET /api/v1/sync HTTP/1.1\r\nHost: {HOST}:{PORT}\r\n"
        "Upgrade: websocket\r\nConnection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n"
        "Sec-WebSocket-Protocol: textcascade.v1\r\n"
        f"Authorization: Bearer {token}\r\n\r\n")
    writer.write(request.encode())
    await writer.drain()
    response = await reader.readuntil(b"\r\n\r\n")
    status_line = response.split(b"\r\n", 1)[0]
    if b"101" not in status_line:
        raise RuntimeError(f"handshake failed: {status_line!r}")
    return WS(reader, writer)


async def hello(ws, client_id):
    await ws.send_text(json.dumps({
        "type": "hello", "clientId": client_id, "clientName": "perf",
        "lastServerVersion": 0, "snapshot": None}))
    while True:
        opcode, payload = await ws.read_frame()
        if opcode == 1 and b'"welcome"' in payload:
            return


def is_ping(payload):
    return b'"type":"ping"' in payload


def pong_text():
    return json.dumps({
        "type": "pong",
        "clientTimeUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})


async def cmd_hold(args):
    token = login(args.user, args.password)
    connections = []
    errors = 0
    pongs = 0
    semaphore = asyncio.Semaphore(64)

    async def one(index):
        nonlocal errors
        async with semaphore:
            try:
                ws = await connect(token, f"hold-{index}")
                await hello(ws, f"hold-{index}")
                connections.append(ws)
            except Exception:
                errors += 1

    await asyncio.gather(*(one(i) for i in range(args.count)))

    async def keepalive(ws):
        nonlocal pongs
        try:
            while True:
                opcode, payload = await ws.read_frame()
                if opcode == 1 and is_ping(payload):
                    await ws.send_text(pong_text())
                    pongs += 1
        except Exception:
            pass

    tasks = [asyncio.create_task(keepalive(ws)) for ws in connections]
    await asyncio.sleep(args.seconds)

    for ws in connections:
        try:
            ws.writer.close()
        except Exception:
            pass
    for task in tasks:
        task.cancel()
    print(json.dumps({
        "opened": len(connections), "errors": errors,
        "expected_pongs_floor": len(connections) * max(0, int(args.seconds / 30) - 1),
        "pongs_sent": pongs}))


async def cmd_latency(args):
    token = login(args.user, args.password)

    ws_b = await connect(token, "lat-b")
    await hello(ws_b, "lat-b")
    ws_a = await connect(token, "lat-a")
    await hello(ws_a, "lat-a")

    send_ts = {}
    acks = []
    lags = []
    pings = {"n": 0}

    async def reader_a():
        while True:
            opcode, payload = await ws_a.read_frame()
            if opcode != 1:
                continue
            if is_ping(payload):
                await ws_a.send_text(pong_text())
                pings["n"] += 1
                continue
            if b'"clip_ack"' in payload:
                clip_id = json.loads(payload)["id"]
                start = send_ts.get(clip_id)
                if start is not None:
                    acks.append((time.monotonic_ns() - start) / 1e6)

    async def reader_b():
        while True:
            opcode, payload = await ws_b.read_frame()
            if opcode != 1:
                continue
            if is_ping(payload):
                await ws_b.send_text(pong_text())
                continue
            if b'"type":"clip"' in payload:
                message = json.loads(payload)
                start = send_ts.get(message["id"])
                if start is not None:
                    lags.append((time.monotonic_ns() - start) / 1e6)

    tasks = [asyncio.create_task(reader_a()), asyncio.create_task(reader_b())]

    payload_text = "x" * args.size

    async def send_wave(tag, count, interval, record):
        for i in range(count):
            clip_id = f"{tag}-{i}"
            start = time.monotonic_ns()
            if record:
                send_ts[clip_id] = start
            await ws_a.send_text(json.dumps({
                "type": "clip", "id": clip_id, "payload": payload_text,
                "encrypted": False, "hash": "h"}))
            if interval:
                await asyncio.sleep(interval)

    await send_wave("warmup", args.warmup, 0.005, record=False)
    await asyncio.sleep(0.5)
    acks.clear()
    lags.clear()
    await send_wave("m", args.count, args.interval, record=True)

    deadline = time.monotonic() + 20
    while len(acks) < args.count and time.monotonic() < deadline:
        await asyncio.sleep(0.05)

    for task in tasks:
        task.cancel()
    print(json.dumps({
        "size_bytes": args.size,
        "samples_expected": args.count,
        "ack_samples": len(acks),
        "lag_samples": len(lags),
        "ack_rtt_ms": {"p50": percentile(acks, 0.50), "p95": percentile(acks, 0.95),
                        "p99": percentile(acks, 0.99), "max": percentile(acks, 1.0)},
        "broadcast_lag_ms": {"p50": percentile(lags, 0.50), "p95": percentile(lags, 0.95),
                              "p99": percentile(lags, 0.99), "max": percentile(lags, 1.0)}}))


async def cmd_slow(args):
    token = login(args.user, args.password)

    ws_a = await connect(token, "slow-a")
    await hello(ws_a, "slow-a")

    send_ts = {}
    acks = {"baseline": [], "stall": []}

    async def reader_a():
        while True:
            opcode, payload = await ws_a.read_frame()
            if opcode != 1:
                continue
            if is_ping(payload):
                await ws_a.send_text(pong_text())
                continue
            if b'"clip_ack"' in payload:
                clip_id = json.loads(payload)["id"]
                start = send_ts.get(clip_id)
                if start is not None:
                    phase = "baseline" if clip_id.startswith("b-") else "stall"
                    acks[phase].append((time.monotonic_ns() - start) / 1e6)

    reader_task = asyncio.create_task(reader_a())
    payload_text = "x" * args.size

    async def send_wave(tag, seconds):
        count = int(seconds / args.interval)
        for i in range(count):
            clip_id = f"{tag}-{i}"
            send_ts[clip_id] = time.monotonic_ns()
            await ws_a.send_text(json.dumps({
                "type": "clip", "id": clip_id, "payload": payload_text,
                "encrypted": False, "hash": "h"}))
            await asyncio.sleep(args.interval)
        return count

    baseline_count = await send_wave("b", args.baseline)

    async def drain(minimum, timeout):
        deadline = time.monotonic() + timeout
        while len(acks["baseline"]) < minimum and time.monotonic() < deadline:
            await asyncio.sleep(0.05)

    await drain(baseline_count, 10)

    # B joins, completes hello, then never reads again (stalled consumer).
    ws_b = await connect(token, "slow-b")
    await hello(ws_b, "slow-b")

    stall_count = await send_wave("s", args.stall)
    deadline = time.monotonic() + 20
    while len(acks["stall"]) < stall_count and time.monotonic() < deadline:
        await asyncio.sleep(0.05)

    reader_task.cancel()

    b_state = "unknown"
    try:
        opcode, _ = await asyncio.wait_for(ws_b.read_frame(), timeout=3)
        b_state = f"received-frame-op-{opcode}"
    except ConnectionResetError:
        b_state = "connection-reset"
    except asyncio.TimeoutError:
        b_state = "still-open-no-data"
    except Exception as exc:
        b_state = f"error:{type(exc).__name__}"

    print(json.dumps({
        "clip_bytes": args.size,
        "baseline": {"count": len(acks["baseline"]),
                      "p50": percentile(acks["baseline"], 0.50),
                      "p95": percentile(acks["baseline"], 0.95)},
        "stall": {"count": len(acks["stall"]),
                   "p50": percentile(acks["stall"], 0.50),
                   "p95": percentile(acks["stall"], 0.95)},
        "b_state": b_state}))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--user", required=True)
    parser.add_argument("--password", required=True)
    sub = parser.add_subparsers(dest="command", required=True)

    hold = sub.add_parser("hold")
    hold.add_argument("--count", type=int, required=True)
    hold.add_argument("--seconds", type=float, required=True)
    hold.set_defaults(func=cmd_hold)

    latency = sub.add_parser("latency")
    latency.add_argument("--size", type=int, required=True)
    latency.add_argument("--count", type=int, required=True)
    latency.add_argument("--interval", type=float, required=True)
    latency.add_argument("--warmup", type=int, default=50)
    latency.set_defaults(func=cmd_latency)

    slow = sub.add_parser("slow")
    slow.add_argument("--size", type=int, default=32768)
    slow.add_argument("--interval", type=float, default=0.02)
    slow.add_argument("--baseline", type=float, default=5)
    slow.add_argument("--stall", type=float, default=20)
    slow.set_defaults(func=cmd_slow)

    args = parser.parse_args()
    asyncio.run(args.func(args))


if __name__ == "__main__":
    main()
