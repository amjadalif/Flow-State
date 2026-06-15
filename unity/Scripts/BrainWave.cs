using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class BrainWave : MonoBehaviour
{
    [Header("Channel")]
    public string channelName = "TP9";

    [Header("Wave Display")]
    public float waveWidth = 13f;
    public float amplitude = 1.0f;
    public float maxHeight = 0.8f;
    public Color waveColor = Color.black;
    public float lineWidth = 0.05f;

    [Header("Focus Score (enable on ONE wave object only)")]
    public bool isScoreListener = false;
    public TextMesh scoreText;
    public int listenPort = 5009;

    private LineRenderer _lr;
    private const int PointCount = 200;
    private float[] _displayBuffer;
    private int _writePos = 0;
    private bool _bufferFilled = false;

    private static UdpClient _udpClient;
    private static Thread _receiveThread;
    private static bool _running = false;
    private static bool _udpStarted = false;
    private static readonly object _lock = new object();
    private static Dictionary<string, float[]> _channelData = new Dictionary<string, float[]>();
    private static float _latestScore = 0f;
    private static string _latestState = "WAITING";
    private static bool _hasNewScore = false;
    private static float _lastDataTime = 0f;

    // ── Lifecycle ──

    void Start()
    {
        InitLineRenderer();
        _displayBuffer = new float[PointCount];
        DrawFlatLine();

        if (isScoreListener)
        {
            InitScoreText();
            if (!_udpStarted) StartUDP();
        }
    }

    void Update()
    {
        ConsumeIncomingData();

        if (_lastDataTime > 0f && (Time.time - _lastDataTime) < 2f)
            DrawRealData();
        else
            DrawFlatLine();
    }

    void OnDestroy()
    {
        if (!isScoreListener) return;
        _running = false;
        _udpStarted = false;
        if (_udpClient != null) _udpClient.Close();
        if (_receiveThread != null && _receiveThread.IsAlive) _receiveThread.Abort();
    }

    // ── Initialisation ──

    private void InitLineRenderer()
    {
        _lr = GetComponent<LineRenderer>();
        _lr.positionCount = PointCount;
        _lr.startWidth = lineWidth;
        _lr.endWidth = lineWidth;
        _lr.useWorldSpace = false;
        _lr.material = new Material(Shader.Find("Sprites/Default"));
        _lr.startColor = waveColor;
        _lr.endColor = waveColor;
    }

    private void InitScoreText()
    {
        if (scoreText != null)
        {
            scoreText.text = "Waiting for data...";
            scoreText.color = Color.gray;
        }
        else
        {
            Debug.LogError("BrainWave: Score Text not assigned!");
        }
    }

    // ── Data Consumption ──

    private void ConsumeIncomingData()
    {
        lock (_lock)
        {
            if (_channelData.ContainsKey(channelName))
            {
                PushSamples(_channelData[channelName]);
                _channelData.Remove(channelName);
                _lastDataTime = Time.time;
            }

            if (isScoreListener && scoreText != null && _hasNewScore)
            {
                UpdateScoreText();
                _hasNewScore = false;
            }
        }
    }

    private void PushSamples(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            _displayBuffer[_writePos] = samples[i];
            _writePos = (_writePos + 1) % PointCount;
            if (_writePos == 0) _bufferFilled = true;
        }
    }

    // ── Drawing ──

    private void DrawFlatLine()
    {
        for (int i = 0; i < PointCount; i++)
            _lr.SetPosition(i, new Vector3(XAt(i), 0f, 0f));
    }

    private void DrawRealData()
    {
        int readPos = _bufferFilled ? _writePos : 0;
        int count = _bufferFilled ? PointCount : _writePos;

        for (int i = 0; i < PointCount; i++)
        {
            float y = 0f;
            if (i < count)
            {
                int idx = (readPos + i) % PointCount;
                y = Mathf.Clamp(_displayBuffer[idx] * amplitude * 0.01f, -maxHeight, maxHeight);
            }
            _lr.SetPosition(i, new Vector3(XAt(i), y, 0f));
        }
    }

    private float XAt(int index)
    {
        return ((float)index / (PointCount - 1)) * waveWidth - (waveWidth / 2f);
    }

    // ── Score Text ──

    private void UpdateScoreText()
    {
        scoreText.text = $"Focus: {_latestScore:F1}/100  {_latestState}";

        scoreText.color = _latestState switch
        {
            "DISTRACTED" => Color.red,
            "NEUTRAL"    => Color.yellow,
            "FOCUSED"    => Color.green,
            _            => Color.gray
        };
    }

    // ── UDP Receiver ──

    private void StartUDP()
    {
        _running = true;
        _udpStarted = true;
        _udpClient = new UdpClient(listenPort);
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
        Debug.Log($"BrainWave: listening on UDP port {listenPort}");
    }

    private static void ReceiveLoop()
    {
        IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _udpClient.Receive(ref remoteEP);
                ParsePacket(Encoding.UTF8.GetString(data));
            }
            catch (SocketException) { }
            catch (Exception) { }
        }
    }

    private static void ParsePacket(string json)
    {
        try
        {
            if (json.Contains("\"type\":\"eeg\"") || json.Contains("\"type\": \"eeg\""))
            {
                lock (_lock)
                {
                    foreach (string ch in new[] { "TP9", "AF7", "AF8", "TP10" })
                    {
                        float[] values = ParseArray(json, ch);
                        if (values != null) _channelData[ch] = values;
                    }
                }
            }
            else if (json.Contains("\"type\":\"score\"") || json.Contains("\"type\": \"score\""))
            {
                lock (_lock)
                {
                    _latestScore = ParseFloat(json, "score");
                    _latestState = ParseString(json, "state");
                    _hasNewScore = true;
                }
            }
        }
        catch (Exception) { }
    }

    // ── JSON Helpers ──

    private static int FindKey(string json, string key)
    {
        int idx = json.IndexOf($"\"{key}\": ");
        if (idx < 0) idx = json.IndexOf($"\"{key}\":");
        return idx;
    }

    private static float ParseFloat(string json, string key)
    {
        int idx = FindKey(json, key);
        if (idx < 0) return 0f;

        int start = json.IndexOf(':', idx) + 1;
        int end = json.IndexOfAny(new[] { ',', '}' }, start);
        float.TryParse(json.Substring(start, end - start).Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float result);
        return result;
    }

    private static string ParseString(string json, string key)
    {
        int idx = FindKey(json, key);
        if (idx < 0) return "";

        int start = json.IndexOf('"', json.IndexOf(':', idx) + 1) + 1;
        int end = json.IndexOf('"', start);
        return json.Substring(start, end - start);
    }

    private static float[] ParseArray(string json, string key)
    {
        int idx = FindKey(json, key);
        if (idx < 0) return null;

        int start = json.IndexOf('[', idx) + 1;
        int end = json.IndexOf(']', start);
        if (end < 0) return null;

        string[] parts = json.Substring(start, end - start).Split(',');
        float[] result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            float.TryParse(parts[i].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result[i]);
        }
        return result;
    }
}
