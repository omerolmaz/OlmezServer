using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Application.Interfaces;
using Server.Application.DTOs.Device;
using Server.Domain.Enums;
using Server.Infrastructure.Data;
using Server.Api.Services;

namespace Server.Api.Middleware;

public class AgentWebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentWebSocketMiddleware> _logger;
    private readonly AgentConnectionManager _connectionManager;

    public AgentWebSocketMiddleware(
        RequestDelegate next, 
        ILogger<AgentWebSocketMiddleware> logger,
        AgentConnectionManager connectionManager)
    {
        _next = next;
        _logger = logger;
        _connectionManager = connectionManager;
    }

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext dbContext, IDeviceService deviceService)
    {
        if (context.Request.Path == "/agent.ashx" && context.WebSockets.IsWebSocketRequest)
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("New agent WebSocket connection from {RemoteIp}", context.Connection.RemoteIpAddress);
            
            await HandleAgentConnection(webSocket, context, dbContext, deviceService);
        }
        else
        {
            await _next(context);
        }
    }

    private async Task HandleAgentConnection(
        WebSocket webSocket, 
        HttpContext context, 
        ApplicationDbContext dbContext,
        IDeviceService deviceService)
    {
        var buffer = new byte[1024 * 4];
        Guid? deviceId = null;
        
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                
                try
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (WebSocketException wsEx) when (wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogInformation("Client {DeviceId} closed connection prematurely", deviceId?.ToString() ?? "unknown");
                    break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("WebSocket operation cancelled for device {DeviceId}", deviceId?.ToString() ?? "unknown");
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Client {DeviceId} initiated close handshake", deviceId?.ToString() ?? "unknown");
                    
                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogInformation("Received text message: {Message}", message);
                    
                    try
                    {
                        var json = JsonDocument.Parse(message);
                        var root = json.RootElement;
                        
                        if (root.TryGetProperty("action", out var actionElement))
                        {
                            var action = actionElement.GetString();
                            
                            switch (action)
                            {
                                case "agenthello":
                                    await HandleAgentHello(root, webSocket, context);
                                    break;
                                    
                                case "agentinfo":
                                    var newDeviceId = await HandleAgentInfo(root, webSocket, deviceService, context);
                                    if (newDeviceId.HasValue)
                                    {
                                        deviceId = newDeviceId;
                                        _logger.LogInformation("Device ID set from agentinfo: {DeviceId}", deviceId);
                                    }
                                    break;
                                    
                                case "register":
                                    var regDeviceId = await HandleRegister(root, webSocket, deviceService);
                                    if (regDeviceId.HasValue)
                                    {
                                        deviceId = regDeviceId;
                                        _logger.LogInformation("Device ID set from register: {DeviceId}", deviceId);
                                    }
                                    break;
                                    
                                case "heartbeat":
                                    await HandleHeartbeat(deviceId, deviceService);
                                    break;
                                    
                                case "commandResult":
                                case "commandresult":
                                    await HandleCommandResult(root, dbContext, deviceService, context);
                                    break;
                                    
                                default:
                                    _logger.LogWarning("Unknown action: {Action}", action);
                                    break;
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to parse JSON message");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary protocol için - MeshCentral gibi
                    _logger.LogDebug("Received binary message: {Length} bytes", result.Count);
                    // TODO: Binary protokol implementasyonu
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket connection closed prematurely for device {DeviceId}", deviceId?.ToString() ?? "unknown");
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket error for device {DeviceId}: {ErrorCode}", deviceId?.ToString() ?? "unknown", ex.WebSocketErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling agent connection for device {DeviceId}", deviceId?.ToString() ?? "unknown");
        }
        finally
        {
            if (deviceId.HasValue)
            {
                // Connection manager'dan çıkar
                _connectionManager.RemoveConnection(deviceId.Value);
                
                // Device durumunu disconnected olarak güncelle
                try
                {
                    await deviceService.UpdateDeviceStatusAsync(new UpdateDeviceStatusRequest
                    {
                        DeviceId = deviceId.Value,
                        Status = ConnectionStatus.Disconnected
                    });
                    _logger.LogInformation("Device {DeviceId} disconnected and removed from connection manager", deviceId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update device status for {DeviceId}", deviceId);
                }
            }
            
            // WebSocket'i güvenli şekilde kapat
            try
            {
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
                // Zaten kapalı veya bağlantı kesilmiş - sessizce görmezden gel
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing WebSocket for device {DeviceId}", deviceId?.ToString() ?? "unknown");
            }
            finally
            {
                webSocket.Dispose();
            }
        }
    }

    private async Task<Guid?> HandleRegister(
        JsonElement root, 
        WebSocket webSocket, 
        IDeviceService deviceService)
    {
        try
        {
            var hostname = root.GetProperty("hostname").GetString() ?? "Unknown";
            var domain = root.TryGetProperty("domain", out var domainEl) ? domainEl.GetString() : null;
            var osVersion = root.TryGetProperty("osVersion", out var osEl) ? osEl.GetString() : null;
            var architecture = root.TryGetProperty("architecture", out var archEl) ? archEl.GetString() : null;
            var ipAddress = root.TryGetProperty("ipAddress", out var ipEl) ? ipEl.GetString() : null;
            var agentVersion = root.TryGetProperty("agentVersion", out var verEl) ? verEl.GetString() : null;

            var request = new RegisterDeviceRequest
            {
                Hostname = hostname ?? "Unknown",
                Domain = domain,
                OsVersion = osVersion,
                Architecture = architecture,
                IpAddress = ipAddress ?? "0.0.0.0",
                AgentVersion = agentVersion ?? "1.0.0"
            };

            var result = await deviceService.RegisterDeviceAsync(request);

            if (result.Success && result.Data != null)
            {
                // CRITICAL: Always add/update connection in manager
                _connectionManager.AddConnection(result.Data.Id, webSocket);
                _logger.LogInformation("Device added to connection manager: {DeviceId} ({Hostname})", 
                    result.Data.Id, hostname);
                
                var response = JsonSerializer.Serialize(new
                {
                    action = "registered",
                    deviceId = result.Data.Id,
                    status = "success"
                });

                var bytes = Encoding.UTF8.GetBytes(response);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes), 
                    WebSocketMessageType.Text, 
                    true, 
                    CancellationToken.None);

                _logger.LogInformation("Registration response sent to device: {DeviceId}", result.Data.Id);
                return result.Data.Id;
            }
            else
            {
                _logger.LogWarning("Device registration failed: {Error}", result.ErrorMessage);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling device registration");
            return null;
        }
    }

    private async Task HandleHeartbeat(Guid? deviceId, IDeviceService deviceService)
    {
        if (deviceId.HasValue)
        {
            await deviceService.UpdateDeviceStatusAsync(new UpdateDeviceStatusRequest
            {
                DeviceId = deviceId.Value,
                Status = ConnectionStatus.Connected
            });
            _logger.LogDebug("Heartbeat from device {DeviceId}", deviceId);
        }
    }

    private async Task HandleAgentHello(JsonElement root, WebSocket webSocket, HttpContext context)
    {
        _logger.LogInformation("Agent hello received from {RemoteIp}", context.Connection.RemoteIpAddress);
        
        // MeshCentral protokolü: Server bilgilerini gönder
        var response = new
        {
            action = "serverhello",
            serverid = Environment.MachineName,
            version = "1.0.0",
            serverTime = DateTime.UtcNow,
            features = new[] { "agentinfo", "commands", "files" }
        };

        var responseJson = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        
        _logger.LogInformation("Sent serverhello response");
    }

    private async Task<Guid?> HandleAgentInfo(
        JsonElement root, 
        WebSocket webSocket, 
        IDeviceService deviceService,
        HttpContext context)
    {
        try
        {
            // MeshCentral protokolünden gelen bilgileri parse et
            var hostname = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : 
                          root.TryGetProperty("hostname", out var hostEl) ? hostEl.GetString() : "Unknown";
            
            var macAddress = root.TryGetProperty("macAddress", out var macEl) ? macEl.GetString() : null;
            var domain = root.TryGetProperty("domain", out var domainEl) ? domainEl.GetString() : null;
            var osVersion = root.TryGetProperty("osdesc", out var osEl) ? osEl.GetString() : null;
            var architecture = root.TryGetProperty("platform", out var platEl) ? platEl.GetString() : null;
            
            // IP adresini önce agent'ten al, yoksa connection'dan al
            var ipAddress = root.TryGetProperty("ipAddress", out var ipEl) ? ipEl.GetString() : null;
            if (string.IsNullOrEmpty(ipAddress) || ipAddress == "127.0.0.1" || ipAddress == "::1")
            {
                // Agent'ten gelen IP localhost ise connection IP'sini kullan
                var remoteIp = context.Connection.RemoteIpAddress;
                if (remoteIp != null)
                {
                    // IPv6 localhost'u IPv4'e çevir
                    if (remoteIp.ToString() == "::1")
                    {
                        ipAddress = "127.0.0.1";
                    }
                    else
                    {
                        ipAddress = remoteIp.ToString();
                    }
                }
            }
            
            var agentVersion = root.TryGetProperty("agentVersion", out var verEl) ? verEl.GetString() : 
                              root.TryGetProperty("ver", out var ver2El) ? ver2El.GetString() : null;

            _logger.LogInformation("Agent info - Hostname: {Hostname}, MAC: {MacAddress}, OS: {Os}, IP: {Ip}", 
                hostname, macAddress, osVersion, ipAddress);

            var request = new RegisterDeviceRequest
            {
                Hostname = hostname ?? "Unknown",
                MacAddress = macAddress,
                Domain = domain,
                OsVersion = osVersion,
                Architecture = architecture,
                IpAddress = ipAddress ?? "0.0.0.0",
                AgentVersion = agentVersion ?? "1.0.0"
            };

            var result = await deviceService.RegisterDeviceAsync(request);

            if (result.Success && result.Data != null)
            {
                // CRITICAL: Always add/update connection in manager (handles both new and existing devices)
                _connectionManager.AddConnection(result.Data.Id, webSocket);
                _logger.LogInformation("Device added to connection manager: {DeviceId} ({Hostname})", 
                    result.Data.Id, hostname);
                
                var response = JsonSerializer.Serialize(new
                {
                    action = "registered",
                    deviceId = result.Data.Id,
                    status = "success",
                    message = "Device registered successfully"
                });

                var bytes = Encoding.UTF8.GetBytes(response);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes), 
                    WebSocketMessageType.Text, 
                    true, 
                    CancellationToken.None);

                _logger.LogInformation("Registration response sent to device: {DeviceId}", result.Data.Id);
                return result.Data.Id;
            }
            else
            {
                _logger.LogWarning("Device registration failed: {Error}", result.ErrorMessage);
                
                // Hata mesajı gönder
                var errorResponse = JsonSerializer.Serialize(new
                {
                    action = "error",
                    message = result.ErrorMessage
                });
                
                var errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
                
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling agent info");
            return null;
        }
    }

    private async Task HandleCommandResult(JsonElement root, ApplicationDbContext dbContext, IDeviceService deviceService, HttpContext httpContext)
    {
        try
        {
            if (!root.TryGetProperty("commandId", out var commandIdProp))
            {
                _logger.LogWarning("CommandResult missing commandId");
                return;
            }

            var commandIdStr = commandIdProp.GetString();
            
            // If commandId is "system" or other non-GUID values, it's a system message (like registration agentinfo)
            // Check if it contains agentinfo in the result
            if (string.IsNullOrEmpty(commandIdStr) || !Guid.TryParse(commandIdStr, out var commandId))
            {
                _logger.LogInformation("System message received (commandId: {CommandId})", commandIdStr);
                
                // Check if this is an agentinfo system message for device registration
                if (root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object)
                {
                    if (resultEl.TryGetProperty("action", out var actionEl) && 
                        actionEl.GetString() == "agentinfo")
                    {
                        _logger.LogInformation("Processing agentinfo from system message");
                        await HandleAgentInfoFromResult(resultEl, deviceService, httpContext);
                    }
                }
                return;
            }

            var command = await dbContext.Commands.FindAsync(commandId);
            if (command == null)
            {
                _logger.LogWarning("Command not found: {CommandId}", commandId);
                return;
            }

            // Get status
            var status = root.TryGetProperty("status", out var statusEl) 
                ? statusEl.GetString() ?? "Completed" 
                : "Completed";

            // Get result (can be string or JSON object)
            string? resultText = null;
            if (root.TryGetProperty("result", out var resultEl2))
            {
                if (resultEl2.ValueKind == JsonValueKind.String)
                {
                    resultText = resultEl2.GetString();
                }
                else
                {
                    // Convert JSON object/array to string
                    resultText = JsonSerializer.Serialize(resultEl2);
                }
            }

            // Get success flag
            var success = root.TryGetProperty("success", out var successEl) && successEl.GetBoolean();

            // Get error message if any
            string? errorMessage = null;
            if (root.TryGetProperty("error", out var errorEl))
            {
                errorMessage = errorEl.GetString();
            }

            // Update command
            command.Status = success ? "Completed" : "Failed";
            command.Result = resultText;
            command.CompletedAt = DateTime.UtcNow;
            
            if (!string.IsNullOrEmpty(errorMessage))
            {
                command.ErrorMessage = errorMessage;
            }

            // Calculate execution duration if SentAt is set
            if (command.SentAt.HasValue)
            {
                command.ExecutionDurationMs = (long)(command.CompletedAt.Value - command.SentAt.Value).TotalMilliseconds;
            }

            await dbContext.SaveChangesAsync();
            
            _logger.LogInformation("Command {CommandId} completed with status: {Status}, success: {Success}, duration: {Duration}ms", 
                commandId, command.Status, success, command.ExecutionDurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command result");
        }
    }
    
    private async Task<Guid?> HandleAgentInfoFromResult(
        JsonElement resultElement,
        IDeviceService deviceService,
        HttpContext context)
    {
        try
        {
            var hostname = resultElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "Unknown";
            var macAddress = resultElement.TryGetProperty("macAddress", out var macEl) ? macEl.GetString() : null;
            var domain = resultElement.TryGetProperty("domain", out var domainEl) ? domainEl.GetString() : null;
            var osVersion = resultElement.TryGetProperty("osdesc", out var osEl) ? osEl.GetString() : null;
            var architecture = resultElement.TryGetProperty("platform", out var platEl) ? platEl.GetString() : null;
            var ipAddress = resultElement.TryGetProperty("ipAddress", out var ipEl) ? ipEl.GetString() : null;
            var agentVersion = resultElement.TryGetProperty("agentVersion", out var verEl) ? verEl.GetString() : 
                              resultElement.TryGetProperty("ver", out var ver2El) ? ver2El.GetString() : null;

            _logger.LogInformation("Agent info from result - Hostname: {Hostname}, MAC: {MacAddress}, OS: {Os}, IP: {Ip}", 
                hostname, macAddress, osVersion, ipAddress);

            var request = new RegisterDeviceRequest
            {
                Hostname = hostname ?? "Unknown",
                MacAddress = macAddress,
                Domain = domain,
                OsVersion = osVersion,
                Architecture = architecture,
                IpAddress = ipAddress ?? "0.0.0.0",
                AgentVersion = agentVersion ?? "1.0.0"
            };

            var result = await deviceService.RegisterDeviceAsync(request);

            if (result.Success && result.Data != null)
            {
                _logger.LogInformation("Device registered via system message: {DeviceId} ({Hostname}, MAC: {MacAddress})", 
                    result.Data.Id, hostname, macAddress);
                return result.Data.Id;
            }
            else
            {
                _logger.LogWarning("Device registration failed: {Error}", result.ErrorMessage);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling agentinfo from result");
            return null;
        }
    }
}
