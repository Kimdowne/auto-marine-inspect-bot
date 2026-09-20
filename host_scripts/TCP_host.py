import json
import socket
import threading
import time

from pynput import keyboard


# ============================================================
# 설정
# ============================================================

JETSON_IP = "192.168.1.111"
JETSON_PORT = 9000

SEND_HZ = 20.0
SEND_PERIOD = 1.0 / SEND_HZ


# ============================================================
# 공유 상태
# ============================================================

pressed_keys = set()
key_lock = threading.Lock()

stop_event = threading.Event()

sent_lock = threading.Lock()

# seq -> (송신 monotonic 시각, drive, servo)
sent_packets = {}

last_display_state = None


# ============================================================
# 키보드 callback
# ============================================================

def key_to_char(key):
    try:
        if key.char is None:
            return None

        return key.char.lower()

    except AttributeError:
        return None


def on_press(key):
    # ESC를 누르면 전체 프로그램 종료
    if key == keyboard.Key.esc:
        stop_event.set()

        return False

    ch = key_to_char(key)

    if ch in {
        "w", "a", "s", "d",
        "1", "2", "3"
    }:
        with key_lock:
            pressed_keys.add(ch)


def on_release(key):
    ch = key_to_char(key)

    if ch in {
        "w", "a", "s", "d",
        "1", "2", "3"
    }:
        with key_lock:
            pressed_keys.discard(ch)


# ============================================================
# 현재 키 상태 → robot command
# ============================================================

def get_control_state():
    with key_lock:
        keys = set(pressed_keys)

    # W = +1, S = -1
    linear = (
        int("w" in keys)
        - int("s" in keys)
    )

    # A = +1, D = -1
    turn = (
        int("a" in keys)
        - int("d" in keys)
    )

    servo = [
        int("1" in keys),
        int("2" in keys),
        int("3" in keys)
    ]

    drive = [
        linear,
        turn
    ]

    return drive, servo


# ============================================================
# ACK receiver thread
# ============================================================

def ack_receiver(sock):
    buffer = b""

    while not stop_event.is_set():
        try:
            data = sock.recv(4096)

            if not data:
                print()
                print("[TCP] Jetson disconnected")

                stop_event.set()

                return

            buffer += data

            while b"\n" in buffer:
                line, buffer = buffer.split(
                    b"\n",
                    1
                )

                if not line:
                    continue

                try:
                    ack_msg = json.loads(
                        line.decode("utf-8")
                    )

                except Exception as e:
                    print(
                        f"[ACK ERROR] Invalid JSON: {e}"
                    )

                    continue

                if "ack" not in ack_msg:
                    print(
                        f"[ACK ERROR] "
                        f"Missing ack field: {ack_msg}"
                    )

                    continue

                ack_seq = ack_msg["ack"]

                rx_time = time.perf_counter()

                with sent_lock:
                    sent_info = sent_packets.pop(
                        ack_seq,
                        None
                    )

                if sent_info is None:
                    print(
                        f"[ACK] seq={ack_seq} "
                        f"unknown/late"
                    )

                    continue

                (
                    tx_time,
                    expected_drive,
                    expected_servo
                ) = sent_info

                rtt_ms = (
                    rx_time - tx_time
                ) * 1000.0

                received_drive = ack_msg.get(
                    "drive"
                )

                received_servo = ack_msg.get(
                    "servo"
                )

                verified = (
                    received_drive == expected_drive
                    and
                    received_servo == expected_servo
                )

                result = (
                    "PASS"
                    if verified
                    else "FAIL"
                )

                # 매 packet을 보고 싶다면 그대로 유지.
                # 20Hz라 초당 20줄 출력됨.
                print(
                    f"[ACK] "
                    f"seq={ack_seq:06d} "
                    f"RTT={rtt_ms:6.2f}ms "
                    f"VERIFY={result}"
                )

        except socket.timeout:
            continue

        except OSError:
            if not stop_event.is_set():
                print("[ACK] Socket closed")

            return

        except Exception as e:
            print(
                f"[ACK ERROR] "
                f"{type(e).__name__}: {e}"
            )

            stop_event.set()

            return


# ============================================================
# main
# ============================================================

def main():
    global last_display_state

    print("=" * 70)
    print("JetBot Windows Control Test")
    print("=" * 70)
    print()
    print("W : Forward")
    print("S : Reverse")
    print("A : Turn Left")
    print("D : Turn Right")
    print()
    print("1 : Servo 1 command")
    print("2 : Servo 2 command")
    print("3 : Servo 3 command")
    print()
    print("ESC : Exit")
    print()
    print(f"Target Jetson: {JETSON_IP}:{JETSON_PORT}")
    print(f"Command rate: {SEND_HZ:.1f} Hz")
    print()

    # --------------------------------------------------------
    # TCP 연결
    # --------------------------------------------------------

    sock = socket.socket(
        socket.AF_INET,
        socket.SOCK_STREAM
    )

    # 작은 제어 packet 지연 최소화
    sock.setsockopt(
        socket.IPPROTO_TCP,
        socket.TCP_NODELAY,
        1
    )

    sock.settimeout(1.0)

    print("[TCP] Connecting to Jetson...")

    try:
        sock.connect(
            (JETSON_IP, JETSON_PORT)
        )

    except Exception as e:
        print(
            f"[TCP] Connection failed: "
            f"{type(e).__name__}: {e}"
        )

        sock.close()

        return

    print("[TCP] Connected")
    print()

    # --------------------------------------------------------
    # ACK thread 시작
    # --------------------------------------------------------

    ack_thread = threading.Thread(
        target=ack_receiver,
        args=(sock,),
        daemon=True
    )

    ack_thread.start()

    # --------------------------------------------------------
    # Keyboard listener 시작
    # --------------------------------------------------------

    listener = keyboard.Listener(
        on_press=on_press,
        on_release=on_release
    )

    listener.start()

    print("[CONTROL] Ready")
    print("[CONTROL] Click this console and use WASD / 1 / 2 / 3")
    print()

    # --------------------------------------------------------
    # 20Hz command loop
    # --------------------------------------------------------

    seq = 0

    next_send = time.perf_counter()

    try:
        while not stop_event.is_set():
            now = time.perf_counter()

            # 다음 50ms deadline까지 기다림
            wait_time = next_send - now

            if wait_time > 0:
                stop_event.wait(wait_time)

                if stop_event.is_set():
                    break

            # 현재 keyboard state 읽기
            drive, servo = get_control_state()

            # seq 증가
            seq += 1

            command = {
                "seq": seq,
                "ts": (
                    time.time_ns()
                    // 1_000_000
                ),
                "drive": drive,
                "servo": servo
            }

            # compact JSON
            message = (
                json.dumps(
                    command,
                    separators=(",", ":")
                )
                + "\n"
            )

            tx_time = time.perf_counter()

            with sent_lock:
                sent_packets[seq] = (
                    tx_time,
                    list(drive),
                    list(servo)
                )

                # 혹시 ACK가 장기간 오지 않아도
                # dictionary가 끝없이 커지지 않도록 제한
                if len(sent_packets) > 200:
                    oldest_seq = min(
                        sent_packets
                    )

                    sent_packets.pop(
                        oldest_seq,
                        None
                    )

            try:
                sock.sendall(
                    message.encode("utf-8")
                )

            except Exception as e:
                print(
                    f"[TCP SEND ERROR] "
                    f"{type(e).__name__}: {e}"
                )

                stop_event.set()

                break

            # 입력 상태가 변한 경우에만 별도 표시
            display_state = (
                tuple(drive),
                tuple(servo)
            )

            if display_state != last_display_state:
                print(
                    f"[CONTROL] "
                    f"drive={drive} "
                    f"servo={servo}"
                )

                last_display_state = (
                    display_state
                )

            # 다음 정확한 deadline
            next_send += SEND_PERIOD

            # PC가 아주 오래 멈췄다가 돌아온 경우
            # 밀린 packet을 몰아서 보내지 않음
            now_after = time.perf_counter()

            if (
                now_after - next_send
                > SEND_PERIOD
            ):
                next_send = (
                    now_after
                    + SEND_PERIOD
                )

    except KeyboardInterrupt:
        stop_event.set()

    finally:
        print()
        print("[SYSTEM] Closing...")

        stop_event.set()

        try:
            sock.shutdown(
                socket.SHUT_RDWR
            )
        except OSError:
            pass

        sock.close()

        try:
            listener.stop()
        except Exception:
            pass

        print("[SYSTEM] Finished")


if __name__ == "__main__":
    main()