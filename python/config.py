"""
CONFIG — All pipeline settings in one place
===========================================
Only the settings actually used by the real-time focus cycle:
Muse 2 -> preprocessing -> fft_features -> ThresholdClassifier -> score.
"""

# =============================================================
# EEG SIGNAL SETTINGS
# =============================================================

SAMPLE_RATE = 256                              # Muse 2 native sampling rate (Hz)
CHANNEL_NAMES = ['TP9', 'AF7', 'AF8', 'TP10']  # Muse 2 electrode order

# Band names. Populated at runtime by fft_features.extract_features_all_channels
# (set to ['theta', 'alpha', 'beta']) so the classifier's index math lines up.
# Leave this empty here — do not hardcode.
BANDS = []


# =============================================================
# PREPROCESSING SETTINGS
# =============================================================

LOW_FREQ = 0.5          # Bandpass high-pass at 0.5 Hz (removes slow drift)
HIGH_FREQ = 45.0        # Bandpass low-pass at 45 Hz (removes muscle noise)
NOTCH_FREQ = 50         # Power-line notch (50 Hz in Australia; 60 for US)
FILTER_ORDER = 4

# Artifact rejection: reject any window where a channel exceeds this (uV).
# Raised to 1000 for live Muse 2 testing (loose contact spikes above 100 uV).
# Lower back toward 100 once you have clean, well-seated contact.
ARTIFACT_THRESHOLD = 1000.0


# =============================================================
# WINDOWING SETTINGS
# =============================================================

WINDOW_SECONDS = 2      # Analysis window size (seconds)
STEP_SECONDS = 0.5      # How often a new reading is computed (seconds)
