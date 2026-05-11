import socket
import threading
import time

GTA_TELEM_PORT = 55000
GTA_CMD_PORT   = 55001
GTA_IP         = "127.0.0.1"

state = {
    "x": 0.0, "y": 0.0, "z": 0.0,
    "vx": 0.0, "vy": 0.0, "vz": 0.0,
    "spawn_z": None,
}

def send(msg: bytes):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.sendto(msg, (GTA_IP, GTA_CMD_PORT))
    s.close()

def telemetry_thread():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("0.0.0.0", GTA_TELEM_PORT))
    print(f"[TELEM] Listening on UDP {GTA_TELEM_PORT}")
    while True:
        try:
            data, _ = sock.recvfrom(1024)
            parts = data.decode().strip().split(",")
            if len(parts) == 6:
                state["x"]  = float(parts[0])
                state["y"]  = float(parts[1])
                state["z"]  = float(parts[2])
                state["vx"] = float(parts[3])
                state["vy"] = float(parts[4])
                state["vz"] = float(parts[5])
                if state["spawn_z"] is None:
                    state["spawn_z"] = state["z"]
        except:
            pass

HELP = """
Commands:
  spawn              — spawn Frogger next to player
  takeoff            — climb to 80m above spawn
  land               — return to spawn position
  hover              — hold current position
  goto <x> <y> <z>  — fly to world coordinates
  pos                — print current position
  speed <n>          — set flight speed (default 30)
  q                  — quit
"""

def command_loop():
    global SPEED
    SPEED = 30.0
    time.sleep(1)
    print(HELP)

    while True:
        try:
            raw = input("> ").strip().lower()
        except EOFError:
            break

        if not raw:
            continue

        parts = raw.split()
        cmd   = parts[0]

        if cmd == "q":
            break
        elif cmd == "spawn":
            state["spawn_z"] = None
            send(b"SPAWN")
            print("  → SPAWN")
        elif cmd == "takeoff":
            send(b"TAKEOFF")
            print("  → TAKEOFF")
        elif cmd == "land":
            send(b"LAND")
            print("  → LAND")
        elif cmd == "hover":
            send(b"HOVER")
            print("  → HOVER")
        elif cmd == "goto":
            if len(parts) == 4:
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                    send(f"GOTO,{x},{y},{z},{SPEED}".encode())
                    print(f"  → GOTO ({x}, {y}, {z})")
                except ValueError:
                    print("  [ERR] Usage: goto <x> <y> <z>")
            else:
                print("  [ERR] Usage: goto <x> <y> <z>")
        elif cmd == "pos":
            print(f"  POS({state['x']:.2f}, {state['y']:.2f}, {state['z']:.2f})"
                  f"  VEL({state['vx']:.2f}, {state['vy']:.2f}, {state['vz']:.2f})")
        elif cmd == "speed":
            if len(parts) == 2:
                try:
                    SPEED = float(parts[1])
                    send(f"SPEED,{SPEED}".encode())
                    print(f"  Speed set to {SPEED}")
                except ValueError:
                    print("  [ERR] Bad number")
            else:
                print(f"  Current speed: {SPEED}")
        else:
            print("  [ERR] Unknown command")
            print(HELP)

if __name__ == "__main__":
    threading.Thread(target=telemetry_thread, daemon=True).start()
    command_loop()