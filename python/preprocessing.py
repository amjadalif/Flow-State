"""
STEP 2: Preprocessing — Filter & Clean the EEG Signal
======================================================

Pipeline per window:
  1. Notch filter at 50 Hz       (power-line hum)
  2. Bandpass filter 0.5-45 Hz   (keep brain waves, drop drift + muscle noise)
  3. Artifact rejection          (drop windows where any channel exceeds the
                                  threshold in config — likely blink/movement)
"""

import numpy as np
from scipy.signal import butter, filtfilt, iirnotch

import config


def bandpass_filter(signal, low_freq=None, high_freq=None,
                    sample_rate=None, order=None):
    """
    Keep only frequencies between low_freq and high_freq.

    Parameters:
        signal:      1D numpy array (one channel of EEG)
        low_freq:    Lower cutoff (default from config)
        high_freq:   Upper cutoff (default from config)
        sample_rate: Samples per second (default from config)
        order:       Filter steepness (default from config)

    Returns:
        Filtered signal (same length)
    """
    if low_freq is None: low_freq = config.LOW_FREQ
    if high_freq is None: high_freq = config.HIGH_FREQ
    if sample_rate is None: sample_rate = config.SAMPLE_RATE
    if order is None: order = config.FILTER_ORDER

    nyquist = sample_rate / 2.0
    low = low_freq / nyquist
    high = high_freq / nyquist

    # Clamp to valid range (must be between 0 and 1 exclusive)
    low = max(low, 0.001)
    high = min(high, 0.999)

    b, a = butter(order, [low, high], btype='band')
    return filtfilt(b, a, signal)


def notch_filter(signal, notch_freq=None, sample_rate=None, quality=30):
    """
    Remove a specific frequency (power-line hum).

    Parameters:
        signal:      1D numpy array
        notch_freq:  Frequency to remove (default from config)
        sample_rate: Samples per second
        quality:     How narrow the notch is (30 = removes ~48-52 Hz)
    """
    if notch_freq is None: notch_freq = config.NOTCH_FREQ
    if sample_rate is None: sample_rate = config.SAMPLE_RATE

    # Only apply if the notch frequency is below Nyquist
    if notch_freq >= sample_rate / 2:
        return signal

    b, a = iirnotch(notch_freq, quality, sample_rate)
    return filtfilt(b, a, signal)


def check_artifacts(window, threshold=None):
    """
    Check whether a window of EEG data is clean.

    Parameters:
        window:    2D array (n_channels, n_samples) — one window of data
        threshold: Maximum acceptable amplitude in uV (default from config)

    Returns:
        True  if the window is CLEAN (no artifacts)
        False if the window should be REJECTED (has artifacts)
    """
    if threshold is None:
        threshold = config.ARTIFACT_THRESHOLD

    max_amplitude = np.max(np.abs(window))
    return max_amplitude < threshold


def preprocess_channel(raw_signal, sample_rate=None):
    """
    Full preprocessing for ONE channel: notch (50 Hz) then bandpass (0.5-45 Hz).

    Parameters:
        raw_signal:  1D numpy array — raw EEG from one electrode
        sample_rate: Samples per second

    Returns:
        Clean signal, ready for feature extraction
    """
    if sample_rate is None:
        sample_rate = config.SAMPLE_RATE

    after_notch = notch_filter(raw_signal, sample_rate=sample_rate)
    clean = bandpass_filter(after_notch, sample_rate=sample_rate)
    return clean


def preprocess_all_channels(eeg_data, sample_rate=None):
    """
    Preprocess all EEG channels at once.

    Parameters:
        eeg_data:    2D array (n_channels, n_samples)
        sample_rate: Samples per second

    Returns:
        2D array (n_channels, n_samples) — all channels cleaned
    """
    if sample_rate is None:
        sample_rate = config.SAMPLE_RATE

    n_channels = eeg_data.shape[0]
    clean_data = np.zeros_like(eeg_data)

    for ch in range(n_channels):
        clean_data[ch] = preprocess_channel(eeg_data[ch], sample_rate)

    return clean_data


def preprocess_and_segment(eeg_data, sample_rate=None,
                           window_sec=None, step_sec=None, board_id=None):
    """
    Preprocess raw EEG and segment into analysis windows, rejecting any
    window that contains artifacts.

    Parameters:
        eeg_data:   2D array (n_channels, n_samples) — raw data
        window_sec: Window size in seconds
        step_sec:   Step between windows in seconds

    Returns:
        List of clean windows, each shape (n_channels, window_samples)
    """
    if sample_rate is None: sample_rate = config.SAMPLE_RATE
    if window_sec is None: window_sec = config.WINDOW_SECONDS
    if step_sec is None: step_sec = config.STEP_SECONDS

    clean_data = preprocess_all_channels(eeg_data, sample_rate)

    window_samples = int(window_sec * sample_rate)
    step_samples = int(step_sec * sample_rate)
    n_samples = clean_data.shape[1]

    windows = []
    rejected = 0

    for start in range(0, n_samples - window_samples + 1, step_samples):
        end = start + window_samples
        window = clean_data[:, start:end]

        if check_artifacts(window):
            windows.append(window)
        else:
            rejected += 1

    total = len(windows) + rejected
    if total > 0 and rejected > 0:
        print(f"  Artifact rejection: {rejected}/{total} windows rejected "
              f"({rejected / total * 100:.0f}%)")

    return windows
