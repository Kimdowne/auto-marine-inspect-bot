import serial
import time

s = serial.Serial("/dev/ttyACM0", 115200, timeout=0.1)

for i in range(40):
    s.write(f"M,{i},300,0\n".encode())
    print(s.readline().decode().strip())
    time.sleep(0.05)

s.write(b"STOP\n")#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import serial
import time

PORT = "/dev/ttyACM0"
BAUD = 115200

# 시험할 PWM 값
LEVELS = [200, 300, 400, 500, 600]

# 각 단계 시험 시간
TEST_SECONDS = 1.5

# Nucleo watchdog 300 ms보다 충분히 빠르게 전송
PERIOD = 0.05      # 20 Hz


def send_command(ser, seq, left, right):
    cmd = f"M,{seq},{left},{right}\n"

    ser.write(cmd.encode("ascii"))
    ser.flush()

    reply = ser.readline().decode(
        "ascii",
        errors="replace"
    ).strip()

    expected = f"ACK,{seq}"

    if reply != expected:
        print(
            f"[WARN] TX={cmd.strip()} "
            f"RX={reply if reply else 'NO ACK'}"
        )

    return reply


def run_motor(ser, seq, left, right, seconds):
    end_time = time.monotonic() + seconds

    while time.monotonic() < end_time:
        reply = send_command(
            ser,
            seq,
            left,
            right
        )

        print(
            f"seq={seq:04d} "
            f"L={left:4d} "
            f"R={right:4d} "
            f"ACK={reply}"
        )

        seq += 1
        time.sleep(PERIOD)

    return seq


def stop(ser):
    ser.write(b"STOP\n")
    ser.flush()

    reply = ser.readline().decode(
        "ascii",
        errors="replace"
    ).strip()

    print(f"[STOP] {reply}")


def main():
    ser = serial.Serial(
        PORT,
        BAUD,
        timeout=0.03
    )

    time.sleep(0.2)
    ser.reset_input_buffer()

    seq = 1

    try:
        stop(ser)

        # ---------------------------------------------
        # LEFT MOTOR FORWARD
        # ---------------------------------------------

        print("\n=== LEFT MOTOR FORWARD ===")

        for power in LEVELS:
            print(f"\n--- PWM {power}/1000 ---")

            seq = run_motor(
                ser,
                seq,
                power,
                0,
                TEST_SECONDS
            )

            stop(ser)
            time.sleep(0.5)

        # ---------------------------------------------
        # RIGHT MOTOR FORWARD
        # ---------------------------------------------

        print("\n=== RIGHT MOTOR FORWARD ===")

        for power in LEVELS:
            print(f"\n--- PWM {power}/1000 ---")

            seq = run_motor(
                ser,
                seq,
                0,
                power,
                TEST_SECONDS
            )

            stop(ser)
            time.sleep(0.5)

        # ---------------------------------------------
        # BOTH FORWARD
        # ---------------------------------------------

        print("\n=== BOTH MOTORS ===")

        seq = run_motor(
            ser,
            seq,
            500,
            500,
            2.0
        )

        stop(ser)

        # ---------------------------------------------
        # BOTH REVERSE
        # ---------------------------------------------

        time.sleep(1)

        seq = run_motor(
            ser,
            seq,
            -500,
            -500,
            2.0
        )

        stop(ser)

        print("\n=== TEST FINISHED ===")

    except KeyboardInterrupt:
        print("\n[CTRL+C]")

    finally:
        try:
            stop(ser)
        except Exception:
            pass

        ser.close()


if __name__ == "__main__":
    main()