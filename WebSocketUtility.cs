// WebSocketUtility
// Shepherd Zhu
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using SimpleJSON;
using UnityEngine;

public class WebSocketUtility
{
    public Uri Uri;
    public ClientWebSocket ws;
    public Coroutine HeartbeatCoroutine;
    public Coroutine ReceiveDataCoroutine;
    private List<string> m_Datalist = new List<string>();
    public Action<string> OnReceiveJson;

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

    public WebSocketUtility(Uri address)
    {
        Uri = address;
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
        }

        ws = new ClientWebSocket();
        ws.Options.AddSubProtocol($"access_token, {PlayerPrefs.GetString("localJwt")}");

        OverwatchLog.Log($"[{GetType()}.Connect] Establishing connection to server......");
        try
        {
            await ws.ConnectAsync(Uri, CancellationToken.None);
        }
        catch (Exception ex)
        {
            OverwatchLog.Error($"[{GetType()}.Connect] Exception on ws.ConnectAsync.\n{ex}");
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

        HeartbeatCoroutine = HttpManager.Instance.StartCoroutine(Heartbeat());
        ReceiveDataCoroutine = HttpManager.Instance.StartCoroutine(ReceiveData());
    }

    public void Disconnect()
    {
        ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", default);
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

                m_Datalist.Add(data);
                try
                {
                    // 检查是否为有效json，然后再下放广播
                    var js = new JsonSerializer();
                    js.Deserialize(
                        new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(data))
                    );

					// Obsolete
                    // 这块也需要用try catch，以防有哈皮自己写的OnReceiveJson报空导致本脚本跟着一起寄。
                    // try
                    // {
                    //     OnReceiveJson.Invoke(data);
                    // }
                    // catch (Exception ex)
                    // {
                    //     OverwatchLog.Error(
                    //         $"[{GetType()}.ReceiveData] Error when invoke OnReceiveJson.\n{ex}"
                    //     );
                    // }

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

    IEnumerator Heartbeat()
    {
        int retryCount = 0;
        while (ws is { State: WebSocketState.Open })
        {
            OverwatchLog.Log($"[{GetType()}.Heartbeat] Ping!");
            Send("ping");

            int index = Mathf.Max(0, m_Datalist.Count - 1);
            bool hasPong = false;
            yield return new WaitForSecondsRealtime(5.0f);
            // 检查最新列表的最后一项到index之间的数据是否存在"pong"
            for (int i = index + 1; i < m_Datalist.Count; i++)
            {
                if (m_Datalist[i] == "pong")
                {
                    hasPong = true;
                    OverwatchLog.Log($"[{GetType()}.Heartbeat] Pong detected in m_Datalist!");
                    break; // 存在"pong"，退出循环
                }
            }

            if (!hasPong)
                retryCount++;

            if (retryCount > 4)
            {
                // 5次都没回应，铁定寄了，抓紧主动重连。
                OverwatchLog.Error(
                    $"[{GetType()}.Heartbeat] It looks like server no response after tried 5 times. Gotta reconnect!"
                );

                Connect();
                yield break;
            }
        }

        OverwatchLog.Error(
            $"[{GetType()}.Heartbeat] Connection {ws.State}! \n{ws.CloseStatusDescription}"
        );

        Connect();
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
