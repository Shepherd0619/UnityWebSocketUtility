// WebSocketUtility
// Shepherd Zhu
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Biozone.Networking;
using Newtonsoft.Json;
using SimpleJSON;
using UnityEngine;

public class WebSocketUtility
{
    public Uri Uri;
    public string Token;
    public ClientWebSocket ws;
    public Coroutine HeartbeatCoroutine;
    public Coroutine ReceiveDataCoroutine;
    public Coroutine CheckWebSocketCoroutine;
    private List<string> m_Datalist = new List<string>();
    private bool m_StartCacheWebSocketData = false;
    public Action OnConnected;
    public Action OnDisconnected;

    public bool Reconnect = true;

    // 按照协议头分类回调
    private readonly Dictionary<string, Action<string>> _protocolCallbacks =
        new Dictionary<string, Action<string>>();

    /// <summary>
    /// 注册回调
    /// </summary>
    /// <param name="protocol">协议头</param>
    /// <param name="callback"></param>
    public void RegisterProtocolCallback(string protocol, Action<string> callback)
    {
        if (!_protocolCallbacks.TryAdd(protocol, callback))
        {
            _protocolCallbacks[protocol] += callback;
        }

        OverwatchLog.Log($"[{GetType()}.RegisterProtocolCallback] protocol is {protocol}");
    }

    /// <summary>
    /// 调用回调
    /// </summary>
    /// <param name="protocol">协议头</param>
    /// <param name="data">数据</param>
    public void InvokeProtocolCallback(string protocol, string data)
    {
        if (_protocolCallbacks.TryGetValue(protocol, out var callback))
        {
            OverwatchLog.Log(
                $"[{GetType()}.InvokeProtocolCallback] Invoke! protocol is {protocol}"
            );
            try
            {
                callback?.Invoke(data);
            }
            catch (Exception ex)
            {
                OverwatchLog.Error($"[{GetType()}.InvokeProtocolCallback] ERROR! {ex}");
            }
        }
    }

    public WebSocketUtility(Uri address, string token, bool reconnect = true)
    {
        Uri = address;
        Token = token;
        Reconnect = reconnect;
        Connect();
    }

    public async void Connect()
    {
        if (!Application.isPlaying)
            return;

        ws?.Dispose();

        if (HeartbeatCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(HeartbeatCoroutine);

            HeartbeatCoroutine = null;
        }

        if (ReceiveDataCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(ReceiveDataCoroutine);

            ReceiveDataCoroutine = null;
        }

        if (CheckWebSocketCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(CheckWebSocketCoroutine);

            CheckWebSocketCoroutine = null;
        }

        ws = new ClientWebSocket();
        //ws.Options.AddSubProtocol($"access_token, {Token}");
        ws.Options.SetRequestHeader("Authorization", Token);

        OverwatchLog.Log($"[{GetType()}.Connect] Establishing connection to server......");
        try
        {
            await ws.ConnectAsync(Uri, CancellationToken.None);
        }
        catch (Exception ex)
        {
            OverwatchLog.Error($"[{GetType()}.Connect] Exception on ws.ConnectAsync.\n{ex}");
            OnDisconnected?.Invoke();
            Connect();
        }

        if (ws.State != WebSocketState.Open)
        {
            OverwatchLog.Error(
                $"[{GetType()}.Connect] Failed to connect! \n{ws.CloseStatusDescription}"
            );
            if (ws.CloseStatus != WebSocketCloseStatus.NormalClosure)
                Connect();
            return;
        }

        OverwatchLog.Log($"[{GetType()}.Connect] Connected!");

        try
        {
            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            OverwatchLog.Error($"[{GetType()}.Connect] ERROR when invoke OnConnected. {ex}");
        }

        HeartbeatCoroutine = HttpManager.Instance.StartCoroutine(Heartbeat());
        ReceiveDataCoroutine = HttpManager.Instance.StartCoroutine(ReceiveData());
        CheckWebSocketCoroutine = HttpManager.Instance.StartCoroutine(CheckWebSocketStatus());
    }

    public void Disconnect()
    {
        try
        {
            ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", default);
        }
        catch
        {
            ws?.Abort();
        }

        try
        {
            OnDisconnected?.Invoke();
        }
        catch (Exception ex)
        {
            OverwatchLog.Error($"[{GetType()}.Disconnect] ERROR when invoke OnDisconnected. {ex}");
        }

        ws?.Dispose();
        m_StartCacheWebSocketData = false;
        m_Datalist.Clear();

        if (HeartbeatCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(HeartbeatCoroutine);

            HeartbeatCoroutine = null;
        }

        if (ReceiveDataCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(ReceiveDataCoroutine);

            ReceiveDataCoroutine = null;
        }

        if (CheckWebSocketCoroutine != null)
        {
            HttpManager.Instance.StopCoroutine(CheckWebSocketCoroutine);

            CheckWebSocketCoroutine = null;
        }
    }

    public void Send(string message)
    {
        if (ws == null)
            return;

        if (ws.State == WebSocketState.Closed)
        {
            OverwatchLog.Error(
                $"[{GetType()}.Send] ERROR: Websocket connection is closed when sending {message}!"
            );
            return;
        }

        ws.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
    }

    protected IEnumerator ReceiveData()
    {
        while (ws is { State: WebSocketState.Open })
        {
            byte[] buffer = new byte[1024 * 4];
            var cancel = new CancellationTokenSource();
            var task = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token);
            yield return new WaitUntil(() => task.IsCompleted || task.IsCanceled);
            if (task.IsCompleted && !task.IsCanceled)
            {
                string data = ReturnCleanASCII(Encoding.UTF8.GetString(buffer));
                if (string.IsNullOrWhiteSpace(data))
                    continue;

                if(m_StartCacheWebSocketData)
                    m_Datalist.Add(data);
                
                try
                {
                    // 检查是否为有效json，然后再下放广播
                    var js = new JsonSerializer();
                    js.Deserialize(
                        new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(data))
                    );

                    var action = JSONNode.Parse(data)["action"];
                    // 按照协议头Invoke回调
                    InvokeProtocolCallback(action, data);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    protected IEnumerator Heartbeat()
    {
        int retryCount = 0;
        while (ws is { State: WebSocketState.Open })
        {
            OverwatchLog.Log($"[{GetType()}.Heartbeat] Ping!");
            Send("ping");
            m_StartCacheWebSocketData = true;
            bool hasPong = false;
            yield return new WaitForSecondsRealtime(5.0f);
            hasPong = m_Datalist.Contains("pong");
            if (hasPong)
            {
                m_StartCacheWebSocketData = false;
                m_Datalist.Clear();
                OverwatchLog.Log($"[{GetType()}.Heartbeat] Pong detected in m_Datalist!");
                continue;
            }

            retryCount++;

            m_StartCacheWebSocketData = false;
			m_Datalist.Clear();

            if (retryCount > 4)
            {
                // 5次都没回应，铁定寄了，抓紧主动重连。
                OverwatchLog.Error(
                    $"[{GetType()}.Heartbeat] It looks like server no response after tried 5 times."
                );

                if (Reconnect)
                {
                    Disconnect();
                    Connect();
                }

                yield break;
            }
        }
    }

    protected IEnumerator CheckWebSocketStatus()
    {
        while (ws is { State: WebSocketState.Open })
        {
            yield return new WaitForSecondsRealtime(1.0f);
        }

        if (Reconnect)
        {
            Disconnect();
            Connect();
        }
    }

    /// <summary>
    /// 去掉不可见的Unicode控制符
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    public string ReturnCleanASCII(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            //if ((int)c > 127) // you probably don't want 127 either
            //	continue;
            if ((int)c < 32) // I bet you don't want control characters
                continue;
            if (c == '%')
                continue;
            if (c == '?')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
