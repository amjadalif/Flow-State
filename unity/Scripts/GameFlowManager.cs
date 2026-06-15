using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Video;
using TMPro;
using static DashboardReceiver;
using Debug = UnityEngine.Debug;

// ──────────────────────────────────────────────────────────────────────────────
// Data models
// ──────────────────────────────────────────────────────────────────────────────

[Serializable]
public class SessionData
{
    public string playerName;
    public string sessionId;
    public string sessionDate;
    public List<float> focusScores = new List<float>();
    public float averageFocusScore;
    public float calibrationBaseline;
    public float lotusBloomAmount;      // 0 = bud, 1 = fully bloomed
    public int   lotusColorIndex = 0;   // which color variant (0-3 for now)
}

[Serializable]
public class LeaderboardData
{
    public List<SessionData> sessions = new List<SessionData>();
}

// ──────────────────────────────────────────────────────────────────────────────
// Stage enum — maps directly to stageScrollPositions[] index
// ──────────────────────────────────────────────────────────────────────────────

public enum GameStage
{
    IdleWaitingForEEG    = 0,
    NameEntry            = 1,
    EEGCalibration       = 2,
    VideoTaskPlayback    = 3,
    CharacterAnimation   = 4,   // reveal animation shown after focus session
    LeaderboardDashboard = 5,
    Resetting            = 6,
}

// ──────────────────────────────────────────────────────────────────────────────
// GameFlowManager
// Attach to a persistent GameObject in your scene (e.g. "GameManager").
// ──────────────────────────────────────────────────────────────────────────────

public class GameFlowManager : MonoBehaviour
{
    // ── Singleton ──────────────────────────────────────────────────────────────

    public static GameFlowManager Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────────────

    [Header("Scrolling")]
    [Tooltip("Reference to the Scrollingbehaviour component on the river background.")]
    public Scrollingbehaviour riverScroller;

    [Header("Stage UI Panels")]
    [Tooltip("Root GameObject for each stage panel — order must match GameStage enum.")]
    public GameObject[] stagePanels = new GameObject[6]; // index 0-5; Resetting (6) has no panel

    [Header("Name Entry (Stage 2)")]
    public TMP_InputField nameInputField;

    [Header("Calibration (Stage 3)")]
    public TextMeshProUGUI calibrationTimerText;
    public float           calibrationDuration = 10f;
    [Tooltip("VideoPlayer inside the calibration panel — plays as soon as the calibration stage is entered.")]
    public VideoPlayer     calibrationVideoPlayer;

    [Header("Video Task (Stage 4)")]
    public VideoPlayer     videoPlayer;
    public TextMeshProUGUI focusScoreDisplay;
    [Tooltip("How often (seconds) a focus reading is sampled and stored.")]
    public float           focusSampleInterval = 1f;
    [Tooltip("TMP text object shown before the task video plays — drag your instruction text object here.")]
    public TextMeshProUGUI videoInstructionText;
    [Tooltip("Seconds the instruction is displayed before it hides and the video begins.")]
    public float           videoInstructionDuration = 5f;

    [Header("Character Animation (Stage 5)")]
    [Tooltip("Fallback duration (seconds) if LotusController is not assigned.")]
    public float characterAnimationFallbackDuration = 5f;
    [Tooltip("Seconds to wait after the bloom finishes before advancing to the dashboard.")]
    public float characterAnimationExitDelay = 2f;

    [Header("Dashboard (Stage 6)")]
    // public Transform           leaderboardEntryContainer;
    public GameObject          leaderboardEntryPrefab;
    // public TextMeshProUGUI     sessionSummaryText;
    // [Tooltip("Seconds to display leaderboard before auto-resetting.")]
    public float               leaderboardDisplayTime = 12f;

    [Header("Lotus Flower")]
    [Tooltip("The LotusController already placed in the scene. Drag the lotus GameObject here.")]
    public LotusController lotusController;

    [Tooltip("Prefab with LotusController — used to spawn past session lotuses in idle background.")]
    public GameObject lotusPastPrefab;

    [Tooltip("Parent transform where past lotus instances are placed (under the river background).")]
    public Transform pastLotusContainer;

    [Tooltip("Speed past lotus flowers drift down the river (units/sec).")]
    public float pastLotusScrollSpeed = 5f;

    [Tooltip("Local Y below which a past lotus is recycled back to the top (keeps them on screen permanently).")]
    public float pastLotusRecycleY = -12f;

    [Tooltip("Local Y at which a recycled lotus reappears at the top of the container.")]
    public float pastLotusRespawnY = 12f;

    [Header("Leaderboard Lotuses (Stage 6)")]
    [Tooltip("Parent GameObject whose bounds define where leaderboard lotuses can spawn. " +
             "Assign an empty GameObject sized to match your leaderboard panel area.")]
    public Transform leaderboardLotusContainer;
    [Tooltip("Half-extents of the random spawn area in local space (x = half-width, y = half-height).")]
    public Vector2 leaderboardSpawnExtents = new Vector2(3f, 4f);

    [Header("EEG / Gyroscope (Stage 1)")]
    [Tooltip("Minimum unbiased rotation-rate magnitude that counts as a gesture.")]
    public float eegGyroThreshold = 0.5f;

    [Header("Python TCP Listener")]
    [Tooltip("Port Unity listens on for messages from the Python EEG bridge.")]
    public int pythonListenPort = 5006;

    [Header("Python UDP Sender")]
    [Tooltip("Port Python listens on for commands from Unity (START_CALIBRATION, START_SESSION).")]
    public int pythonCommandPort = 5007;

    [Header("Stage Entry Delays")]
    [Tooltip("Seconds to wait after the scroll starts before each stage's UI and logic activate. " +
             "Index matches GameStage enum (0=Idle, 1=Name, … 6=Resetting). " +
             "Set each value to match or slightly exceed your scroll animation duration.")]
    public float[] stageEntryDelays = new float[7];

    [Header("Testing (disable before shipping)")]
    [Tooltip("When enabled, overrides the EEG focus score with the value below.")]
    public bool overrideFocusScore = false;
    [Range(0f, 100f)]
    [Tooltip("Fake focus score (0–100) used when Override Focus Score is ticked.")]
    public float overrideFocusScoreValue = 75f;

    [Header("Python Launcher")]
    [Tooltip("Automatically launch realtime_focus.py when Play is pressed")]
    public bool autoLaunchPython = true;
    [Tooltip("Path to the Python executable (e.g. /Users/amjadali/.pyenv/versions/3.11.9/bin/python)")]
    public string pythonPath = "python";
    [Tooltip("Path to realtime_focus.py")]
    public string pythonScriptPath = "";

    // ── Private state ──────────────────────────────────────────────────────────

    private GameStage    currentStage = GameStage.IdleWaitingForEEG;
    private SessionData  currentSession;
    private LeaderboardData leaderboard;
    private string       saveFilePath;

    // Python bridge
    private TcpListener  tcpListener;
    private Thread       listenerThread;
    private volatile bool newPythonMessage;
    private string       latestPythonMessage = "";
    private readonly object msgLock = new object();

    // UDP sender → Python commands
    private UdpClient _pythonCommandSender;

    // Python process
    private Process _pythonProcess;
    private bool    _eegConnected;

    // Calibration
    private readonly List<float> calibrationReadings = new List<float>();
    private readonly Queue<string> _pythonOutputQueue = new Queue<string>();

    // Focus
    private float latestFocusScore = 0.8F;
    private readonly Queue<string> _pythonErrorQueue = new Queue<string>();

    // Motor control mode
    private bool _motorControlMode;
    private bool  focusSamplingActive;

    // Stage activation (cancelled if a new stage is entered before the delay elapses)
    private Coroutine stageActivationRoutine;

    // Lotus
    private Coroutine pastLotusScrollRoutine;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    // Cached reference — FindFirstObjectByType is expensive, do it once
    private DashboardReceiver _dashboardReceiver;

    void Awake()
    {
        // Singleton enforcement
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        saveFilePath = Path.Combine(Application.persistentDataPath, "leaderboard.json");
        LoadLeaderboard();

        Input.gyro.enabled = true;
    }

    void Start()
    {
        // Confirm name on Enter key press while the input field is focused
        if (nameInputField != null)
            nameInputField.onSubmit.AddListener(_ => OnNameConfirmed());
        else
            Debug.LogWarning("[GameFlowManager] Name Input Field not assigned — wire it in the Inspector.");

        _pythonCommandSender = new UdpClient();
        StartPythonListener();

        // Launch Python script automatically
        if (autoLaunchPython)
            LaunchPython();

        EnterStage(GameStage.IdleWaitingForEEG);
    }

    void OnDestroy()
    {
        StopPythonListener();
        if (_pythonCommandSender != null)
            _pythonCommandSender.Close();
        KillPython();
    }

    void OnApplicationQuit()
    {
        StopPythonListener();
        if (_pythonCommandSender != null)
            _pythonCommandSender.Close();
        KillPython();
    }

    // ── Python process management ────────────────────────────────────────────

    private void LaunchPython()
    {
        if (string.IsNullOrEmpty(pythonScriptPath))
        {
            Debug.LogWarning("[GameFlowManager] Python script path not set — skipping auto-launch. Set it in the Inspector.");
            return;
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-u \"{pythonScriptPath}\" --unity",  // -u = unbuffered, --unity = no menu
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Set working directory to the script's folder
            string scriptDir = Path.GetDirectoryName(pythonScriptPath);
            if (!string.IsNullOrEmpty(scriptDir))
                psi.WorkingDirectory = scriptDir;

            _pythonProcess = Process.Start(psi);

            // Read Python's stdout and stderr on background threads → Unity Console
            _pythonProcess.OutputDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    lock (msgLock)
                    {
                        _pythonOutputQueue.Enqueue(args.Data);
                    }
                }
            };
            _pythonProcess.ErrorDataReceived += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    lock (msgLock)
                    {
                        _pythonErrorQueue.Enqueue(args.Data);
                    }
                }
            };
            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();

            Debug.Log($"[GameFlowManager] Python launched (PID {_pythonProcess?.Id}): {pythonScriptPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameFlowManager] Failed to launch Python: {e.Message}");
        }
    }

    private void KillPython()
    {
        if (_pythonProcess != null && !_pythonProcess.HasExited)
        {
            try
            {
                _pythonProcess.Kill();
                _pythonProcess.WaitForExit(3000);
                Debug.Log("[GameFlowManager] Python process killed.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameFlowManager] Error killing Python: {e.Message}");
            }
        }
        _pythonProcess = null;
    }

    // ── Send commands to Python via UDP ─────────────────────────────────────

    /// <summary>
    /// Send a command string to Python's UnityCommandListener (UDP port 5007).
    /// </summary>
    private void SendToPython(string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _pythonCommandSender.Send(data, data.Length, "127.0.0.1", pythonCommandPort);
            Debug.Log($"[GameFlowManager] Sent to Python: {message}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameFlowManager] Failed to send to Python: {e.Message}");
        }
    }

    // ── Update: EEG poll + Python message dispatch ─────────────────────────────

    void Update()
    {
        if (currentStage == GameStage.IdleWaitingForEEG)
        {
            if (_motorControlMode)
                PollMotorControls();
            else
                PollEEGGyroscope();
        }

        if (newPythonMessage)
        {
            string msg;
            lock (msgLock) { msg = latestPythonMessage; newPythonMessage = false; }
            HandlePythonMessage(msg);
        }

        // Flush Python stdout/stderr to Unity Console
        lock (msgLock)
        {
            while (_pythonOutputQueue.Count > 0)
                Debug.Log($"[Python] {_pythonOutputQueue.Dequeue()}");
            while (_pythonErrorQueue.Count > 0)
                Debug.LogWarning($"[Python] {_pythonErrorQueue.Dequeue()}");
        }
    }

    // ── Stage machine ──────────────────────────────────────────────────────────

    void EnterStage(GameStage stage)
    {
        currentStage = stage;
        _motorControlMode = false;  // always exit motor mode on stage change
        SetAllPanelsInactive();

        // Scroll and lotus glide start immediately so the animation is already underway
        // while the entry delay counts down.
        riverScroller?.ScrollToStage((int)stage);
        lotusController?.MoveToStage((int)stage);

        // Cancel any pending activation from a previous stage that hasn't fired yet.
        if (stageActivationRoutine != null)
        {
            StopCoroutine(stageActivationRoutine);
            stageActivationRoutine = null;
        }

        stageActivationRoutine = StartCoroutine(ActivateStageAfterDelay(stage));
    }

    IEnumerator ActivateStageAfterDelay(GameStage stage)
    {
        int   stageIndex = (int)stage;
        float delay      = stageIndex < stageEntryDelays.Length ? stageEntryDelays[stageIndex] : 0f;
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        stageActivationRoutine = null;

        // Use the receiver cached in Start() — avoids a per-stage FindFirstObjectByType call.
        DashboardReceiver dashboardReceiver = _dashboardReceiver;

        switch (stage)
        {
            case GameStage.IdleWaitingForEEG:
                ActivatePanel(0);
                AudioManager.Instance?.PlayIdleMusic();
                break;

            case GameStage.NameEntry:
                ActivatePanel(1);
                lotusController?.OnNameEntered();
                if (nameInputField != null)
                {
                nameInputField.text = "";
                nameInputField.ActivateInputField();
                }
                // Continues idle music — no change needed
                break;

            case GameStage.EEGCalibration:
                ActivatePanel(2);
                if (calibrationVideoPlayer != null && calibrationVideoPlayer.clip != null)
                    calibrationVideoPlayer.Play();
                StartCoroutine(RunCalibration());
                AudioManager.Instance?.PlayCalibrationMusic();
                break;

            case GameStage.VideoTaskPlayback:
                ActivatePanel(3);
                StartCoroutine(RunVideoTask());
                break;

            case GameStage.CharacterAnimation:
                ActivatePanel(4);
                lotusController?.SetName(currentSession?.playerName ?? string.Empty);
                float revealScore = GetAverageFocusScore();
                lotusController?.OnDashboardReached(revealScore);
                AudioManager.Instance?.PlayLotusReveal(Mathf.Clamp01(revealScore / 100f));
                StartCoroutine(RunCharacterAnimation());
                break;

            case GameStage.LeaderboardDashboard:
                ActivatePanel(5);
                SpawnLeaderboardLotuses();   // static lotuses in the leaderboard panel
                SpawnPastLotuses();          // drifting river of all past session lotuses
                dashboardReceiver?.LoadAndUpdateDisplay();
                // AudioManager wants a normalised 0–1 value; LotusController wants the raw 0–100 score
                float avgScore        = GetAverageFocusScore();
                float normalisedScore = Mathf.Clamp01(avgScore / 100f);
                AudioManager.Instance?.PlayDashboardAudio(normalisedScore);
                StartCoroutine(AutoAdvance(leaderboardDisplayTime, GameStage.Resetting));
                break;

            case GameStage.Resetting:
                AudioManager.Instance?.FadeOutMusic();
                StartCoroutine(ResetFlow());
                break;
        }
    }

    void SetAllPanelsInactive()
    {
        foreach (var panel in stagePanels)
            if (panel) panel.SetActive(false);
    }

    void ActivatePanel(int index)
    {
        if (index < stagePanels.Length && stagePanels[index])
            stagePanels[index].SetActive(true);
    }

    // ── Stage 1: Idle — EEG gyroscope pickup ──────────────────────────────────

    void PollEEGGyroscope()
    {
        // Uses Unity's built-in gyroscope. Replace with your EEG SDK call if needed.
        // Python can also trigger this stage by sending "EEG_TRIGGER" over TCP.
        if (Input.gyro.rotationRateUnbiased.magnitude > eegGyroThreshold)
            EnterStage(GameStage.NameEntry);

#if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.Space))
            EnterStage(GameStage.NameEntry);

        // Press 1 to enter motor control mode
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            _motorControlMode = true;
            Debug.Log("[Motor Control] Entered motor control mode.\n" +
                      "  1 = Wind Motor 1\n" +
                      "  2 = Unwind Motor 1\n" +
                      "  3 = Wind Motor 2\n" +
                      "  4 = Unwind Motor 2\n" +
                      "  SPACE = Stop motors & return to Welcome");
        }
#endif
    }

    // ── Motor control mode (runs while on Welcome screen) ────────────────────

    void PollMotorControls()
    {
#if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            SendToPython("M1W");
            Debug.Log("[Motor Control] → Motor 1: WINDING...");
        }
        else if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            SendToPython("M1U");
            Debug.Log("[Motor Control] → Motor 1: UNWINDING...");
        }
        else if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            SendToPython("M2W");
            Debug.Log("[Motor Control] → Motor 2: WINDING...");
        }
        else if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            SendToPython("M2U");
            Debug.Log("[Motor Control] → Motor 2: UNWINDING...");
        }
        else if (Input.GetKeyDown(KeyCode.Space))
        {
            SendToPython("MSTOP");
            _motorControlMode = false;
            Debug.Log("[Motor Control] Motors stopped. Returned to Welcome screen.");
        }
#endif
    }

    // ── Stage 2: Name entry ────────────────────────────────────────────────────

    private void OnNameConfirmed()
{
    string charactername = nameInputField.text.Trim();
    if (string.IsNullOrEmpty(charactername)) return; // also fixed: was checking 'name' not 'charactername'

    currentSession = new SessionData
    {
        playerName  = charactername,
        sessionId   = System.Guid.NewGuid().ToString(),
        sessionDate = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    };

    // Show the player's name on the lotus as soon as it's confirmed — stays visible all session.
    lotusController?.SetName(currentSession.playerName);

  
    
    // Tell Python to start calibrating for this player
    SendToPython($"START_CALIBRATION:{charactername}");

    // OnNameEntered() is already called inside EnterStage(NameEntry) — do not call it here again.
    EnterStage(GameStage.EEGCalibration);
}

    // ── Stage 3: EEG Calibration (10 s) ───────────────────────────────────────
    // Python sends "CALIB:<value>" messages during this window.
    // The average of those readings becomes the baseline for normalising focus.

    IEnumerator RunCalibration()
    {
        calibrationReadings.Clear();
        
          //align waiting time for python and unity
        float elapsed = 0f;
        while (elapsed < calibrationDuration)
        {
            elapsed += Time.deltaTime;
            float remaining = Mathf.CeilToInt(calibrationDuration - elapsed);
            if (calibrationTimerText)
                calibrationTimerText.text = $"Calibrating for your garden \n Clear your mind and keep your head still.. \n {remaining:0}s";
            yield return null;
        }

        if (calibrationTimerText)
            calibrationTimerText.text = "Calibration complete!";

        AudioManager.Instance?.PlayCalibrationComplete();
        currentSession.calibrationBaseline = ListAverage(calibrationReadings);

       

        yield return new WaitForSeconds(1f);
        EnterStage(GameStage.VideoTaskPlayback);
    }

    // ── Stage 4: Video task + live focus recording ─────────────────────────────
    // Python sends "FOCUS:<value>" continuously; we sample at focusSampleInterval.

    IEnumerator RunVideoTask()
    {
        // Show instruction text, wait, then hide before video starts
        if (videoInstructionText != null)
        {
            videoInstructionText.gameObject.SetActive(true);
            yield return new WaitForSeconds(videoInstructionDuration);
            videoInstructionText.gameObject.SetActive(false);
        }

         // Tell Python calibration is done, start the focus session
        SendToPython("START_SESSION");
        focusSamplingActive = true;
        StartCoroutine(SampleFocusScore());

        if (videoPlayer && videoPlayer.clip != null)
        {
            videoPlayer.Play();

            // Step 1: wait until the video is actually prepared and playing
            yield return new WaitUntil(() => videoPlayer.isPrepared && videoPlayer.isPlaying);

            // Step 2: now wait until it stops (end of clip)
            yield return new WaitUntil(() => !videoPlayer.isPlaying);
        }
        else
        {
            // Fallback: run for a fixed time if no clip is assigned
            Debug.LogWarning("[GameFlowManager] No video clip assigned — using 30s fallback.");
            yield return new WaitForSeconds(30f);
        }

        focusSamplingActive = false;

       
        yield return new WaitForSeconds(1.5f);  // let the sting play before transitioning
        EnterStage(GameStage.CharacterAnimation);
    }

    // ── Stage 5: Character animation reveal ───────────────────────────────────

    IEnumerator RunCharacterAnimation()
    {
        // Wait for the lotus bloom PNG sequence to finish.
        // LotusController.HasBloomed becomes true at the end of PlayBloom().
        // Fall back to a fixed duration if no lotus controller is assigned.
        if (lotusController != null)
            yield return new WaitUntil(() => lotusController.HasBloomed);
        else
            yield return new WaitForSeconds(characterAnimationFallbackDuration);

        if (characterAnimationExitDelay > 0f)
            yield return new WaitForSeconds(characterAnimationExitDelay);

        EnterStage(GameStage.LeaderboardDashboard);
    }

    IEnumerator SampleFocusScore()
    {
        while (focusSamplingActive)
        {
            currentSession.focusScores.Add(latestFocusScore);

            string state = latestFocusScore > 66f ? "FOCUSED" :
                           latestFocusScore <= 33f ? "DISTRACTED" : "NEUTRAL";

            // Update the in-scene focus score display (canvas UI label)
            if (focusScoreDisplay)
            {
                focusScoreDisplay.text = $"Focus: {latestFocusScore:F1}/100  {state}";
                focusScoreDisplay.color = state switch
                {
                    "FOCUSED"    => Color.green,
                    "DISTRACTED" => Color.red,
                    "NEUTRAL"    => Color.yellow,
                    _            => Color.gray
                };
            }

            // Mirror the live score onto the lotus character label
            lotusController?.SetLiveScore(latestFocusScore);

            yield return new WaitForSeconds(focusSampleInterval);
        }
    }

    // ── Stage 5: Leaderboard & Dashboard ──────────────────────────────────────

    // void FinaliseAndShowSession()
    // {
    //     currentSession.averageFocusScore = ListAverage(currentSession.focusScores);

    //     // Lotus bloom frame is saved inside LotusController.PlayBloom() via PlayerPrefs.
        // Store normalized bloom in session data for leaderboard display.
        // currentSession.lotusBloomAmount = Mathf.Clamp01(getAverageFocusScore / 100f);
        // currentSession.lotusColorIndex  = UnityEngine.Random.Range(0, 4);

    //     // Persist
    //     leaderboard.sessions.Add(currentSession);
    //     leaderboard.sessions.Sort((a, b) => b.averageFocusScore.CompareTo(a.averageFocusScore));
    //     SaveLeaderboard();

    //     // Summary card
    //     if (sessionSummaryText)
    //         sessionSummaryText.text =
    //             $"Player:     {currentSession.playerName}\n" +
    //             $"Date:       {currentSession.sessionDate}\n" +
    //             $"Avg Focus:  {currentSession.averageFocusScore:F1}\n" +
    //             $"Baseline:   {currentSession.calibrationBaseline:F2}";

    //     // Leaderboard rows
    //     if (leaderboardEntryContainer && leaderboardEntryPrefab)
    //     {
    //         foreach (Transform child in leaderboardEntryContainer)
    //             Destroy(child.gameObject);

    //         for (int i = 0; i < leaderboard.sessions.Count; i++)
    //         {
    //             SessionData s    = leaderboard.sessions[i];
    //             GameObject  row  = Instantiate(leaderboardEntryPrefab, leaderboardEntryContainer);
    //             var         texts = row.GetComponentsInChildren<TextMeshProUGUI>();
    //             if (texts.Length >= 3)
    //             {
    //                 texts[0].text = $"{i + 1}.";
    //                 texts[1].text = s.playerName;
    //                 texts[2].text = $"{s.averageFocusScore:F1}";
    //             }
    //         }
    //     }
    // }

    // ── Stage 6: Reset ────────────────────────────────────────────────────────

    IEnumerator ResetFlow()
    {
        currentSession   = null;
        latestFocusScore = 0f;
        lotusController?.ResetLotus();  // hide and reset bud for next player
        ClearLeaderboardLotuses();
        ClearPastLotuses();             // destroy all spawned past-session lotuses
        focusSamplingActive = false;
        calibrationReadings.Clear();

        yield return new WaitForSeconds(0.5f);
        EnterStage(GameStage.IdleWaitingForEEG);
    }

    IEnumerator AutoAdvance(float delay, GameStage next)
    {
        yield return new WaitForSeconds(delay);
        if (currentStage == GameStage.LeaderboardDashboard)
            EnterStage(next);
    }

    // ── Leaderboard lotus management ─────────────────────────────────────────

    void SpawnLeaderboardLotuses()
    {
        if (!leaderboardLotusContainer || !lotusPastPrefab) return;

        // Clear any previous instances
        foreach (Transform child in leaderboardLotusContainer)
            Destroy(child.gameObject);

        // Pull session history from DashboardReceiver — it is the authoritative store
        // (written by Python via UDP when session_complete arrives).
        var history = _dashboardReceiver?.LoadHistory();
        if (history == null || history.sessions.Count == 0) return;

        foreach (var session in history.sessions)
        {
            int   storedFrame = PlayerPrefs.GetInt  ($"LotusFrame_{session.charactername}", 0);
            float storedScore = PlayerPrefs.GetFloat($"FocusScore_{session.charactername}", session.score);

            GameObject      go    = Instantiate(lotusPastPrefab, leaderboardLotusContainer);
            LotusController lotus = go.GetComponent<LotusController>();

            if (lotus)
            {
                lotus.DisplayStoredState(storedFrame);
                lotus.SetName(session.charactername);
            }

            // Random position within the assigned container bounds
            go.transform.localPosition = new Vector3(
                UnityEngine.Random.Range(-leaderboardSpawnExtents.x, leaderboardSpawnExtents.x),
                UnityEngine.Random.Range(-leaderboardSpawnExtents.y, leaderboardSpawnExtents.y),
                0f
            );
        }
    }

    void ClearLeaderboardLotuses()
    {
        if (!leaderboardLotusContainer) return;
        foreach (Transform child in leaderboardLotusContainer)
            Destroy(child.gameObject);
    }

    // ── Lotus management ──────────────────────────────────────────────────────

    void SpawnPastLotuses()
    {
        if (!pastLotusContainer || !lotusPastPrefab) return;

        // Clear previous instances
        foreach (Transform child in pastLotusContainer)
            Destroy(child.gameObject);

        // Pull session history from DashboardReceiver — the authoritative data source.
        var history = _dashboardReceiver?.LoadHistory();
        if (history == null || history.sessions.Count == 0) return;

        for (int i = 0; i < history.sessions.Count; i++)
        {
            var session    = history.sessions[i];
            int storedFrame = PlayerPrefs.GetInt($"LotusFrame_{session.charactername}", 0);

            GameObject      go    = Instantiate(lotusPastPrefab, pastLotusContainer);
            LotusController lotus = go.GetComponent<LotusController>();

            if (lotus)
            {
                lotus.DisplayStoredState(storedFrame);
                lotus.SetName(session.charactername);
            }

            // Stagger each lotus horizontally and vertically on the river
            go.transform.localPosition = new Vector3(
                UnityEngine.Random.Range(-2f, 2f),
                pastLotusRespawnY - i * 1.5f,
                0f
            );
        }

        // Start the drift + recycle animation
        if (pastLotusScrollRoutine != null) StopCoroutine(pastLotusScrollRoutine);
        pastLotusScrollRoutine = StartCoroutine(ScrollPastLotuses());
    }

    /// <summary>
    /// Stops drift and destroys all past lotus instances.
    /// Wire to a UI button or call from code to manually clear the idle river.
    /// </summary>
    public void ClearPastLotuses()
    {
        if (pastLotusScrollRoutine != null)
        {
            StopCoroutine(pastLotusScrollRoutine);
            pastLotusScrollRoutine = null;
        }
        if (pastLotusContainer)
            foreach (Transform child in pastLotusContainer)
                Destroy(child.gameObject);
    }

    IEnumerator ScrollPastLotuses()
    {
        while (true)
        {
            if (pastLotusContainer != null)
            {
                foreach (Transform child in pastLotusContainer)
                {
                    // Drift downward
                    child.localPosition -= new Vector3(0f, pastLotusScrollSpeed * Time.deltaTime, 0f);

                    // Recycle: when a lotus drifts below the recycle threshold,
                    // teleport it back to the top at a new random X so it loops forever.
                    if (child.localPosition.y < pastLotusRecycleY)
                    {
                        child.localPosition = new Vector3(
                            UnityEngine.Random.Range(-2f, 2f),
                            pastLotusRespawnY,
                            0f
                        );
                    }
                }
            }
            yield return null;
        }
    }

    // ── Python TCP bridge ──────────────────────────────────────────────────────
    // Run python_bridge.py on the same machine; it connects to 127.0.0.1:<pythonListenPort>
    // and sends newline-terminated strings:
    //   EEG_TRIGGER          — advance from idle to name entry
    //   CALIB:<float>        — calibration reading (stage 3)
    //   FOCUS:<float>        — live focus score   (stage 4)

    void StartPythonListener()
    {
        listenerThread = new Thread(ListenLoop) { IsBackground = true };
        listenerThread.Start();
    }

    void StopPythonListener()
    {
        try { tcpListener?.Stop(); } catch { }
        listenerThread = null;
    }

    void ListenLoop()
    {
        tcpListener = new TcpListener(IPAddress.Loopback, pythonListenPort);
        tcpListener.Start();

        while (true)
        {
            TcpClient client = null;
            try
            {
                client = tcpListener.AcceptTcpClient();
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] buf   = new byte[256];
                    int    bytes = stream.Read(buf, 0, buf.Length);
                    string msg   = Encoding.UTF8.GetString(buf, 0, bytes).Trim();

                    lock (msgLock)
                    {
                        latestPythonMessage = msg;
                        newPythonMessage    = true;
                    }
                }
            }
            catch (Exception) { break; }
            finally { client?.Close(); }
        }
    }

    void HandlePythonMessage(string msg)
    {
        if (msg.StartsWith("FOCUS:") && float.TryParse(msg.Substring(6), out float fs))
        {
            latestFocusScore = fs;
        }
        else if (msg.StartsWith("CALIB:") && float.TryParse(msg.Substring(6), out float cal))
        {
            if (currentStage == GameStage.EEGCalibration)
                calibrationReadings.Add(cal);
        }
        else if (msg == "EEG_TRIGGER" && currentStage == GameStage.IdleWaitingForEEG)
        {
            if (!_eegConnected)
            {
                _eegConnected = true;
                Debug.Log("[GameFlowManager] ✅ Python running & EEG connected successfully — press SPACE to start, or 1 for motor control");
            }
            EnterStage(GameStage.NameEntry);
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    void SaveLeaderboard()
    {
        File.WriteAllText(saveFilePath, JsonUtility.ToJson(leaderboard, true));
    }

    void LoadLeaderboard()
    {
        leaderboard = File.Exists(saveFilePath)
            ? JsonUtility.FromJson<LeaderboardData>(File.ReadAllText(saveFilePath))
            : new LeaderboardData();
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    static float ListAverage(List<float> values)
    {
        if (values == null || values.Count == 0) return 0f;
        float sum = 0f;
        foreach (float v in values) sum += v;
        return sum / values.Count;
    }

    // Public accessor so UI buttons (e.g. "Skip to leaderboard") can call this.
    // Guard prevents advancing past the final stage.
    public void AdvanceStageManually()
    {
        if (currentStage < GameStage.Resetting)
            EnterStage(currentStage + 1);
    }

    // ── Public accessors for LotusController ──────────────────────────────────

    /// <summary>Returns the latest focus score (0–100) streamed from Python.</summary>
    public float GetFocusScore() => latestFocusScore;

    /// <summary>
    /// Called by DashboardReceiver when it receives a live score via UDP.
    /// Updates latestFocusScore so SampleFocusScore() captures real EEG data.
    /// </summary>
    public void SetLiveFocusScore(float score)
    {
        latestFocusScore = score;
        if (lotusController != null)
            lotusController.SetLiveScore(score); 
    }

    /// <summary>Returns the session average focus score, falling back to the latest reading.
    /// Returns the inspector override value when overrideFocusScore is ticked.</summary>
    public float GetAverageFocusScore()
    {
        if (overrideFocusScore) return overrideFocusScoreValue;
        if (currentSession != null && currentSession.focusScores.Count > 0)
            return ListAverage(currentSession.focusScores);
        return latestFocusScore;
    }

    /// <summary>Returns the current player's name, or an empty string if no session is active.</summary>
    public string GetCurrentSessionName() => currentSession?.playerName ?? string.Empty;
}