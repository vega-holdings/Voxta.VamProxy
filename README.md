# Voxta VaM Proxy

A WebSocket proxy that enables Virt-A-Mate (VaM) to communicate with a remote Voxta server. This solves the fundamental limitation that VaM's Voxta plugin can only work with local Voxta installations due to its reliance on local file system access for audio playback.

## The Problem

VaM's Voxta plugin has two critical limitations when trying to use a remote Voxta server:

1. **Audio Output**: VaM cannot fetch audio files over HTTP/WebSocket. It expects audio files to exist on the local filesystem at a path like `E:\VAM\Custom\Sounds\Voxta\`. When VaM authenticates with Voxta, it sends `audioOutput: "LocalFile"` with an `audioFolder` path, and Voxta writes audio directly to that folder. This doesn't work when Voxta runs on a different machine.

2. **Audio Input (Microphone)**: VaM's plugin doesn't implement microphone streaming. When Voxta sends a `recordingRequest` message, VaM responds with "Recording request received but VaM does not support recording." The plugin expects the user to handle speech-to-text through other means.

## The Solution

This proxy sits between VaM and a remote Voxta server, handling both problems transparently:

```
┌─────────┐         ┌─────────────────┐         ┌──────────────────┐
│   VaM   │ ◄─────► │  Voxta.VamProxy │ ◄─────► │  Remote Voxta    │
│ (local) │  WS     │    (local)      │   WS    │  (Linux/remote)  │
└─────────┘         └─────────────────┘         └──────────────────┘
                            │
                            ▼
                    ┌───────────────┐
                    │ Local Audio   │
                    │ Folder (VaM)  │
                    └───────────────┘
```

### Audio Output Flow

1. VaM connects to proxy with `audioOutput: "LocalFile"` and `audioFolder: "E:\VAM\Custom\Sounds\Voxta"`
2. Proxy intercepts authentication and changes to `audioOutput: "Url"` when forwarding to remote Voxta
3. Remote Voxta generates speech and returns audio URLs (e.g., `/api/audio/12345.wav`)
4. Proxy downloads audio files from remote Voxta via HTTP
5. Proxy saves files locally to VaM's audio folder
6. Proxy rewrites the `audioUrl` in messages to the local file path
7. VaM receives local file paths and plays audio normally

### Audio Input Flow (Microphone)

1. When a chat starts, proxy captures the `sessionId` from the `chatStarted` message
2. Proxy opens a WebSocket connection to remote Voxta's audio input endpoint: `ws://{host}/ws/audio/input/stream?sessionId={sessionId}`
3. Proxy starts capturing audio from the local microphone using NAudio (16kHz, mono, 16-bit PCM)
4. When Voxta sends `recordingRequest: enabled=true`, proxy starts streaming mic data
5. When Voxta sends `recordingRequest: enabled=false`, proxy stops streaming
6. The `recordingRequest` messages are still forwarded to VaM (which ignores them)

## Requirements

- Windows (for NAudio microphone capture)
- .NET 8.0 Runtime
- A microphone for speech input
- VaM with the Voxta plugin installed
- A remote Voxta server (can be Linux, Windows, or any platform)

## Installation

1. Build the project:
   ```bash
   dotnet build --configuration Release
   ```

2. Or publish as a self-contained executable:
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained
   ```

## Configuration

Edit `appsettings.json`:

```json
{
  "Proxy": {
    "ListenPort": 5385,
    "RemoteVoxtaUrl": "ws://192.168.1.160:5384/hub",
    "LocalAudioFolder": "E:\\VAM\\Custom\\Sounds\\Voxta",
    "CleanupAudioAfterSeconds": 8640
  },
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

### Configuration Options

| Option | Description | Default |
|--------|-------------|---------|
| `ListenPort` | Port the proxy listens on for VaM connections | `5385` |
| `RemoteVoxtaUrl` | WebSocket URL to the remote Voxta server's SignalR hub | `ws://127.0.0.1:5384/hub` |
| `LocalAudioFolder` | Local folder where audio files are saved for VaM | `E:\VAM\Custom\Sounds\Voxta` |
| `CleanupAudioAfterSeconds` | How long to keep downloaded audio files before cleanup | `8640` (2.4 hours) |

## Usage

1. **Start the proxy:**
   ```bash
   dotnet run --project Voxta.VamProxy
   ```

   Or run the built executable:
   ```bash
   ./bin/Release/net8.0-windows/Voxta.VamProxy.exe
   ```

2. **Configure VaM's Voxta plugin:**
   - Host: `127.0.0.1`
   - Port: `5385` (or whatever `ListenPort` you configured)

3. **Start a chat in VaM** - the proxy will handle everything automatically.

## Console Output

When running, you'll see detailed logs:

```
╔════════════════════════════════════════════════════════════╗
║           Voxta VaM Proxy - Remote Audio Bridge            ║
╚════════════════════════════════════════════════════════════╝

  Local listen port:    http://0.0.0.0:5385
  Remote Voxta server:  ws://192.168.1.160:5384/hub
  Local audio folder:   E:\VAM\Custom\Sounds\Voxta
  Audio cleanup after:  8640s

  Configure VaM Voxta plugin to connect to:
    Host: 127.0.0.1    Port: 5385

  Press Ctrl+C to exit.
════════════════════════════════════════════════════════════════

info: VaM client connecting...
info: Connecting to remote Voxta at ws://192.168.1.160:5384/hub
info: Connected to remote Voxta
info: VaM wants audio at: E:\VAM\Custom\Sounds\Voxta
info: Modified auth: audioOutput LocalFile → Url
info: Chat started with sessionId: 81e1325f-830c-415a-b64d-b6150d69ec7f
info: Available audio input devices (3):
info:   [0] Microphone (Realtek Audio)
info:   [1] Headset Microphone (HyperX)
info:   [2] Stereo Mix (Realtek Audio)
info: Connecting mic stream to ws://192.168.1.160:5384/ws/audio/input/stream?sessionId=81e1325f-830c-415a-b64d-b6150d69ec7f
info: WebSocket connected to Voxta audio input
info: Sending audio specs: {"sampleRate":16000,"channels":1,"bufferMilliseconds":30,"bitsPerSample":16}
info: NAudio recording started on device 0 (16kHz mono)
info: Recording requested: START
dbug: Mic streaming: 96000 bytes sent total
dbug: Downloaded audio: http://192.168.1.160:5384/api/audio/abc123.wav → E:\VAM\Custom\Sounds\Voxta\voxta_def456.wav
info: Recording requested: STOP
```

## How It Works - Technical Details

### SignalR Protocol Handling

The proxy parses SignalR messages (JSON with `0x1e` delimiter) and handles:

- **Type 1 (Invocation)**: Normal messages with `arguments` array containing Voxta protocol messages
- **Type 6 (Ping)**: Keep-alive messages, passed through unchanged
- **Type 7 (Close)**: Connection close messages, passed through unchanged

### Message Types Intercepted

**VaM → Voxta (modified):**
- `authenticate`: Capabilities modified from `audioOutput: "LocalFile"` to `audioOutput: "Url"`, and `audioInput: "WebSocketStream"` is added

**Voxta → VaM (modified):**
- `replyChunk`: `audioUrl` field rewritten from HTTP URL to local file path
- `replyGenerating`: `thinkingSpeechUrl` field rewritten similarly

**Voxta → VaM (handled by proxy):**
- `chatStarted`: SessionId extracted to establish mic streaming connection
- `recordingRequest`: Controls when the proxy streams microphone audio

### Audio Specifications

Microphone audio is streamed with these specifications (matching Voxta's expectations):
- Sample Rate: 16000 Hz
- Channels: 1 (mono)
- Bits per Sample: 16
- Buffer: 30ms chunks

### File Cleanup

Downloaded audio files are automatically cleaned up after `CleanupAudioAfterSeconds` to prevent disk space accumulation. The cleanup runs every 30 seconds.

## Troubleshooting

### "No audio input devices found!"
- Ensure you have a microphone connected and recognized by Windows
- Check Windows Sound settings → Recording devices

### Microphone not working / No speech recognition
- Check that the correct microphone is device 0 (the default)
- To use a different mic, modify the `DeviceNumber` in the code
- Verify the remote Voxta server has STT configured (e.g., Deepgram, Whisper)

### Audio files not playing in VaM
- Verify the `LocalAudioFolder` path matches what VaM expects
- Check that the folder exists and is writable
- Look for downloaded `.wav` files in the folder

### Connection errors
- Verify the `RemoteVoxtaUrl` is correct and reachable
- Ensure the remote Voxta server is running
- Check firewall settings on both machines

### "Chat started but no sessionId found"
- This indicates the Voxta server didn't include a sessionId in the chatStarted message
- Ensure you're using a compatible Voxta version

## Limitations

- **Windows only**: Uses NAudio's WaveInEvent which requires Windows audio APIs
- **Single microphone**: Uses device 0 by default; no UI to select mic
- **No authentication passthrough**: Assumes Voxta authentication is disabled or handled separately

## Dependencies

- **Microsoft.AspNetCore.WebSockets** (2.3.0): WebSocket server support
- **NAudio.WinMM** (2.2.1): Windows microphone capture

## License

This project is provided as-is for use with Voxta and Virt-A-Mate.

## See Also

- [Voxta](https://voxta.ai/) - AI-powered conversational characters
- [Virt-A-Mate](https://www.patreon.com/meshedvr) - VR character simulation
- [Voxta VaM Plugin](https://hub.virtamate.com/) - Official VaM integration
