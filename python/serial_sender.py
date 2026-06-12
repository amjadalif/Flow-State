"""
Send Focus Scores to ESP32 via USB Serial
==========================================

Usage:
    import serial_sender

    sender = serial_sender.SerialSender()   # connect (run once)

    sender.send_score(72.5)                 # send a focus score (each window)

    sender.send_command("M1W")              # Motor 1 wind
    sender.send_command("M1U")              # Motor 1 unwind
    sender.send_command("M2W")              # Motor 2 wind
    sender.send_command("M2U")              # Motor 2 unwind
    sender.send_command("MSTOP")            # stop all motors
    sender.send_command("SHOME")            # mark home position

    sender.close()                          # when done

Finding your port (macOS):
    ls /dev/cu.usb*
    -> e.g. /dev/cu.usbserial-0001 or /dev/cu.SLAB_USBtoUART

Install:
    pip install pyserial
"""

import serial
import time


class SerialSender:
    def __init__(self, port=None, baud_rate=115200):
        """
        Connect to the ESP32 via USB Serial.

        Parameters:
            port:      Serial port path. If None, tries to auto-detect.
            baud_rate: Must match the ESP32 firmware (default 115200)
        """
        if port is None:
            port = self._find_port()

        print(f"Connecting to ESP32 on {port}...")
        self.ser = serial.Serial(port, baud_rate, timeout=1)

        # Wait for the ESP32 to reset after the serial connection opens
        time.sleep(2)

        # Drain any startup messages from the ESP32
        while self.ser.in_waiting:
            line = self.ser.readline().decode('utf-8', errors='ignore').strip()
            if line:
                print(f"  ESP32: {line}")

        print("Connected to ESP32!")

    def send_score(self, score):
        """
        Send a focus score (0-100) to the ESP32, which maps it to motor action.

        Parameters:
            score: float, 0-100
        """
        message = f"{score:.1f}\n"
        self.ser.write(message.encode('utf-8'))

        # Read confirmation from the ESP32
        time.sleep(0.05)
        if self.ser.in_waiting:
            response = self.ser.readline().decode('utf-8', errors='ignore').strip()
            if response:
                print(f"  ESP32: {response}")

    def send_command(self, cmd):
        """Send a manual command string (e.g. 'M1W', 'MSTOP', 'SHOME') to the ESP32."""
        message = f"{cmd}\n"
        self.ser.write(message.encode('utf-8'))

        # Read confirmation — drain all queued lines
        time.sleep(0.3)
        while self.ser.in_waiting:
            response = self.ser.readline().decode('utf-8', errors='ignore').strip()
            if response:
                print(f"  ESP32: {response}")

    def close(self):
        """Close the serial connection."""
        if self.ser and self.ser.is_open:
            self.ser.close()
            print("Serial connection closed.")

    def _find_port(self):
        """Try to auto-detect the ESP32 serial port on macOS."""
        import glob

        patterns = [
            '/dev/cu.usbserial-*',
            '/dev/cu.SLAB_USBtoUART*',
            '/dev/cu.usbmodem*',
            '/dev/cu.wchusbserial*',
        ]

        ports = []
        for pattern in patterns:
            ports.extend(glob.glob(pattern))

        if not ports:
            raise RuntimeError(
                "No USB serial port found!\n"
                "1. Make sure the ESP32 is plugged in via USB\n"
                "2. Run 'ls /dev/cu.usb*' in terminal to find the port\n"
                "3. Pass it manually: SerialSender(port='/dev/cu.usbserial-XXXX')"
            )

        if len(ports) > 1:
            print(f"  Multiple ports found: {ports}")
            print(f"  Using: {ports[0]}")

        return ports[0]
