using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using XCharts.Runtime;

/// <summary>
/// DashboardReceiver — Receives session data from Python via UDP,
/// saves to JSON history, and updates the dashboard UI.
///
/// SETUP:
///   1. Create Empty → name "DashboardReceiver"
///   2. Attach this script
///   3. Drag these into the Inspector:
///      - FocusScoreNumber   → Focus Score Text
///      - YourRankNumber     → Rank Text
///      - OutOf              → Out Of Text
///      - AverageRecovery    → Recovery Time Text
///      - FocusedNumber      → Focused Pct Text
///      - DistractedNumber   → Distracted Pct Text
///      - PieChart           → Pie Chart
/// </summary>
public class DashboardReceiver : MonoBehaviour
{
    [Header("Network")]
    [Tooltip("Must match the port in unity_sender.py (default 5005)")]
    public int listenPort = 5005;

    [Header("UI References")]
    [Tooltip("Drag FocusScoreNumber here")]
    public TMP_Text focusScoreText;

    [Tooltip("Drag YourRankNumber here")]
    public TMP_Text rankText;

    [Tooltip("Drag OutOf here")]
    public TMP_Text outOfText;

    [Tooltip("Drag recovery time number text here")]
    public TMP_Text recoveryTimeText;

    [Tooltip("Drag FocusedNumber here (shows '62%')")]
    public TMP_Text focusedPctText;

    [Tooltip("Drag DistractedNumber here (shows '38%')")]
    public TMP_Text distractedPctText;

    [Tooltip("Drag FocusConsistency number text here (shows '68/100')")]
    public TMP_Text focusConsistencyText;

    [Header("Charts")]
    [Tooltip("Drag your PieChart object here")]
    public PieChart pieChart;

    [Header("Leaderboard")]
    [Tooltip("Drag the 'Content' object from inside LeaderboardPanel → Viewport → Content")]
    public Transform leaderboardContent;

    [Tooltip("Drag the leaderboard row prefab here (needs 3 TMP_Text children: rank, name, score)")]
    public GameObject leaderboardRowPrefab;

    [Tooltip("Drag the trophy sprite here (used for top 3 ranks)")]
    public Sprite trophySprite;

    [Header("Timeline")]
    [Tooltip("Drag all 30 TimelineBar Image objects here (in order, 0-29)")]
    public Image[] timelineBars;

    [Tooltip("Drag the FocusDrops text here (shows 'Focus drops at 20s, 42s and 55s')")]
    public TMP_Text focusDropsText;

    [Header("Live Score")]
    [Tooltip("Drag a TextMeshPro text object here to show live focus score during session")]
    public TMP_Text liveScoreText;

    [Header("Character Name")]
    [Tooltip("Drag the FlowerTitle text here to show the player's name")]
    public TMP_Text flowerTitleText;

    // ── Internal ──
    private UdpClient _udpClient;
    private Thread _receiveThread;
    private bool _running = false;

    private readonly object _lock = new object();
    private string _charactername = "";
    private float _latestScore = 0f;
    private string _latestState = "";
    private float _latestAvgRecovery = 0f;
    private int _latestRecoveryCount = 0;
    private float _latestFocusedPct = 0f;
    private float _latestDistractedPct = 0f;
    private float _latestFocusConsistency = 0f;
    private string[] _latestTimelineBars = new string[30];
    private string _latestDropTimesStr = "";
    private bool _hasNewData = false;

    // Live score (updated every 0.5s during session)
    private float _liveScore = 0f;
    private string _liveState = "WAITING";
    private bool _hasNewLiveScore = false;

    private string _historyPath;

    void Start()
    {
        _historyPath = Path.Combine(Application.persistentDataPath, "session_history.json");

        _running = true;
        _udpClient = new UdpClient(listenPort);
        _receiveThread = new Thread(ReceiveLoop);
        _receiveThread.IsBackground = true;
        _receiveThread.Start();

        Debug.Log($"[DashboardReceiver] Listening on UDP port {listenPort}");
        Debug.Log($"[DashboardReceiver] History file: {_historyPath}");

        LoadAndUpdateDisplay();
    }

    void Update()
    {
        lock (_lock)
        {
            if (_hasNewData)
            {
                // Get the player name from GameFlowManager (same Unity project)
                if (GameFlowManager.Instance != null)
                {
                    string playerName = GameFlowManager.Instance.GetCurrentSessionName();
                    if (!string.IsNullOrEmpty(playerName))
                        _charactername = playerName;
                }

                SaveScore(_charactername, _latestScore, _latestState, _latestAvgRecovery,
                          _latestRecoveryCount, _latestFocusedPct, _latestDistractedPct,
                          _latestFocusConsistency);
                LoadAndUpdateDisplay();

                // ── Color the timeline bars ──
                UpdateTimelineBars();

                // ── Update drop times text ──
                if (focusDropsText != null)
                {
                    if (!string.IsNullOrEmpty(_latestDropTimesStr))
                        focusDropsText.text = $"Focus drops at {_latestDropTimesStr}";
                    else
                        focusDropsText.text = "No focus drops!";
                }

                Debug.Log($"[DashboardReceiver] Session complete → Name: {_charactername}, Score: {_latestScore:F1}, " +
                          $"State: {_latestState}, Recovery: {_latestAvgRecovery:F1}s, " +
                          $"Focused: {_latestFocusedPct}%, Distracted: {_latestDistractedPct}%");
                _hasNewData = false;
            }

            // ── Live score update (every 0.5s during session) ──
            if (_hasNewLiveScore && liveScoreText != null)
            {
                liveScoreText.text = $"{_liveScore:F0}";

                if (_liveState == "FOCUSED")
                    liveScoreText.color = new Color(0.3f, 0.8f, 0.3f);
                else if (_liveState == "DISTRACTED")
                    liveScoreText.color = new Color(0.85f, 0.3f, 0.3f);
                else
                    liveScoreText.color = new Color(1f, 0.9f, 0.2f);

                _hasNewLiveScore = false;
            }
        }
    }

    void OnDestroy()
    {
        _running = false;
        if (_udpClient != null)
            _udpClient.Close();
        if (_receiveThread != null && _receiveThread.IsAlive)
            _receiveThread.Abort();
    }

    // ══════════════════════════════════════════════
    //  HISTORY FILE
    // ══════════════════════════════════════════════

    private void SaveScore(string sessionname, float score, string state, float avgRecovery,
                           int recoveryCount, float focusedPct, float distractedPct,
                           float focusConsistency)
    {
        SessionHistory history = LoadHistory();

        SessionEntry newEntry = new SessionEntry
        {
            charactername = sessionname,
            score = Mathf.Round(score * 10f) / 10f,
            state = state,
            avg_recovery_time = Mathf.Round(avgRecovery * 10f) / 10f,
            recovery_count = recoveryCount,
            focused_pct = Mathf.Round(focusedPct * 10f) / 10f,
            distracted_pct = Mathf.Round(distractedPct * 10f) / 10f,
            focus_consistency = Mathf.Round(focusConsistency * 10f) / 10f,
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        history.sessions.Add(newEntry);

        string json = JsonUtility.ToJson(history, true);
        File.WriteAllText(_historyPath, json);

        Debug.Log($"[DashboardReceiver] Saved session #{history.sessions.Count}");
    }

    public SessionHistory LoadHistory()
    {
        if (!File.Exists(_historyPath))
            return new SessionHistory();

        try
        {
            string json = File.ReadAllText(_historyPath);
            SessionHistory history = JsonUtility.FromJson<SessionHistory>(json);
            return history ?? new SessionHistory();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DashboardReceiver] Error reading history: {e.Message}");
            return new SessionHistory();
        }
    }

    public void LoadAndUpdateDisplay()
    {
        SessionHistory history = LoadHistory();

        if (history.sessions.Count == 0)
            return;

        SessionEntry latest = history.sessions[history.sessions.Count - 1];

        // ── Focus score ──
        if (focusScoreText != null)
            focusScoreText.text = Mathf.RoundToInt(latest.score).ToString();

        // ── Character name ──
        if (flowerTitleText != null)
            flowerTitleText.text = latest.charactername;

        // ── Rank ──
        List<float> allScores = new List<float>();
        foreach (SessionEntry entry in history.sessions)
            allScores.Add(entry.score);

        allScores.Sort();
        allScores.Reverse();

        int rank = 1;
        for (int i = 0; i < allScores.Count; i++)
        {
            if (Mathf.Approximately(allScores[i], latest.score))
            {
                rank = i + 1;
                break;
            }
        }

        if (rankText != null)
            rankText.text = $"#{rank}";

        if (outOfText != null)
            outOfText.text = $"Out of {history.sessions.Count}";

        // ── Recovery time ──
        if (recoveryTimeText != null)
        {
            if (latest.recovery_count > 0)
                recoveryTimeText.text = $"{latest.avg_recovery_time:F1}s";
            else
                recoveryTimeText.text = "N/A";
        }

        // ── Breakdown percentages ──
        if (focusedPctText != null)
            focusedPctText.text = $"{Mathf.RoundToInt(latest.focused_pct)}%";

        if (distractedPctText != null)
            distractedPctText.text = $"{Mathf.RoundToInt(latest.distracted_pct)}%";

        // ── Focus consistency ──
        if (focusConsistencyText != null)
            focusConsistencyText.text = $"{Mathf.RoundToInt(latest.focus_consistency)}";

        // ── Pie chart ──
        UpdatePieChart(latest.focused_pct, latest.distracted_pct);

        // ── Leaderboard ──
        UpdateLeaderboard(history);

        Debug.Log($"[DashboardReceiver] Display updated — Rank: #{rank} out of {history.sessions.Count}");
    }

    // ══════════════════════════════════════════════
    //  PIE CHART (XCharts)
    // ══════════════════════════════════════════════

    private void UpdatePieChart(float focusedPct, float distractedPct)
    {
        if (pieChart == null)
            return;

        // Get the first serie (the pie data)
        var serie = pieChart.GetSerie(0);
        if (serie == null)
        {
            Debug.LogWarning("[DashboardReceiver] PieChart has no serie. Add one in the Inspector.");
            return;
        }

        // Clear existing data and add our two slices
        serie.ClearData();
        serie.AddData(new List<double> { (double)focusedPct }, "Focused");
        serie.AddData(new List<double> { (double)distractedPct }, "Distracted");

        // Set colors: green for Focused, red for Distracted
        var focusedData = serie.GetSerieData(0);
        var distractedData = serie.GetSerieData(1);
        if (focusedData != null)
        {
            var itemStyle = focusedData.EnsureComponent<XCharts.Runtime.ItemStyle>();
            itemStyle.color = new Color32(76, 204, 76, 255);
        }
        if (distractedData != null)
        {
            var itemStyle = distractedData.EnsureComponent<XCharts.Runtime.ItemStyle>();
            itemStyle.color = new Color32(217, 76, 76, 255);
        }

        // Refresh the chart
        pieChart.RefreshChart();
    }

    // ══════════════════════════════════════════════
    //  LEADERBOARD
    // ══════════════════════════════════════════════

    private void UpdateLeaderboard(SessionHistory history)
    {
        if (leaderboardContent == null || leaderboardRowPrefab == null)
            return;

        // Clear old rows (skip non-clone children like the title)
        foreach (Transform child in leaderboardContent)
        {
            if (child.name.Contains("LeaderboardRow"))
                Destroy(child.gameObject);
        }

        // Sort sessions by score (highest first)
        List<SessionEntry> sorted = new List<SessionEntry>(history.sessions);
        sorted.Sort((a, b) => b.score.CompareTo(a.score));

        // Find the current player's name
        string currentPlayer = "";
        if (GameFlowManager.Instance != null)
            currentPlayer = GameFlowManager.Instance.GetCurrentSessionName();

        // Create a row for each session
        for (int i = 0; i < sorted.Count; i++)
        {
            GameObject row = Instantiate(leaderboardRowPrefab, leaderboardContent);
            TMP_Text[] texts = row.GetComponentsInChildren<TMP_Text>();

            if (texts.Length >= 3)
            {
                // Rank number (no "Rank" prefix — cleaner like your mockup)
                texts[0].text = $"{i + 1}";
                texts[1].text = sorted[i].charactername;
                texts[2].text = $"{Mathf.RoundToInt(sorted[i].score)}";

                // Trophy/spacer after rank number (index 1, between rank and name)
                if (trophySprite != null)
                {
                    GameObject trophyObj = new GameObject("Trophy");
                    trophyObj.transform.SetParent(row.transform, false);
                    trophyObj.transform.SetSiblingIndex(1); // after RankText (0), before NameText

                    Image trophyImg = trophyObj.AddComponent<Image>();
                    trophyImg.preserveAspect = true;

                    LayoutElement trophyLayout = trophyObj.AddComponent<LayoutElement>();
                    trophyLayout.minWidth = 30;
                    trophyLayout.minHeight = 30;
                    trophyLayout.preferredWidth = 30;
                    trophyLayout.preferredHeight = 30;

                    if (i < 3)
                    {
                        trophyImg.sprite = trophySprite;
                    }
                    else
                    {
                        trophyImg.color = new Color(0, 0, 0, 0);
                    }
                }

                // Highlight current player's row in green
                if (sorted[i].charactername == currentPlayer &&
                    sorted[i] == history.sessions[history.sessions.Count - 1])
                {
                    Image rowBg = row.GetComponent<Image>();
                    if (rowBg != null)
                        rowBg.color = new Color(0.2f, 0.8f, 0.4f, 0.8f); // green highlight
                }
            }
        }
    }

    // ══════════════════════════════════════════════
    //  TIMELINE BARS
    // ══════════════════════════════════════════════

    private void UpdateTimelineBars()
    {
        if (timelineBars == null || timelineBars.Length == 0)
            return;

        Color greenColor = new Color(0.3f, 0.8f, 0.3f, 1f);  // focused green
        Color redColor = new Color(0.85f, 0.3f, 0.3f, 1f);    // distracted red
        Color grayColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);  // no data

        for (int i = 0; i < timelineBars.Length && i < _latestTimelineBars.Length; i++)
        {
            if (timelineBars[i] == null) continue;

            string barState = _latestTimelineBars[i];
            if (barState == "green")
                timelineBars[i].color = greenColor;
            else if (barState == "red")
                timelineBars[i].color = redColor;
            else
                timelineBars[i].color = grayColor;
        }
    }

    // ══════════════════════════════════════════════
    //  NETWORK — background thread
    // ══════════════════════════════════════════════

    private void ReceiveLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remoteEP);
                string json = Encoding.UTF8.GetString(data);

                if (json.Contains("\"type\": \"session_complete\"") ||
                    json.Contains("\"type\":\"session_complete\""))
                {
                    float score = ExtractFloat(json, "score");
                    string state = ExtractString(json, "state");
                    float avgRecovery = ExtractFloat(json, "avg_recovery_time");
                    int recoveryCount = Mathf.RoundToInt(ExtractFloat(json, "recovery_count"));
                    float focusedPct = ExtractFloat(json, "focused_pct");
                    float distractedPct = ExtractFloat(json, "distracted_pct");
                    float focusConsistency = ExtractFloat(json, "focus_consistency");
                    string dropTimesStr = ExtractString(json, "drop_times_str");
                    string[] bars = ExtractStringArray(json, "timeline_bars");

                    lock (_lock)
                    {
                        _latestScore = score;
                        _latestState = state;
                        _latestAvgRecovery = avgRecovery;
                        _latestRecoveryCount = recoveryCount;
                        _latestFocusedPct = focusedPct;
                        _latestDistractedPct = distractedPct;
                        _latestFocusConsistency = focusConsistency;
                        _latestDropTimesStr = dropTimesStr;
                        if (bars != null) _latestTimelineBars = bars;
                        _hasNewData = true;
                    }
                }
                else if (json.Contains("\"type\": \"score\"") ||
                         json.Contains("\"type\":\"score\""))
                {
                    lock (_lock)
                    {
                        _liveScore = ExtractFloat(json, "score");
                        _liveState = ExtractString(json, "state");
                        _hasNewLiveScore = true;

                        // Also update GameFlowManager's latestFocusScore
                        // so the lotus bloom uses real EEG data
                        if (GameFlowManager.Instance != null)
                            GameFlowManager.Instance.SetLiveFocusScore(_liveScore);
                    }
                }
            }
            catch (SocketException) { }
            catch (Exception e)
            {
                Debug.LogWarning($"[DashboardReceiver] Error: {e.Message}");
            }
        }
    }

    // ══════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════

    public float GetScore()
    {
        lock (_lock) { return _latestScore; }
    }

    public string GetState()
    {
        lock (_lock) { return _latestState; }
    }

    // ══════════════════════════════════════════════
    //  JSON HELPERS
    // ══════════════════════════════════════════════

    private float ExtractFloat(string json, string key)
    {
        string pattern = $"\"{key}\": ";
        string pattern2 = $"\"{key}\":";
        int idx = json.IndexOf(pattern);
        if (idx < 0) idx = json.IndexOf(pattern2);
        if (idx < 0) return 0f;

        int start = json.IndexOf(':', idx) + 1;
        int end = json.IndexOfAny(new char[] { ',', '}' }, start);
        string val = json.Substring(start, end - start).Trim();
        float.TryParse(val, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float result);
        return result;
    }

    private string ExtractString(string json, string key)
    {
        string pattern = $"\"{key}\": \"";
        string pattern2 = $"\"{key}\":\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) idx = json.IndexOf(pattern2);
        if (idx < 0) return "";

        int start = json.IndexOf('"', json.IndexOf(':', idx) + 1) + 1;
        int end = json.IndexOf('"', start);
        return json.Substring(start, end - start);
    }

    private string[] ExtractStringArray(string json, string key)
    {
        // Find "timeline_bars": ["green", "red", ...]
        string pattern = $"\"{key}\"";
        int idx = json.IndexOf(pattern);
        if (idx < 0) return null;

        int bracketStart = json.IndexOf('[', idx);
        int bracketEnd = json.IndexOf(']', bracketStart);
        if (bracketStart < 0 || bracketEnd < 0) return null;

        string arrayStr = json.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
        string[] items = arrayStr.Split(',');
        string[] result = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            result[i] = items[i].Trim().Trim('"').Trim();
        }
        return result;
    }

    // ══════════════════════════════════════════════
    //  DATA CLASSES
    // ══════════════════════════════════════════════

    [Serializable]
    public class SessionEntry
    {
        public string charactername;
        public float score;
        public string state;
        public float avg_recovery_time;
        public int recovery_count;
        public float focused_pct;
        public float distracted_pct;
        public float focus_consistency;
        public string timestamp;
    }

    [Serializable]
    public class SessionHistory
    {
        public List<SessionEntry> sessions = new List<SessionEntry>();
    }
}