# UnityWebSocketUtility
A script serves as template of WebSocket in Unity.

WebSocketUtility is a utility class written in C# for Unity that provides functionality for establishing and managing WebSocket connections. It allows sending and receiving messages over the WebSocket protocol.

## Features

- Connect to a WebSocket server using a specified URI.
- Send messages to the server.
- Receive messages from the server.
- Handle received JSON data.
- Implement heartbeat functionality to check the connection status.

## Usage

1. Create an instance of the WebSocketUtility class by providing the WebSocket server URI.

```csharp
Uri serverUri = new Uri("wss://example.com");
WebSocketUtility wsUtility = new WebSocketUtility(serverUri);
```

2. Connect to the WebSocket server.

```csharp
wsUtility.Connect();
```

3. Send messages to the server.

```csharp
string message = "Hello, server!";
wsUtility.Send(message);
```

4. Handle received JSON data by subscribing to the `OnReceiveJson` event.

```csharp
wsUtility.OnReceiveJson += HandleReceivedJson;

private void HandleReceivedJson(string jsonData)
{
    // Process the received JSON data
}
```

5. Disconnect from the WebSocket server.

```csharp
wsUtility.Disconnect();
```

## Dependencies

- UnityEngine
- System.Net.WebSockets
- System.Text
- System.Threading
- Newtonsoft.Json
- [OverwatchLogger](https://github.com/Shepherd0619/OverwatchUnityLogger)

## Tips
At line 34, I used Sec-WebSocket-Protocol to send the JWT token to server for authentication. You should change it according to your scenario.