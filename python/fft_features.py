"""
STEP 3: FFT Feature Extraction
==============================

Welch PSD per channel -> theta / alpha / beta band powers, then the three
focus metrics the ThresholdClassifier reads:

    [theta, alpha, beta, tbr, engagement, alpha_suppression]  (per channel)

    TBR               = theta / beta              (lower  = more focused)
    engagement index  = beta / (alpha + theta)    (higher = more focused)
    alpha_suppression = 1 - alpha / baseline_alpha (positive = more focused)
"""

import numpy as np
from brainflow.board_shim import BoardShim
from brainflow.data_filter import DataFilter, WindowOperations, DetrendOperations

import config


def extract_features_all_channels(window, board_id=None, params=None, baseline_alpha=None):
    """
    Extract FFT-based features from ALL channels in a window.

    Parameters:
        window:         2D array (n_channels, n_samples) — one preprocessed window
        board_id:       BrainFlow board ID (used to get the sampling rate)
        params:         (unused, kept for call-site compatibility)
        baseline_alpha: array of baseline alpha power per channel (from calibration).
                        Pass None during calibration, pass the array during scoring.

    Returns:
        features:           1D numpy array — per channel:
                            [theta, alpha, beta, tbr, engagement, alpha_suppression]
                            Total length = n_channels x 6
        band_powers_all_ch: list of dicts — per channel:
                            {'theta': ..., 'alpha': ..., 'beta': ...}
    """
    board_descr = BoardShim.get_board_descr(board_id)
    sampling_rate = int(board_descr['sampling_rate'])

    window = np.array(window)
    n_channels = window.shape[0]

    # Step 1: PSD + band powers per channel
    band_powers_all_ch = []
    for ch in range(n_channels):
        psd = get_fft_from_window(window[ch], sampling_rate)
        band_powers_all_ch.append(get_band_power(psd))

    # The classifier reads features by index using len(config.BANDS).
    # Set it to our 3 bands so its index math (tbr_idx = len(BANDS), etc.) works.
    config.BANDS = ['theta', 'alpha', 'beta']

    # Step 2: per channel -> [theta, alpha, beta, tbr, engagement, alpha_suppression]
    all_features = []
    for ch in range(n_channels):
        theta = band_powers_all_ch[ch]['theta']
        alpha = band_powers_all_ch[ch]['alpha']
        beta = band_powers_all_ch[ch]['beta']

        # TBR: theta / beta — lower means more focused
        tbr = theta / beta if beta > 0 else 0.0

        # Engagement index: beta / (alpha + theta) — higher means more focused
        denom = alpha + theta
        engagement = beta / denom if denom > 0 else 0.0

        # Alpha suppression vs baseline. During calibration baseline_alpha is None,
        # so this stays 0.0 (a placeholder); it's computed during real-time scoring.
        alpha_suppression = 0.0
        if baseline_alpha is not None and ch < len(baseline_alpha):
            if baseline_alpha[ch] > 0:
                alpha_suppression = 1.0 - (alpha / baseline_alpha[ch])

        all_features.extend([theta, alpha, beta, tbr, engagement, alpha_suppression])

        ch_name = config.CHANNEL_NAMES[ch] if ch < len(config.CHANNEL_NAMES) else '?'
        print(f"  Ch{ch} ({ch_name}): "
              f"theta={theta:.2f}  alpha={alpha:.2f}  beta={beta:.2f}  "
              f"TBR={tbr:.3f}  EI={engagement:.3f}")

    return np.array(all_features), band_powers_all_ch


def get_fft_from_window(signal, sampling_rate):
    """
    Compute PSD (Power Spectral Density) from a single channel's signal
    using Welch's method.

    Parameters:
        signal:        1D numpy array — one channel of preprocessed EEG
        sampling_rate: int — samples per second (256 for Muse 2)

    Returns:
        psd: tuple of (amplitudes, frequencies) from Welch's method
    """
    # Copy so detrend doesn't modify the original window data
    signal_copy = signal.copy()
    nfft = DataFilter.get_nearest_power_of_two(sampling_rate)
    DataFilter.detrend(signal_copy, DetrendOperations.LINEAR.value)
    psd = DataFilter.get_psd_welch(signal_copy, nfft, nfft // 2, sampling_rate,
                                   WindowOperations.BLACKMAN_HARRIS.value)
    return psd


def get_band_power(psd):
    """Extract theta / alpha / beta band powers from a PSD."""
    return {
        'theta': DataFilter.get_band_power(psd, 4.0, 8.0),
        'alpha': DataFilter.get_band_power(psd, 7.0, 13.0),
        'beta':  DataFilter.get_band_power(psd, 14.0, 38.0),
    }
