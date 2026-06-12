"""
Send EEG Data + Focus Scores to Unity via UDP
===============================================

Usage:
    import unity_sender

    # Create sender (run once)
    unity = unity_sender.UnitySender()

    # Send EEG wave data (call every update tick, ~50ms)
    unity.send_eeg(eeg_data_dict)

    # Send focus score during session (call every 0.5s)
    unity.send_score(72.5, "FOCUSED")

    # Send final score when session ends (call once)
    unity.send_session_complete(72.5, "FOCUSED")

    # When done
    unity.close()

The Unity C# script (DashboardReceiver.cs) listens on the same port
and parses the JSON packets.

Install:
    No extra packages needed — uses Python's built-in socket module.
"""

import socket
import json


class UnitySender:
    def __init__(self, ip="127.0.0.1", port=5005, gameflow_port=5006):
        """
        Create a UDP sender that talks to Unity.

        Parameters:
            ip:             Unity's IP address (127.0.0.1 for same machine)
            port:           Must match the port in DashboardReceiver.cs (default 5005)
            gameflow_port:  TCP port for GameFlowManager messages (default 5006)
        """
        self.ip = ip
        self.port = port
        self.gameflow_port = gameflow_port
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        print(f"Unity sender ready → {ip}:{port}")
        print(f"GameFlowManager TCP → {ip}:{gameflow_port}")

    def send_eeg(self, channel_data):
        packet = {
            "type": "eeg",
            "channels": channel_data
        }
        self._send(packet)
        try:
            data = json.dumps(packet).encode('utf-8')
            self.sock.sendto(data, (self.ip, 5009))
        except Exception:
            pass

    def send_score(self, score, state="NEUTRAL"):
        """
        Send the focus score to Unity during the session.
        Called every 0.5s when a new score is computed.

        Parameters:
            score: float, 0-100
            state: "DISTRACTED", "NEUTRAL", or "FOCUSED"
        """
        packet = {
            "type": "score",
            "score": round(float(score), 1),
            "state": state
        }
        self._send(packet)

    def send_session_complete(self, score, state="NEUTRAL", recovery_times=None,
                             focused_readings=0, distracted_readings=0,
                             all_scores=None, focus_drops=0,
                             all_states=None, drop_times=None):
        """
        Send the FINAL focus score when the session ends.

        Parameters:
            score:               float, 0-100
            state:               "DISTRACTED", "NEUTRAL", or "FOCUSED"
            recovery_times:      list of floats — seconds for each recovery
            focused_readings:    int — number of FOCUSED readings
            distracted_readings: int — number of DISTRACTED readings
            all_scores:          list of floats — every score from the session
            focus_drops:         int — number of FOCUSED → non-FOCUSED transitions
            all_states:          list of (elapsed_s, state) tuples — every reading
            drop_times:          list of floats — seconds when drops occurred
        """
        if recovery_times is None:
            recovery_times = []
        if all_scores is None:
            all_scores = []
        if all_states is None:
            all_states = []
        if drop_times is None:
            drop_times = []

        # Compute average recovery time
        avg_recovery = 0.0
        if len(recovery_times) > 0:
            avg_recovery = sum(recovery_times) / len(recovery_times)

        # Compute focus/distracted percentages (ignoring NEUTRAL)
        total_fd = focused_readings + distracted_readings
        focused_pct = 0.0
        distracted_pct = 0.0
        if total_fd > 0:
            focused_pct = round((focused_readings / total_fd) * 100, 1)
            distracted_pct = round((distracted_readings / total_fd) * 100, 1)

        # Compute focus consistency from drops (0-100)
        focus_consistency = round(max(0, 100 - (focus_drops * 10)), 1)

        # ── Build 30 timeline bars (each = 2 seconds of a 60s session) ──
        NUM_BARS = 30
        SESSION_DURATION = 60
        BAR_SECONDS = SESSION_DURATION / NUM_BARS  # 2 seconds per bar

        timeline_bars = []
        for i in range(NUM_BARS):
            bar_start = i * BAR_SECONDS
            bar_end = bar_start + BAR_SECONDS

            states_in_bar = [
                s for (t, s) in all_states
                if bar_start <= t < bar_end
            ]

            if not states_in_bar:
                timeline_bars.append("none")
            else:
                has_distracted = "DISTRACTED" in states_in_bar
                focused_count = states_in_bar.count("FOCUSED")

                if has_distracted:
                    timeline_bars.append("red")
                elif focused_count > 0:
                    timeline_bars.append("green")
                else:
                    timeline_bars.append("red")

        # Fill "none" bars: first look forward, then look backward
        # Forward fill: copy from next non-none bar
        for i in range(len(timeline_bars)):
            if timeline_bars[i] == "none":
                for j in range(i + 1, len(timeline_bars)):
                    if timeline_bars[j] != "none":
                        timeline_bars[i] = timeline_bars[j]
                        break

        # Backward fill: any remaining "none" at the end, copy from previous bar
        for i in range(len(timeline_bars)):
            if timeline_bars[i] == "none" and i > 0:
                timeline_bars[i] = timeline_bars[i - 1]

        # Backward fill any remaining "none" bars at the end
        for i in range(len(timeline_bars) - 1, -1, -1):
            if timeline_bars[i] == "none":
                # Look backward for the previous non-none bar
                for j in range(i - 1, -1, -1):
                    if timeline_bars[j] != "none":
                        timeline_bars[i] = timeline_bars[j]
                        break

        # Format drop times as readable string
        drop_times_str = ""
        if drop_times:
            formatted = [f"{int(t)}s" for t in drop_times]
            if len(formatted) == 1:
                drop_times_str = formatted[0]
            elif len(formatted) == 2:
                drop_times_str = f"{formatted[0]} and {formatted[1]}"
            else:
                drop_times_str = ", ".join(formatted[:-1]) + f" and {formatted[-1]}"

        packet = {
            "type": "session_complete",
            "score": round(float(score), 1),
            "state": state,
            "avg_recovery_time": round(avg_recovery, 1),
            "recovery_count": len(recovery_times),
            "focused_pct": focused_pct,
            "distracted_pct": distracted_pct,
            "focus_consistency": focus_consistency,
            "timeline_bars": timeline_bars,
            "drop_times_str": drop_times_str
        }
        self._send(packet)
        print(f"  → Sent session_complete to Unity: {score:.1f} ({state}), "
              f"avg recovery: {avg_recovery:.1f}s, "
              f"focused: {focused_pct}% distracted: {distracted_pct}% "
              f"consistency: {focus_consistency}/100 "
              f"drops at: {drop_times_str}")

    # ── TCP messages for GameFlowManager ──

    def send_focus_tcp(self, score):
        """
        Send focus score to GameFlowManager via TCP.
        Format: "FOCUS:<score>" — matches HandlePythonMessage().
        Called every 0.5s during the session alongside send_score().
        """
        self._send_tcp(f"FOCUS:{score:.1f}")

    def send_calibration_value(self, value):
        """
        Send a calibration reading to GameFlowManager via TCP.
        Format: "CALIB:<value>" — matches HandlePythonMessage().
        Called during calibration so Unity's timer can track progress.
        """
        self._send_tcp(f"CALIB:{value:.4f}")

    def send_eeg_trigger(self):
        """
        Tell GameFlowManager to move from Idle to NameEntry.
        Format: "EEG_TRIGGER" — matches HandlePythonMessage().
        Call this when Muse 2 is connected and streaming.
        """
        self._send_tcp("EEG_TRIGGER")

    def _send_tcp(self, message):
        """
        Send a short TCP message to GameFlowManager.
        Opens a new connection each time (matches ListenLoop() which
        accepts one client, reads one message, then closes).
        """
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(0.5)
            s.connect((self.ip, self.gameflow_port))
            s.sendall(message.encode('utf-8'))
            s.close()
        except Exception:
            # Don't crash if Unity isn't running or port isn't ready
            pass

    # ── Internal UDP send ──

    def _send(self, packet):
        """Serialize and send a packet via UDP."""
        try:
            data = json.dumps(packet).encode('utf-8')
            self.sock.sendto(data, (self.ip, self.port))
        except Exception as e:
            # Don't crash the pipeline if Unity isn't running
            pass

    def close(self):
        """Close the UDP socket."""
        if self.sock:
            self.sock.close()
            print("Unity sender closed.")
