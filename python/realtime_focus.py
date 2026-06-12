"""
Real-Time Focus Detection with Muse 2 -> ESP32 + Unity
======================================================
This script:
  1. Connects to Muse 2 via BrainFlow
  2. Records a 10-second baseline (sit still, relax)
  3. Calibrates the ThresholdClassifier
  4. Shows a menu:
     [1] Manually set the motors
     [2] Start Session (real-time EEG focus loop)
     [3] Recalibrate baseline
     [q] Quit

  Manual Motor Mode:
     1 -> Wind Motor 1     2 -> Unwind Motor 1
     3 -> Wind Motor 2     4 -> Unwind Motor 2
     SPACE -> Save position & return to menu

  Session Mode:
     Live EEG scoring every 0.5s -> sent to ESP32 and Unity
     Press x -> Stop session & return to menu

Unity mode (launched with --unity): no menu, only listens for Unity commands.

Before running:
  1. Flash esp32_servo_focus.ino to the ESP32
  2. Close the Arduino IDE Serial Monitor
  3. Turn on the Muse 2 (don't pair it via macOS Bluetooth)
  4. Run this script
"""

import time
import sys
import tty
import termios
import threading
import socket
import config
import preprocessing
import compute_baseline
import serial_sender
import unity_sender
from brainflow.board_shim import BoardShim, BrainFlowInputParams, BoardIds


# ════════════════════════════════════════════════════════════════
#  UNITY COMMAND LISTENER (background thread)
# ════════════════════════════════════════════════════════════════

class UnityCommandListener:
    """
    Listens on a UDP port for commands from Unity's GameFlowManager.
    Runs in a background thread so it doesn't block the menu.

    Commands Unity can send:
        START_CALIBRATION:<PlayerName>  — begin calibration for this player
        START_SESSION                    — begin the focus session
        M1W/M1U/M2W/M2U/MSTOP/SHOME/SRETURN — forwarded to the ESP32
    """

    def __init__(self, port=5007):
        self.port = port
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("0.0.0.0", port))
        self.sock.settimeout(0.5)  # so the thread can check _running

        self._running = False
        self._thread = None

        # Pending commands (read by the main loop)
        self._lock = threading.Lock()
        self._pending_command = None
        self._player_name = ""

        # ESP32 serial sender — set by main() after creation
        self._serial_sender = None

    def start(self):
        """Start listening in a background thread."""
        self._running = True
        self._thread = threading.Thread(target=self._listen_loop, daemon=True)
        self._thread.start()
        print(f"Unity command listener started on UDP port {self.port}")

    def stop(self):
        """Stop the listener."""
        self._running = False
        if self._thread:
            self._thread.join(timeout=2)
        self.sock.close()

    def get_pending_command(self):
        """
        Check if Unity sent a command. Returns (command, player_name) or (None, "").
        Call this from the main loop.
        """
        with self._lock:
            cmd = self._pending_command
            name = self._player_name
            self._pending_command = None
            self._player_name = ""
            return cmd, name

    def _listen_loop(self):
        while self._running:
            try:
                data, addr = self.sock.recvfrom(1024)
                message = data.decode('utf-8').strip()
                print(f"\n  [Unity->Python] Received: {message}")

                if message.startswith("START_CALIBRATION:"):
                    player_name = message.split(":", 1)[1]
                    with self._lock:
                        self._pending_command = "START_CALIBRATION"
                        self._player_name = player_name

                elif message == "START_SESSION":
                    with self._lock:
                        self._pending_command = "START_SESSION"

                # Motor commands from Unity — forward directly to the ESP32
                elif message in ("M1W", "M1U", "M2W", "M2U", "MSTOP", "SHOME", "SRETURN"):
                    if self._serial_sender:
                        self._serial_sender.send_command(message)
                        print(f"  [Motor] Forwarded {message} to ESP32")
                    else:
                        print(f"  [Motor] ESP32 not connected, ignoring {message}")

            except socket.timeout:
                continue
            except Exception as e:
                if self._running:
                    print(f"  [Unity listener] Error: {e}")
                break


# ════════════════════════════════════════════════════════════════
#  KEYBOARD HELPERS (macOS/Linux — reads single keypress)
# ════════════════════════════════════════════════════════════════

def get_key():
    """
    Wait for a single keypress and return it (raw terminal mode).
    Returns an empty string when no terminal is available (e.g. from Unity).
    """
    try:
        fd = sys.stdin.fileno()
        old_settings = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            ch = sys.stdin.read(1)
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)
        return ch
    except (termios.error, OSError, ValueError):
        return ''


def check_key():
    """
    Non-blocking key check. Returns the key if one is pressed, else None.
    Returns None silently when no terminal is available (e.g. from Unity).
    """
    try:
        import select
        fd = sys.stdin.fileno()
        old_settings = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            ready, _, _ = select.select([sys.stdin], [], [], 0)
            if ready:
                return sys.stdin.read(1)
            return None
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)
    except (termios.error, OSError, ValueError):
        return None


# ════════════════════════════════════════════════════════════════
#  MANUAL MOTOR CONTROL
# ════════════════════════════════════════════════════════════════

def manual_motor_mode(sender):
    """
    Let the user manually control motors with keypresses.

    1 -> Wind Motor 1     2 -> Unwind Motor 1
    3 -> Wind Motor 2     4 -> Unwind Motor 2
    SPACE -> Save position & go back to menu
    """
    print("\n" + "=" * 60)
    print("MANUAL MOTOR CONTROL")
    print("=" * 60)
    print()
    print("  [1] Wind Motor 1")
    print("  [2] Unwind Motor 1")
    print("  [3] Wind Motor 2")
    print("  [4] Unwind Motor 2")
    print("  [SPACE] Save position & return to menu")
    print()

    if not sender:
        print("ERROR: ESP32 not connected. Cannot control motors.")
        print("Press any key to return to menu...")
        get_key()
        return

    while True:
        key = get_key()

        if key == '1':
            sender.send_command("M1W")
            print("  -> Motor 1: WINDING...")

        elif key == '2':
            sender.send_command("M1U")
            print("  -> Motor 1: UNWINDING...")

        elif key == '3':
            sender.send_command("M2W")
            print("  -> Motor 2: WINDING...")

        elif key == '4':
            sender.send_command("M2U")
            print("  -> Motor 2: UNWINDING...")

        elif key == ' ':
            # Stop motors and save position
            sender.send_command("MSTOP")
            print("\n  Motors stopped. Position saved.")
            print("  Returning to menu...")
            time.sleep(0.5)
            return

        # Ctrl+C in raw mode arrives as '\x03'
        elif key == '\x03':
            sender.send_command("MSTOP")
            print("\n  Motors stopped.")
            return


# ════════════════════════════════════════════════════════════════
#  SESSION MODE (real-time loop — press 'x' to quit)
# ════════════════════════════════════════════════════════════════

def run_session(board, classifier, sender, unity, eeg_channels, window_samples):
    """
    The real-time EEG focus session. Press 'x' to stop and return to the menu.
    """
    print("\n" + "=" * 60)
    print("LIVE FOCUS DETECTION — Press 'x' to stop")
    print("=" * 60)
    print()

    # Clear any old data from the BrainFlow buffer before starting
    board.get_board_data()

    # Mark motor home position at session start
    if sender:
        sender.send_command("SHOME")

    # Session limits
    SESSION_MAX_SECONDS = 81    # auto-end after this many seconds
    FOCUS_MAX_COUNT = 60        # auto-end after this many focused readings
    session_start = time.time()
    focus_count = 0
    last_score = 0.0
    last_state = "NEUTRAL"

    # Recovery-time tracking
    prev_state = "NEUTRAL"
    distracted_start = None      # timestamp when non-focused began
    recovery_times = []          # recovery durations (seconds)

    # Breakdown tracking (NEUTRAL counts as not-focused)
    focused_readings = 0
    distracted_readings = 0

    # Focus consistency tracking
    focus_drops = 0              # FOCUSED -> non-FOCUSED transitions
    all_scores = []

    # Timeline tracking (for the 30-bar timeline)
    all_states = []              # list of (elapsed_seconds, state)
    drop_times = []              # seconds when focus drops occurred

    while True:
        # ── Session time limit ──
        elapsed = time.time() - session_start
        if elapsed >= SESSION_MAX_SECONDS:
            if sender:
                sender.send_command("MSTOP")
            if unity:
                unity.send_session_complete(last_score, last_state, recovery_times,
                                            focused_readings, distracted_readings,
                                            all_scores, focus_drops, all_states, drop_times)
            print(f"\n\n  ⏱ Session time limit reached ({SESSION_MAX_SECONDS}s).")
            print("  Returning to menu...")
            time.sleep(1)
            return

        # ── Wait one step interval, polling for 'x' every 50ms and ──
        # ── streaming EEG waves to Unity each tick ──
        quit_requested = False
        wait_start = time.time()
        while (time.time() - wait_start) < config.STEP_SECONDS:
            key = check_key()
            if key in ('x', 'X', '\x03'):
                quit_requested = True
                break

            if unity:
                raw_peek = board.get_current_board_data(50)
                if raw_peek.shape[1] > 0:
                    eeg_dict = {}
                    for idx, ch in enumerate(eeg_channels):
                        if idx < len(config.CHANNEL_NAMES):
                            eeg_dict[config.CHANNEL_NAMES[idx]] = raw_peek[ch].tolist()
                    unity.send_eeg(eeg_dict)

            time.sleep(0.05)

        if quit_requested:
            if sender:
                sender.send_command("MSTOP")
            if unity:
                unity.send_session_complete(last_score, last_state, recovery_times,
                                            focused_readings, distracted_readings,
                                            all_scores, focus_drops, all_states, drop_times)
            print("\n\nSession stopped. Returning to menu...")
            time.sleep(0.5)
            return

        # ── Grab the latest window (peek — doesn't clear the buffer) ──
        raw_data = board.get_current_board_data(window_samples)
        if raw_data.shape[1] < window_samples:
            print("  Waiting for enough data...")
            continue

        eeg_window = raw_data[eeg_channels, :]
        clean_window = preprocessing.preprocess_all_channels(eeg_window)

        if not preprocessing.check_artifacts(clean_window):
            print("  ⚡ Artifact detected, skipping...")
            continue

        # ── Score this window ──
        score = classifier.compute_focus_score(clean_window, BOARD_ID)
        last_score = score
        all_scores.append(score)

        if sender:
            sender.send_score(score)

        bar_len = int(score / 5)  # 0-20 characters
        bar = "█" * bar_len + "░" * (20 - bar_len)

        if score <= 33:
            state = "DISTRACTED"
            state_display = "🔴 DISTRACTED"
        elif score <= 66:
            state = "NEUTRAL"
            state_display = "🟡 NEUTRAL"
        else:
            state = "FOCUSED"
            state_display = "🟢 FOCUSED"
            focus_count += 1

        last_state = state

        # Count focused vs not-focused (NEUTRAL counts as not-focused)
        if state == "FOCUSED":
            focused_readings += 1
        else:
            distracted_readings += 1

        # Track focus drops (FOCUSED -> NEUTRAL/DISTRACTED)
        if prev_state == "FOCUSED" and state != "FOCUSED":
            focus_drops += 1
            drop_time = time.time() - session_start
            drop_times.append(round(drop_time, 1))
            print(f"  📉 Focus drop #{focus_drops} at {drop_time:.1f}s")
        prev_state = state

        # Record state for the timeline
        all_states.append((time.time() - session_start, state))

        # Track recovery times
        if state != "FOCUSED" and distracted_start is None:
            distracted_start = time.time()
        if state == "FOCUSED" and distracted_start is not None:
            recovery = time.time() - distracted_start
            recovery_times.append(recovery)
            print(f"  ⏱ Recovery time: {recovery:.1f}s")
            distracted_start = None

        # Send focus score to Unity
        if unity:
            unity.send_score(score, state)     # UDP JSON for DashboardReceiver
            unity.send_focus_tcp(score)         # TCP for GameFlowManager

        print(f"  [{bar}] {score:5.1f}/100  {state_display}  "
              f"(focused: {focus_count}/{FOCUS_MAX_COUNT})")

        # ── Focus-count limit ──
        if focus_count >= FOCUS_MAX_COUNT:
            if sender:
                sender.send_command("MSTOP")
            if unity:
                unity.send_session_complete(last_score, last_state, recovery_times,
                                            focused_readings, distracted_readings,
                                            all_scores, focus_drops, all_states, drop_times)
            print(f"\n\n  🎯 Focus goal reached! ({FOCUS_MAX_COUNT} focused readings)")
            print("  Returning to menu...")
            time.sleep(1)
            return


# ════════════════════════════════════════════════════════════════
#  CALIBRATION (called from the menu OR from a Unity trigger)
# ════════════════════════════════════════════════════════════════

def run_calibration(board, eeg_channels, unity=None, duration=10):
    """
    Record baseline EEG and calibrate the ThresholdClassifier.

    Returns:
        ThresholdClassifier if successful, None if failed.
    """
    BOARD_ID = BoardIds.MUSE_2_BOARD.value

    print(f"\n── Recording {duration}s baseline ──")
    print("Sit still and relax... don't focus on anything.")

    # Clear any old data
    board.get_board_data()

    # Send periodic CALIB pings to Unity so its timer stays in sync
    elapsed = 0
    while elapsed < duration:
        time.sleep(1)
        elapsed += 1
        print(f"  Calibrating... {elapsed}/{duration}s")
        if unity:
            unity.send_calibration_value(float(elapsed) / duration)

    # Grab baseline data
    raw_baseline = board.get_board_data()
    eeg_baseline = raw_baseline[eeg_channels, :]
    print(f"Baseline data: {eeg_baseline.shape[1]} samples")
    print(f"Data range: [{eeg_baseline.min():.2f}, {eeg_baseline.max():.2f}]")

    # Preprocess baseline into windows
    baseline_windows = preprocessing.preprocess_and_segment(eeg_baseline, board_id=BOARD_ID)
    print(f"Baseline windows: {len(baseline_windows)}")

    if not baseline_windows:
        print("ERROR: No clean baseline windows!")
        print("Check headband fit and try again.")
        return None

    # Calibrate
    classifier = compute_baseline.ThresholdClassifier()
    classifier.calibrate(baseline_windows, BOARD_ID)

    print("\nBaseline & calibration complete!")
    return classifier


# ════════════════════════════════════════════════════════════════
#  MAIN (interactive: setup + menu loop + Unity listener)
# ════════════════════════════════════════════════════════════════

# Global so run_session can access it (set in main / main_unity_mode)
BOARD_ID = None


def main():
    global BOARD_ID

    # ── 1. Setup ──
    BoardShim.enable_dev_board_logger()

    params = BrainFlowInputParams()
    BOARD_ID = BoardIds.MUSE_2_BOARD.value

    sampling_rate = BoardShim.get_sampling_rate(BOARD_ID)
    eeg_channels = BoardShim.get_eeg_channels(BOARD_ID)
    window_samples = int(config.WINDOW_SECONDS * sampling_rate)  # 512

    print("=" * 60)
    print("REAL-TIME FOCUS DETECTION")
    print("=" * 60)
    print(f"Sampling Rate:  {sampling_rate} Hz")
    print(f"Window Size:    {config.WINDOW_SECONDS}s ({window_samples} samples)")
    print(f"Update Every:   {config.STEP_SECONDS}s")
    print()

    # ── 2. Connect to ESP32 ──
    print("── Connecting to ESP32 ──")
    try:
        sender = serial_sender.SerialSender()
    except Exception as e:
        print(f"ESP32 not found: {e}")
        print("Continuing without ESP32 (scores will print to terminal only)")
        sender = None

    # ── 2b. Connect to Unity ──
    print("\n── Connecting to Unity ──")
    try:
        unity = unity_sender.UnitySender()
    except Exception as e:
        print(f"Unity sender failed: {e}")
        print("Continuing without Unity")
        unity = None

    # ── 2c. Start Unity command listener ──
    print("\n── Starting Unity command listener ──")
    unity_listener = UnityCommandListener(port=5007)
    unity_listener._serial_sender = sender  # forward motor commands to ESP32
    unity_listener.start()

    # ── 3. Connect to Muse 2 ──
    print("\n── Connecting to Muse 2 ──")
    board = BoardShim(BOARD_ID, params)
    board.prepare_session()
    print("Connected!")

    board.start_stream(450000)

    # ── 3b. Tell Unity the Muse is connected ──
    if unity:
        unity.send_eeg_trigger()
        print("Sent EEG_TRIGGER to Unity (Muse is connected)")

    # ── 4. Initial calibration so menu option [2] works right away ──
    classifier = run_calibration(board, eeg_channels, unity)
    if classifier is None:
        print("Initial calibration failed. Recalibrate from the menu [3]")
        print("or wait for Unity to trigger calibration.")
        classifier = None

    # ── 5. Menu loop (also checks for Unity commands) ──
    try:
        while True:
            print("\n" + "=" * 60)
            print("MAIN MENU")
            print("=" * 60)
            print()
            print("  [1] Manually set the motors")
            print("  [2] Start Session (real-time EEG focus)")
            print("  [3] Recalibrate baseline")
            print("  [q] Quit")
            print()
            print("  (Also listening for Unity commands...)")
            print()
            print("Press 1, 2, 3, or q: ", end="", flush=True)

            choice = None
            unity_cmd = None
            player_name = ""

            # Poll for either a keypress OR a Unity command. Keep the terminal
            # in raw mode for the whole loop so we don't swallow keypresses.
            import select
            fd = sys.stdin.fileno()
            old_settings = termios.tcgetattr(fd)
            try:
                tty.setraw(fd)
                while choice is None and unity_cmd is None:
                    ready, _, _ = select.select([sys.stdin], [], [], 0.05)
                    if ready:
                        key = sys.stdin.read(1)
                        if key in ('1', '2', '3', 'q', 'Q', '\x03'):
                            choice = key

                    cmd, name = unity_listener.get_pending_command()
                    if cmd is not None:
                        unity_cmd = cmd
                        player_name = name
            finally:
                termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)

            if choice:
                print(choice)  # echo the key now that the terminal is normal

            # ── Handle keyboard choice ──
            if choice == '1':
                manual_motor_mode(sender)

            elif choice == '2':
                if classifier is None:
                    print("\nNo calibration data! Running calibration first...")
                    classifier = run_calibration(board, eeg_channels, unity)
                    if classifier is None:
                        print("Calibration failed. Check headband fit.")
                        continue
                run_session(board, classifier, sender, unity, eeg_channels, window_samples)

            elif choice == '3':
                classifier = run_calibration(board, eeg_channels, unity)

            elif choice in ('q', 'Q', '\x03'):
                print("\nQuitting...")
                break

            # ── Handle Unity command ──
            elif unity_cmd == "START_CALIBRATION":
                print(f"\n  Unity requested calibration for player: {player_name}")
                classifier = run_calibration(board, eeg_channels, unity)
                if classifier is None:
                    print("Calibration failed!")
                else:
                    print(f"Calibration complete for {player_name}.")
                    print("  Waiting for START_SESSION from Unity...")

                    # Unity may have already sent START_SESSION while we were
                    # calibrating, or will shortly. Wait up to 15s for it.
                    wait_start = time.time()
                    got_start = False
                    while (time.time() - wait_start) < 15:
                        cmd, _ = unity_listener.get_pending_command()
                        if cmd == "START_SESSION":
                            print("\n  Unity requested session start!")
                            run_session(board, classifier, sender, unity, eeg_channels, window_samples)
                            got_start = True
                            break
                        time.sleep(0.1)

                    if not got_start:
                        print("  No START_SESSION received. Returning to menu.")

            elif unity_cmd == "START_SESSION":
                print("\n  Unity requested session start!")
                if classifier is None:
                    print("  ERROR: No calibration data! Cannot start session.")
                    print("  Unity should send START_CALIBRATION first.")
                else:
                    run_session(board, classifier, sender, unity, eeg_channels, window_samples)

    except KeyboardInterrupt:
        print("\n\nStopping...")

    # ── 6. Cleanup ──
    if sender:
        sender.send_command("MSTOP")
    unity_listener.stop()
    board.stop_stream()
    board.release_session()
    if sender:
        sender.close()
    if unity:
        unity.close()
    print("Done!")


# ════════════════════════════════════════════════════════════════
#  MAIN (Unity mode: no menu, only listens for Unity commands)
# ════════════════════════════════════════════════════════════════

def main_unity_mode():
    """
    Unity mode: no interactive menu, no terminal input.
    Connects to ESP32, Unity, and Muse 2, then waits for Unity commands only.
    """
    global BOARD_ID

    BoardShim.enable_dev_board_logger()

    params = BrainFlowInputParams()
    BOARD_ID = BoardIds.MUSE_2_BOARD.value

    sampling_rate = BoardShim.get_sampling_rate(BOARD_ID)
    eeg_channels = BoardShim.get_eeg_channels(BOARD_ID)
    window_samples = int(config.WINDOW_SECONDS * sampling_rate)

    print("=" * 60)
    print("REAL-TIME FOCUS DETECTION (Unity Mode)")
    print("=" * 60)
    print(f"Sampling Rate:  {sampling_rate} Hz")
    print(f"Window Size:    {config.WINDOW_SECONDS}s ({window_samples} samples)")
    print(f"Update Every:   {config.STEP_SECONDS}s")
    print()

    # ── Connect to ESP32 ──
    print("── Connecting to ESP32 ──")
    try:
        sender = serial_sender.SerialSender()
    except Exception as e:
        print(f"ESP32 not found: {e}")
        print("Continuing without ESP32")
        sender = None

    # ── Connect to Unity ──
    print("\n── Connecting to Unity ──")
    try:
        unity = unity_sender.UnitySender()
    except Exception as e:
        print(f"Unity sender failed: {e}")
        unity = None

    # ── Start Unity command listener ──
    print("\n── Starting Unity command listener ──")
    unity_listener = UnityCommandListener(port=5007)
    unity_listener._serial_sender = sender
    unity_listener.start()

    # ── Connect to Muse 2 ──
    print("\n── Connecting to Muse 2 ──")
    board = BoardShim(BOARD_ID, params)
    board.prepare_session()
    print("Connected!")

    board.start_stream(450000)

    # ── Tell Unity the Muse is connected ──
    if unity:
        unity.send_eeg_trigger()
        print("Sent EEG_TRIGGER to Unity (Muse is connected)")

    # ── Unity triggers a fresh calibration per player via START_CALIBRATION ──
    classifier = None
    print("Skipping initial calibration — Unity will trigger per-player calibration.")

    # ── Command loop (Unity commands only) ──
    print("\n" + "=" * 60)
    print("Waiting for Unity commands...")
    print("=" * 60)

    try:
        while True:
            cmd, player_name = unity_listener.get_pending_command()

            if cmd == "START_CALIBRATION":
                print(f"\n  Unity requested calibration for player: {player_name}")
                classifier = run_calibration(board, eeg_channels, unity)
                if classifier is None:
                    print("Calibration failed!")
                else:
                    print(f"Calibration complete for {player_name}.")
                    print("  Waiting for START_SESSION from Unity...")

                    wait_start = time.time()
                    got_start = False
                    while (time.time() - wait_start) < 15:
                        cmd2, _ = unity_listener.get_pending_command()
                        if cmd2 == "START_SESSION":
                            print("\n  Unity requested session start!")
                            run_session(board, classifier, sender, unity, eeg_channels, window_samples)
                            got_start = True
                            break
                        time.sleep(0.1)

                    if not got_start:
                        print("  No START_SESSION received. Waiting...")

            elif cmd == "START_SESSION":
                print("\n  Unity requested session start!")
                if classifier is None:
                    print("  ERROR: No calibration data! Cannot start session.")
                else:
                    run_session(board, classifier, sender, unity, eeg_channels, window_samples)

            time.sleep(0.05)  # avoid busy-waiting

    except KeyboardInterrupt:
        print("\n\nStopping...")

    # ── Cleanup ──
    if sender:
        sender.send_command("MSTOP")
    unity_listener.stop()
    board.stop_stream()
    board.release_session()
    if sender:
        sender.close()
    if unity:
        unity.close()
    print("Done!")


if __name__ == "__main__":
    if "--unity" in sys.argv:
        main_unity_mode()
    else:
        main()
