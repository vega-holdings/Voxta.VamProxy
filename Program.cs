using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

var builder = WebApplication.CreateBuilder(args);

// Load configuration
var config = builder.Configuration.GetSection("Proxy");
var listenPort = config.GetValue<int>("ListenPort", 5385);
var remoteVoxtaUrl = config.GetValue<string>("RemoteVoxtaUrl") ?? "ws://127.0.0.1:5384/hub";
var localAudioFolder = config.GetValue<string>("LocalAudioFolder") ?? @"E:\VAM\Custom\Sounds\Voxta";
var cleanupAfterSeconds = config.GetValue<int>("CleanupAudioAfterSeconds", 60);

// Ensure audio folder exists
Directory.CreateDirectory(localAudioFolder);

builder.WebHost.UseUrls($"http://0.0.0.0:{listenPort}");

var app = builder.Build();
app.UseWebSockets();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
Console.WriteLine("║           Voxta VaM Proxy - Remote Audio Bridge            ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"  Local listen port:    http://0.0.0.0:{listenPort}");
Console.WriteLine($"  Remote Voxta server:  {remoteVoxtaUrl}");
Console.WriteLine($"  Local audio folder:   {localAudioFolder}");
Console.WriteLine($"  Audio cleanup after:  {cleanupAfterSeconds}s");
Console.WriteLine();
Console.WriteLine("  Configure VaM Voxta plugin to connect to:");
Console.WriteLine($"    Host: 127.0.0.1    Port: {listenPort}");
Console.WriteLine();
Console.WriteLine("  Press Ctrl+C to exit.");
Console.WriteLine("════════════════════════════════════════════════════════════════");
Console.WriteLine();

// Track files for cleanup
var audioFiles = new List<(string Path, DateTime Created)>();
var cleanupLock = new object();

// Cleanup timer
var cleanupTimer = new Timer(_ =>
{
    lock (cleanupLock)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-cleanupAfterSeconds);
        var toRemove = audioFiles.Where(f => f.Created < cutoff).ToList();
        foreach (var file in toRemove)
        {
            try
            {
                if (File.Exists(file.Path))
                {
                    File.Delete(file.Path);
                    logger.LogDebug("Cleaned up audio file: {Path}", file.Path);
                }
                audioFiles.Remove(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to cleanup file: {Path}", file.Path);
            }
        }
    }
}, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

// Mic recording state (shared across connections)
WaveInEvent? waveIn = null;
ClientWebSocket? audioInputSocket = null;
bool micReady = false;
string? currentSessionId = null;

async Task StartMicRecording(string remoteBaseUrl, string sessionId)
{
    if (waveIn != null) return; // Already recording

    try
    {
        // List available audio devices
        var deviceCount = WaveInEvent.DeviceCount;
        logger.LogInformation("Available audio input devices ({Count}):", deviceCount);
        for (int i = 0; i < deviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            logger.LogInformation("  [{Index}] {Name}", i, caps.ProductName);
        }

        if (deviceCount == 0)
        {
            logger.LogError("No audio input devices found!");
            return;
        }

        // Connect to remote Voxta audio input endpoint (requires sessionId!)
        var audioUrl = remoteBaseUrl.Replace("http://", "ws://").Replace("https://", "wss://")
            + $"/ws/audio/input/stream?sessionId={sessionId}";
        audioInputSocket = new ClientWebSocket();

        logger.LogInformation("Connecting mic stream to {Url}", audioUrl);
        await audioInputSocket.ConnectAsync(new Uri(audioUrl), CancellationToken.None);
        logger.LogInformation("WebSocket connected to Voxta audio input");

        // Send audio specifications
        var specs = new
        {
            sampleRate = 16000,
            channels = 1,
            bufferMilliseconds = 30,
            bitsPerSample = 16
        };
        var specsJson = JsonSerializer.Serialize(specs);
        logger.LogInformation("Sending audio specs: {Specs}", specsJson);
        await audioInputSocket.SendAsync(
            Encoding.UTF8.GetBytes(specsJson),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        // Start NAudio recording (device 0 = default)
        waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            BufferMilliseconds = 30,
            WaveFormat = new WaveFormat(16000, 1)
        };

        var bytesSent = 0L;
        var lastLogTime = DateTime.UtcNow;

        waveIn.DataAvailable += async (sender, e) =>
        {
            if (!micReady || e.BytesRecorded == 0 || audioInputSocket?.State != WebSocketState.Open)
                return;

            try
            {
                await audioInputSocket.SendAsync(
                    new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);

                bytesSent += e.BytesRecorded;

                // Log throughput every 2 seconds
                if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 2)
                {
                    logger.LogDebug("Mic streaming: {Bytes} bytes sent total", bytesSent);
                    lastLogTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send mic data");
            }
        };

        waveIn.RecordingStopped += (sender, e) =>
        {
            if (e.Exception != null)
                logger.LogError(e.Exception, "Recording stopped with error");
            else
                logger.LogInformation("Recording stopped");
        };

        waveIn.StartRecording();
        logger.LogInformation("NAudio recording started on device 0 (16kHz mono)");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to start mic recording");
    }
}

void StopMicRecording()
{
    micReady = false;

    if (waveIn != null)
    {
        waveIn.StopRecording();
        waveIn.Dispose();
        waveIn = null;
        logger.LogInformation("Mic recording stopped");
    }

    if (audioInputSocket != null)
    {
        try
        {
            audioInputSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(1000);
        }
        catch { }
        audioInputSocket.Dispose();
        audioInputSocket = null;
    }
}

app.Map("/hub", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection required");
        return;
    }

    // Capture Authorization header from VaM's request
    string? authToken = null;
    if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        authToken = authHeader.ToString();
        if (authToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            authToken = authToken.Substring(7); // Extract just the token
        logger.LogInformation("Captured auth token from VaM request");
    }

    logger.LogInformation("VaM client connecting...");

    using var vamSocket = await context.WebSockets.AcceptWebSocketAsync();
    using var voxtaSocket = new ClientWebSocket();
    using var httpClient = new HttpClient();

    // Add auth header to remote Voxta connection
    if (!string.IsNullOrEmpty(authToken))
    {
        voxtaSocket.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
    }

    string? remoteBaseUrl = null;
    string? clientAudioFolder = null;
    string? capturedAuthToken = authToken; // Use the HTTP header token

    try
    {
        // Connect to remote Voxta
        logger.LogInformation("Connecting to remote Voxta at {Url}", remoteVoxtaUrl);
        await voxtaSocket.ConnectAsync(new Uri(remoteVoxtaUrl), CancellationToken.None);
        logger.LogInformation("Connected to remote Voxta");

        // Extract base URL for audio downloads
        var uri = new Uri(remoteVoxtaUrl);
        remoteBaseUrl = $"http://{uri.Host}:{uri.Port}";

        // Proxy messages in both directions
        var vamToVoxta = ProxyMessages(
            vamSocket, voxtaSocket, "VaM→Voxta",
            msg => ProcessVamToVoxta(msg, ref clientAudioFolder, localAudioFolder, ref capturedAuthToken, logger));

        var voxtaToVam = ProxyMessages(
            voxtaSocket, vamSocket, "Voxta→VaM",
            async msg =>
            {
                var result = await ProcessVoxtaToVam(msg, remoteBaseUrl, clientAudioFolder ?? localAudioFolder, httpClient, audioFiles, cleanupLock, logger);

                // Start mic connection when chat starts - extract sessionId
                if (msg.Contains("\"$type\":\"chatStarted\"") || msg.Contains("\"$type\": \"chatStarted\""))
                {
                    var sessionMatch = System.Text.RegularExpressions.Regex.Match(msg, "\"sessionId\"\\s*:\\s*\"([^\"]+)\"");
                    if (sessionMatch.Success)
                    {
                        currentSessionId = sessionMatch.Groups[1].Value;
                        logger.LogInformation("Chat started with sessionId: {SessionId}", currentSessionId);
                        _ = StartMicRecording(remoteBaseUrl, currentSessionId);
                    }
                    else
                    {
                        logger.LogWarning("Chat started but no sessionId found in message!");
                    }
                }

                // Handle recordingRequest - Voxta tells us when to record
                if (msg.Contains("\"$type\":\"recordingRequest\"") || msg.Contains("\"$type\": \"recordingRequest\""))
                {
                    var enabledMatch = System.Text.RegularExpressions.Regex.Match(msg, "\"enabled\"\\s*:\\s*(true|false)");
                    if (enabledMatch.Success)
                    {
                        var enabled = enabledMatch.Groups[1].Value == "true";
                        if (enabled)
                        {
                            logger.LogInformation("Recording requested: START");
                            micReady = true; // Enable sending audio data
                        }
                        else
                        {
                            logger.LogInformation("Recording requested: STOP");
                            micReady = false; // Stop sending audio data
                        }
                    }
                }

                return result;
            });

        await Task.WhenAny(vamToVoxta, voxtaToVam);
    }
    catch (WebSocketException ex)
    {
        logger.LogError(ex, "WebSocket error");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Proxy error");
    }
    finally
    {
        logger.LogInformation("Connection closed");

        // Stop mic recording
        StopMicRecording();

        if (vamSocket.State == WebSocketState.Open)
            await vamSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy closing", CancellationToken.None);
        if (voxtaSocket.State == WebSocketState.Open)
            await voxtaSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Proxy closing", CancellationToken.None);
    }
});

await app.RunAsync();

// Proxy messages from source to destination
async Task ProxyMessages(
    WebSocket source,
    WebSocket destination,
    string direction,
    Func<string, Task<string>> processor)
{
    var buffer = new byte[64 * 1024];
    var messageBuffer = new StringBuilder();

    while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
    {
        var result = await source.ReceiveAsync(buffer, CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            logger.LogInformation("{Direction}: Close received", direction);
            break;
        }

        if (result.MessageType == WebSocketMessageType.Text)
        {
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            messageBuffer.Append(text);

            if (result.EndOfMessage)
            {
                var fullMessage = messageBuffer.ToString();
                messageBuffer.Clear();

                // Process and potentially modify the message
                var processedMessage = await processor(fullMessage);

                // Send to destination
                var bytes = Encoding.UTF8.GetBytes(processedMessage);
                await destination.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        else if (result.MessageType == WebSocketMessageType.Binary)
        {
            // Pass through binary messages unchanged
            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                WebSocketMessageType.Binary,
                result.EndOfMessage,
                CancellationToken.None);
        }
    }
}

// Process messages from VaM to Voxta (intercept authentication)
Task<string> ProcessVamToVoxta(string message, ref string? clientAudioFolder, string localAudioFolder, ref string? capturedAuthToken, ILogger logger)
{
    const char signalREnd = '\x1e';

    // SignalR messages end with 0x1e
    if (!message.EndsWith(signalREnd))
        return Task.FromResult(message);

    var parts = message.Split(signalREnd, StringSplitOptions.RemoveEmptyEntries);
    var processed = new StringBuilder();

    foreach (var part in parts)
    {
        try
        {
            var json = JsonNode.Parse(part);
            if (json == null)
            {
                processed.Append(part);
                processed.Append(signalREnd);
                continue;
            }

            // Handle SignalR protocol messages (type 6=ping, 7=close, etc)
            var signalRType = json["type"]?.GetValue<int>();
            if (signalRType.HasValue && signalRType != 1)
            {
                // Pass through non-invocation SignalR messages (ping, close, etc)
                processed.Append(part);
                processed.Append(signalREnd);
                continue;
            }

            // Check for SignalR invocation with arguments
            var args = json["arguments"]?.AsArray();
            if (args != null && args.Count > 0)
            {
                var innerMsg = args[0];
                var msgType = innerMsg?["$type"]?.GetValue<string>();

                if (msgType == "authenticate")
                {
                    // Capture API key for mic streaming auth
                    var apiKey = innerMsg?["apiKey"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        capturedAuthToken = apiKey;
                        logger.LogDebug("Captured API key for mic streaming");
                    }

                    // Intercept authentication to modify capabilities
                    var capabilities = innerMsg?["capabilities"];
                    if (capabilities != null)
                    {
                        // Tell Voxta we support WebSocket audio input (mic streaming)
                        capabilities["audioInput"] = "WebSocketStream";

                        var audioOutput = capabilities["audioOutput"]?.GetValue<string>();
                        var audioFolder = capabilities["audioFolder"]?.GetValue<string>();

                        if (audioOutput == "LocalFile" && audioFolder != null)
                        {
                            // Store the client's desired audio folder
                            clientAudioFolder = audioFolder.Replace("\\\\", "\\");
                            logger.LogInformation("VaM wants audio at: {Folder}", clientAudioFolder);

                            // Tell remote Voxta to use URL-based audio instead
                            capabilities["audioOutput"] = "Url";
                            capabilities["audioFolder"] = null;

                            logger.LogInformation("Modified auth: audioOutput LocalFile → Url");
                        }
                    }
                }
            }

            processed.Append(json.ToJsonString());
            processed.Append(signalREnd);
        }
        catch
        {
            // If parsing fails, pass through unchanged
            processed.Append(part);
            processed.Append(signalREnd);
        }
    }

    return Task.FromResult(processed.ToString());
}

// Process messages from Voxta to VaM (download audio, rewrite URLs)
async Task<string> ProcessVoxtaToVam(
    string message,
    string? remoteBaseUrl,
    string localAudioFolder,
    HttpClient httpClient,
    List<(string, DateTime)> audioFiles,
    object cleanupLock,
    ILogger logger)
{
    const char signalREnd = '\x1e';

    if (!message.EndsWith(signalREnd))
        return message;

    var parts = message.Split(signalREnd, StringSplitOptions.RemoveEmptyEntries);
    var processed = new StringBuilder();

    foreach (var part in parts)
    {
        try
        {
            var json = JsonNode.Parse(part);
            if (json == null)
            {
                processed.Append(part);
                processed.Append(signalREnd);
                continue;
            }

            // Handle SignalR protocol messages (type 6=ping, 7=close, etc)
            var signalRType = json["type"]?.GetValue<int>();
            if (signalRType.HasValue && signalRType != 1)
            {
                // Pass through non-invocation SignalR messages
                processed.Append(part);
                processed.Append(signalREnd);
                continue;
            }

            // Check for SignalR invocation
            var args = json["arguments"]?.AsArray();
            if (args != null && args.Count > 0)
            {
                var innerMsg = args[0];
                var msgType = innerMsg?["$type"]?.GetValue<string>();

                // Handle replyChunk and replyGenerating (thinkingSpeechUrl)
                if (msgType == "replyChunk" || msgType == "replyGenerating")
                {
                    var audioUrlField = msgType == "replyChunk" ? "audioUrl" : "thinkingSpeechUrl";
                    var audioUrl = innerMsg?[audioUrlField]?.GetValue<string>();

                    if (!string.IsNullOrEmpty(audioUrl))
                    {
                        // Build full URL if relative
                        var fullUrl = audioUrl;
                        if (audioUrl.StartsWith("/") && remoteBaseUrl != null)
                            fullUrl = remoteBaseUrl + audioUrl;

                        if (fullUrl.StartsWith("http"))
                        {
                            try
                            {
                                // Download audio file
                                var audioData = await httpClient.GetByteArrayAsync(fullUrl);

                                // Generate local filename
                                var filename = $"voxta_{Guid.NewGuid()}.wav";
                                var localPath = Path.Combine(localAudioFolder, filename);

                                // Save locally
                                await File.WriteAllBytesAsync(localPath, audioData);

                                // Track for cleanup
                                lock (cleanupLock)
                                {
                                    audioFiles.Add((localPath, DateTime.UtcNow));
                                }

                                // Rewrite URL to local path
                                innerMsg![audioUrlField] = localPath;

                                logger.LogDebug("Downloaded audio: {Url} → {Path}", fullUrl, localPath);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Failed to download audio from {Url}", fullUrl);
                            }
                        }
                    }
                }
            }

            processed.Append(json.ToJsonString());
            processed.Append(signalREnd);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse message, passing through unchanged");
            processed.Append(part);
            processed.Append(signalREnd);
        }
    }

    return processed.ToString();
}
