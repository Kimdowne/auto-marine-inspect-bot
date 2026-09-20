#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import socket
import time
import serial


# ============================================================
# TCP 설정
# ============================================================

HOST = "0.0.0.0"
PORT = 9000

EXPECTED_HZ = 20.0


# ============================================================
# Nucleo Serial 설정
# ============================================================

SERIAL_PORT = "/dev/ttyACM0"
SERIAL_BAUD = 115200
SERIAL_TIMEOUT = 0.02


# ============================================================
# 모터 설정
# ============================================================

# 실제 시험 결과:
# 약 500/1000부터 안정적으로 기동 가능
MIN_DRIVE = 500

# 정상 운용 출력
# 500은 최소 기동점이므로 약간의 여유를 둠
DRIVE_LIMIT = 650

# 모터 방향이 반대로 되어 있으면
# 해당 값만 -1로 변경
LEFT_POLARITY = 1
RIGHT_POLARITY = 1


# ============================================================
# JSON 명령 검사
# ============================================================

def validate_command(msg):
    """
    Expected JSON:

    {
        "seq": 1,
        "ts": 1234567890123,
        "drive": [0, 0],
        "servo": [0, 0, 0]
    }
    """

    if not isinstance(msg, dict):
        raise ValueError(
            "JSON root must be an object"
        )

    if "seq" not in msg:
        raise ValueError(
            "missing 'seq'"
        )

    if "ts" not in msg:
        raise ValueError(
            "missing 'ts'"
        )

    if "drive" not in msg:
        raise ValueError(
            "missing 'drive'"
        )

    if "servo" not in msg:
        raise ValueError(
            "missing 'servo'"
        )

    seq = msg["seq"]
    ts = msg["ts"]
    drive = msg["drive"]
    servo = msg["servo"]

    if not isinstance(seq, int):
        raise ValueError(
            "'seq' must be int"
        )

    if not isinstance(ts, int):
        raise ValueError(
            "'ts' must be int"
        )

    if not isinstance(drive, list) or len(drive) != 2:
        raise ValueError(
            "'drive' must be [linear, turn]"
        )

    if not isinstance(servo, list) or len(servo) != 3:
        raise ValueError(
            "'servo' must contain 3 values"
        )

    linear = drive[0]
    turn = drive[1]

    if linear not in (-1, 0, 1):
        raise ValueError(
            "drive[0] must be -1, 0, or 1"
        )

    if turn not in (-1, 0, 1):
        raise ValueError(
            "drive[1] must be -1, 0, or 1"
        )

    for index, value in enumerate(servo):
        if value not in (0, 1):
            raise ValueError(
                f"servo[{index}] must be 0 or 1"
            )

    return seq, ts, drive, servo


# ============================================================
# 최소 모터 기동 출력 적용
# ============================================================

def apply_motor_output(value):
    """
    value 범위:
        -1.0 ~ +1.0

    0이면 완전 정지.

    0이 아닌 경우:
        최소 MIN_DRIVE 이상으로 출력.

    현재 WASD 입력은 사실상
    -1 / 0 / +1 이므로
    대부분 DRIVE_LIMIT 값으로 동작한다.

    향후 Unity에서 analog 입력을 사용할 경우에도
    그대로 사용할 수 있도록 구성.
    """

    if abs(value) < 0.0001:
        return 0

    magnitude = abs(value)

    # 0~1 입력을
    # MIN_DRIVE~DRIVE_LIMIT로 매핑
    output = MIN_DRIVE + (
        magnitude
        * (DRIVE_LIMIT - MIN_DRIVE)
    )

    output = round(output)

    if value < 0:
        output = -output

    return output


# ============================================================
# drive → Left / Right 궤도 명령 변환
# ============================================================

def mix_drive(drive):
    """
    drive = [linear, turn]

    W : [ 1,  0]
    S : [-1,  0]
    A : [ 0,  1]
    D : [ 0, -1]

    W+A : [1, 1]
    W+D : [1,-1]
    """

    linear = float(drive[0])
    turn = float(drive[1])

    # Differential drive mixing
    left = linear - turn
    right = linear + turn

    # 범위가 1을 초과할 경우 정규화
    scale = max(
        1.0,
        abs(left),
        abs(right)
    )

    left /= scale
    right /= scale

    # 최소 기동 출력 적용
    left_cmd = apply_motor_output(left)
    right_cmd = apply_motor_output(right)

    # 실제 모터 설치 방향 보정
    left_cmd *= LEFT_POLARITY
    right_cmd *= RIGHT_POLARITY

    return left_cmd, right_cmd


# ============================================================
# Nucleo Serial 열기
# ============================================================

def open_nucleo():
    print(
        f"[SERIAL] Opening "
        f"{SERIAL_PORT} @ {SERIAL_BAUD}"
    )

    ser = serial.Serial(
        SERIAL_PORT,
        SERIAL_BAUD,
        timeout=SERIAL_TIMEOUT
    )

    time.sleep(0.3)

    ser.reset_input_buffer()
    ser.reset_output_buffer()

    print(
        "[SERIAL] Nucleo connected"
    )

    return ser


# ============================================================
# Nucleo STOP
# ============================================================

def send_nucleo_stop(ser):
    try:
        ser.write(
            b"STOP\n"
        )

        ser.flush()

        reply = (
            ser.readline()
            .decode(
                "ascii",
                errors="replace"
            )
            .strip()
        )

        print(
            f"[NUCLEO STOP] "
            f"{reply if reply else 'NO ACK'}"
        )

    except Exception as e:
        print(
            f"[NUCLEO STOP ERROR] "
            f"{type(e).__name__}: {e}"
        )


# ============================================================
# Nucleo 주행 명령 송신
# ============================================================

def send_nucleo_drive(
    ser,
    seq,
    left,
    right
):
    """
    Jetson -> Nucleo protocol:

    M,<seq>,<left>,<right>\\n

    Example:

    M,100,650,650
    """

    command = (
        f"M,{seq},{left},{right}\n"
    )

    try:
        ser.write(
            command.encode("ascii")
        )

        ser.flush()

        reply = (
            ser.readline()
            .decode(
                "ascii",
                errors="replace"
            )
            .strip()
        )

    except Exception as e:
        print(
            f"[SERIAL ERROR] "
            f"{type(e).__name__}: {e}"
        )

        return False, None

    expected_ack = (
        f"ACK,{seq}"
    )

    if reply == expected_ack:
        return True, reply

    print(
        f"[NUCLEO WARNING] "
        f"TX={command.strip()} "
        f"RX={reply if reply else 'TIMEOUT'}"
    )

    return False, reply


# ============================================================
# Windows TCP Client 처리
# ============================================================

def run_client(
    conn,
    address,
    ser
):
    print()

    print(
        "=" * 70
    )

    print(
        f"[CONNECTED] "
        f"Windows host: {address}"
    )

    print(
        "=" * 70
    )

    conn.setsockopt(
        socket.IPPROTO_TCP,
        socket.TCP_NODELAY,
        1
    )

    buffer = b""

    last_seq = None
    last_rx_monotonic = None

    total_packets = 0
    missing_packets = 0
    bad_packets = 0

    stat_start = (
        time.monotonic()
    )

    stat_packets = 0

    try:
        while True:
            data = conn.recv(
                4096
            )

            if not data:
                print()

                print(
                    "[DISCONNECTED] "
                    "Windows host closed connection"
                )

                # 네트워크 연결 종료 시
                # 즉시 정지
                send_nucleo_stop(
                    ser
                )

                return

            buffer += data

            # TCP packet과 JSON 메시지 경계는
            # 일치하지 않으므로 newline 기준 분리
            while b"\n" in buffer:

                line, buffer = (
                    buffer.split(
                        b"\n",
                        1
                    )
                )

                if not line:
                    continue

                rx_mono = (
                    time.monotonic()
                )

                # ========================================
                # JSON Decode / Validation
                # ========================================

                try:
                    text = (
                        line.decode(
                            "utf-8"
                        )
                    )

                    msg = (
                        json.loads(
                            text
                        )
                    )

                    (
                        seq,
                        ts,
                        drive,
                        servo
                    ) = validate_command(
                        msg
                    )

                except Exception as e:
                    bad_packets += 1

                    print(
                        f"[INVALID] "
                        f"{type(e).__name__}: "
                        f"{e}"
                    )

                    error_reply = {
                        "ok": 0,
                        "error": str(e)
                    }

                    conn.sendall(
                        (
                            json.dumps(
                                error_reply,
                                separators=(
                                    ",",
                                    ":"
                                )
                            )
                            + "\n"
                        ).encode(
                            "utf-8"
                        )
                    )

                    continue

                # ========================================
                # 수신 주기 계산
                # ========================================

                if last_rx_monotonic is None:
                    dt_ms = 0.0
                    instant_hz = 0.0

                else:
                    dt_s = (
                        rx_mono
                        - last_rx_monotonic
                    )

                    dt_ms = (
                        dt_s * 1000.0
                    )

                    if dt_s > 0:
                        instant_hz = (
                            1.0 / dt_s
                        )
                    else:
                        instant_hz = 0.0

                last_rx_monotonic = (
                    rx_mono
                )

                # ========================================
                # Sequence 검사
                # ========================================

                gap = 0

                if last_seq is not None:
                    expected_seq = (
                        last_seq + 1
                    )

                    if seq > expected_seq:

                        gap = (
                            seq
                            - expected_seq
                        )

                        missing_packets += gap

                    elif seq <= last_seq:
                        print(
                            "[SEQ WARNING] "
                            f"previous="
                            f"{last_seq}, "
                            f"current="
                            f"{seq}"
                        )

                last_seq = seq

                total_packets += 1
                stat_packets += 1

                # ========================================
                # Drive Mixing
                # ========================================

                (
                    left_cmd,
                    right_cmd
                ) = mix_drive(
                    drive
                )

                # ========================================
                # Servo 명령은 현재 무시
                # ========================================

                if any(
                    servo
                ):
                    print(
                        "[ARM IGNORED] "
                        f"servo={servo}"
                    )

                # ========================================
                # Nucleo로 motor 명령 전달
                # ========================================

                (
                    nucleo_ok,
                    nucleo_reply
                ) = send_nucleo_drive(
                    ser,
                    seq,
                    left_cmd,
                    right_cmd
                )

                # ========================================
                # Jetson console log
                # ========================================

                print(
                    f"[RX] "
                    f"seq={seq:06d} "
                    f"dt={dt_ms:6.1f}ms "
                    f"hz={instant_hz:5.1f} "
                    f"gap={gap} "
                    f"drive={drive} "
                    f"servo={servo} "
                    f"motor=("
                    f"{left_cmd},"
                    f"{right_cmd}) "
                    f"nucleo="
                    f"{'OK' if nucleo_ok else 'FAIL'}"
                )

                # ========================================
                # Windows로 ACK 반환
                # ========================================

                ack = {
                    "ack": seq,
                    "drive": drive,
                    "servo": servo,
                    "left": left_cmd,
                    "right": right_cmd,
                    "nucleo": (
                        1
                        if nucleo_ok
                        else 0
                    )
                }

                ack_data = (
                    json.dumps(
                        ack,
                        separators=(
                            ",",
                            ":"
                        )
                    )
                    + "\n"
                ).encode(
                    "utf-8"
                )

                conn.sendall(
                    ack_data
                )

                # ========================================
                # 통계 출력
                # ========================================

                now = (
                    time.monotonic()
                )

                stat_elapsed = (
                    now
                    - stat_start
                )

                if stat_elapsed >= 1.0:

                    measured_hz = (
                        stat_packets
                        / stat_elapsed
                    )

                    print(
                        f"[STATS] "
                        f"rx_rate="
                        f"{measured_hz:.2f}Hz "
                        f"total="
                        f"{total_packets} "
                        f"missing="
                        f"{missing_packets} "
                        f"invalid="
                        f"{bad_packets}"
                    )

                    stat_start = now
                    stat_packets = 0

    except ConnectionResetError:

        print(
            "[DISCONNECTED] "
            "Connection reset "
            "by Windows host"
        )

        send_nucleo_stop(
            ser
        )

    except Exception:

        # 예외 발생 시에도
        # motor를 먼저 정지
        send_nucleo_stop(
            ser
        )

        raise


# ============================================================
# Main
# ============================================================

def main():

    print(
        "=" * 70
    )

    print(
        "JetBot TCP -> "
        "Nucleo Motor Bridge"
    )

    print(
        "=" * 70
    )

    # --------------------------------------------------------
    # Nucleo 연결
    # --------------------------------------------------------

    try:
        ser = open_nucleo()

    except Exception as e:

        print(
            "[FATAL] "
            "Cannot open Nucleo: "
            f"{type(e).__name__}: "
            f"{e}"
        )

        print()

        print(
            f"Check: "
            f"{SERIAL_PORT}"
        )

        return

    # 시작 시 무조건 STOP
    send_nucleo_stop(
        ser
    )

    # --------------------------------------------------------
    # TCP Server
    # --------------------------------------------------------

    server = socket.socket(
        socket.AF_INET,
        socket.SOCK_STREAM
    )

    server.setsockopt(
        socket.SOL_SOCKET,
        socket.SO_REUSEADDR,
        1
    )

    server.bind(
        (
            HOST,
            PORT
        )
    )

    server.listen(
        1
    )

    print()

    print(
        f"[TCP] Listening: "
        f"{HOST}:{PORT}"
    )

    print(
        f"[TCP] Expected rate: "
        f"{EXPECTED_HZ:.1f} Hz"
    )

    print(
        f"[MOTOR] "
        f"Minimum drive: "
        f"{MIN_DRIVE}/1000"
    )

    print(
        f"[MOTOR] "
        f"Operating drive: "
        f"{DRIVE_LIMIT}/1000"
    )

    print()

    print(
        "[TCP] Waiting for "
        "Windows host..."
    )

    try:
        while True:

            (
                conn,
                address
            ) = server.accept()

            try:
                run_client(
                    conn,
                    address,
                    ser
                )

            except Exception as e:

                print(
                    f"[CLIENT ERROR] "
                    f"{type(e).__name__}: "
                    f"{e}"
                )

            finally:

                # TCP client가 종료되면
                # 반드시 정지
                try:
                    send_nucleo_stop(
                        ser
                    )

                except Exception:
                    pass

                conn.close()

                print()

                print(
                    "[TCP] Waiting for "
                    "Windows host..."
                )

    except KeyboardInterrupt:

        print()

        print(
            "[SYSTEM] "
            "Ctrl+C detected"
        )

    finally:

        # 프로그램 종료 전
        # 반드시 STOP
        try:
            send_nucleo_stop(
                ser
            )

        except Exception:
            pass

        server.close()
        ser.close()

        print(
            "[SYSTEM] "
            "Shutdown complete"
        )


if __name__ == "__main__":
    main()