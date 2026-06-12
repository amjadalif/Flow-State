import config
import numpy as np
import fft_features

class ThresholdClassifier:
    """
    Threshold-based focus scoring using TBR, engagement
    index, and alpha suppression. No training required.
    """

    def __init__(self,
                 tbr_weight=0.35,
                 engagement_weight=0.35,
                 alpha_suppression_weight=0.30):
        self.tbr_weight = tbr_weight
        self.engagement_weight = engagement_weight
        self.alpha_suppression_weight = alpha_suppression_weight

        self.baseline_tbr = None
        self.baseline_engagement = None
        self.baseline_alpha = None

        self.history = []
        self.history_len = 5

    def calibrate(self, baseline_windows, board_id=None):
        """
        Process resting baseline windows to establish
        personal reference values for all three metrics.
        
        IMPORTANT: Averages across the SAME channels that
        compute_focus_score uses (frontal for TBR/engagement,
        posterior for alpha suppression).
        """
        
        tbrs = []
        engagements = []
        alpha_powers = [[] for _ in range(len(config.CHANNEL_NAMES))]

        for window in baseline_windows:
            features, band_powers_all_ch = fft_features.extract_features_all_channels(
                window, board_id=board_id, baseline_alpha=None
            )

            # Feature order per channel:
            # [theta, alpha, beta, tbr, engagement, alpha_suppression]
            # That's 3 bands + 3 metrics = 6 per channel
            n_per_ch = len(config.BANDS) + 3

            tbr_idx = len(config.BANDS)       # index 3
            eng_idx = len(config.BANDS) + 1   # index 4

            # Average TBR and engagement across FRONTAL channels [1, 2]
            # (AF7, AF8) — same channels used in compute_focus_score
            frontal_channels = [1, 2]
            
            tbr_values = [features[tbr_idx + ch * n_per_ch] for ch in frontal_channels]
            eng_values = [features[eng_idx + ch * n_per_ch] for ch in frontal_channels]
            
            tbrs.append(np.mean(tbr_values))
            engagements.append(np.mean(eng_values))

            # Record alpha power per channel for suppression baseline
            for ch in range(len(config.CHANNEL_NAMES)):
                alpha_powers[ch].append(
                    band_powers_all_ch[ch]['alpha']
                )

        # Add floor values to prevent division by zero during scoring
        self.baseline_tbr = max(np.mean(tbrs), 0.01)
        self.baseline_engagement = max(np.mean(engagements), 0.01)
        self.baseline_alpha = np.array([
            max(np.mean(alpha_powers[ch]), 0.01)
            for ch in range(len(config.CHANNEL_NAMES))
        ])

        print("Calibration complete.")
        print(f"  Baseline TBR:        {self.baseline_tbr:.3f}")
        print(f"  Baseline Engagement: {self.baseline_engagement:.3f}")
        print(f"  Baseline Alpha (per channel): "
              f"{self.baseline_alpha.round(3)}")

    def compute_focus_score(self, window, board_id=None):
        """
        Returns a smoothed focus score from 0-100.
        Blends TBR, engagement index, and alpha suppression.
        """
        if self.baseline_tbr is None:
            raise RuntimeError("Must calibrate before scoring.")

        features, band_powers_all_ch = fft_features.extract_features_all_channels(
            window,
            board_id=board_id,
            baseline_alpha=self.baseline_alpha
        )

        n_per_ch = len(config.BANDS) + 3
        tbr_idx = len(config.BANDS)
        eng_idx = len(config.BANDS) + 1
        as_idx  = len(config.BANDS) + 2

        frontal_channels = [1, 2]
        posterior_channels = [0, 3]

        tbr_values = [
            features[tbr_idx + ch * n_per_ch]
            for ch in frontal_channels
        ]
        eng_values = [
            features[eng_idx + ch * n_per_ch]
            for ch in frontal_channels
        ]
        as_values = [
            features[as_idx + ch * n_per_ch]
            for ch in posterior_channels
        ]

        tbr_mean = np.mean(tbr_values)
        eng_mean = np.mean(eng_values)
        as_mean  = np.mean(as_values)

        # TBR: lower than baseline = more focused
        tbr_contribution = 1 - (tbr_mean / self.baseline_tbr)

        # Engagement Index: higher than baseline = more focused
        # Clamp to prevent extreme values from dominating
        eng_ratio = eng_mean / self.baseline_engagement
        eng_contribution = np.clip(eng_ratio - 1, -2.0, 2.0)

        # Alpha Suppression: positive means alpha dropped = more focused
        as_contribution = as_mean

        # Blend contributions with weights
        raw_score = (
            self.tbr_weight * tbr_contribution +
            self.engagement_weight * eng_contribution +
            self.alpha_suppression_weight * as_contribution
        )

        # Scale to 0-100
        focus_score = np.clip(50 + (raw_score * 50), 0, 100)

        # Smooth output over recent history
        self.history.append(focus_score)
        if len(self.history) > self.history_len:
            self.history.pop(0)

        smoothed_score = np.mean(self.history)

        print(f"  TBR contribution:        {tbr_contribution:+.3f}")
        print(f"  Engagement contribution: {eng_contribution:+.3f}")
        print(f"  Alpha suppression:       {as_contribution:+.3f}")
        print(f"  Raw score: {raw_score:.3f} -> "
              f"Focus score: {smoothed_score:.1f}/100")

        return smoothed_score
