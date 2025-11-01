using Microsoft.AspNetCore.Mvc;
using Server.Application.Interfaces;
using Server.Application.DTOs.Command;
using Server.Api.Services;
using System.Text.Json;

namespace Server.Api.Controllers;

/// <summary>
/// Envanter (inventory) komutları için API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly ICommandService _commandService;
    private readonly AgentConnectionManager _connectionManager;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(
        ICommandService commandService,
        AgentConnectionManager connectionManager,
        ILogger<InventoryController> logger)
    {
        _commandService = commandService;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Tam sistem envanteri alır
    /// </summary>
    [HttpPost("full/{deviceId}")]
    public async Task<IActionResult> GetFullInventory(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "getfullinventory");
    }

    /// <summary>
    /// Yüklü yazılımları alır
    /// </summary>
    [HttpPost("software/{deviceId}")]
    public async Task<IActionResult> GetInstalledSoftware(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "getinstalledsoftware");
    }

    /// <summary>
    /// Yüklü yamaları alır
    /// </summary>
    [HttpPost("patches/{deviceId}")]
    public async Task<IActionResult> GetInstalledPatches(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "getinstalledpatches");
    }

    /// <summary>
    /// Bekleyen güncellemeleri alır
    /// </summary>
    [HttpPost("updates/{deviceId}")]
    public async Task<IActionResult> GetPendingUpdates(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "getpendingupdates");
    }

    /// <summary>
    /// Sistem bilgisi alır
    /// </summary>
    [HttpPost("sysinfo/{deviceId}")]
    public async Task<IActionResult> GetSystemInfo(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "sysinfo");
    }

    /// <summary>
    /// CPU bilgisi alır
    /// </summary>
    [HttpPost("cpu/{deviceId}")]
    public async Task<IActionResult> GetCpuInfo(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "cpuinfo");
    }

    /// <summary>
    /// Network bilgisi alır
    /// </summary>
    [HttpPost("network/{deviceId}")]
    public async Task<IActionResult> GetNetworkInfo(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "netinfo");
    }

    /// <summary>
    /// SMBIOS bilgisi alır
    /// </summary>
    [HttpPost("smbios/{deviceId}")]
    public async Task<IActionResult> GetSmbiosInfo(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "smbios");
    }

    /// <summary>
    /// VM tespiti yapar
    /// </summary>
    [HttpPost("vm/{deviceId}")]
    public async Task<IActionResult> DetectVirtualMachine(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "vm");
    }

    /// <summary>
    /// WiFi ağlarını tarar
    /// </summary>
    [HttpPost("wifi/{deviceId}")]
    public async Task<IActionResult> ScanWifi(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "wifiscan");
    }

    /// <summary>
    /// Performans sayaçlarını alır
    /// </summary>
    [HttpPost("perfcounters/{deviceId}")]
    public async Task<IActionResult> GetPerformanceCounters(Guid deviceId)
    {
        return await ExecuteCommand(deviceId, "perfcounters");
    }

    private async Task<IActionResult> ExecuteCommand(Guid deviceId, string commandType, object? parameters = null)
    {
        if (!_connectionManager.IsConnected(deviceId))
        {
            return BadRequest(new { error = "Device is not connected" });
        }

        var userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        
        var request = new ExecuteCommandRequest
        {
            DeviceId = deviceId,
            CommandType = commandType,
            Parameters = parameters != null ? JsonSerializer.Serialize(parameters) : null
        };

        var result = await _commandService.ExecuteCommandAsync(request, userId);
        if (!result.Success || result.Data == null)
            return BadRequest(new { error = result.ErrorMessage ?? "Failed to create command" });

        var command = new
        {
            action = commandType,
            commandId = result.Data.Id,
            timestamp = DateTime.UtcNow,
            parameters
        };

        var sent = await _connectionManager.SendCommandToAgentAsync(deviceId, command);
        
        if (sent)
        {
            await _commandService.MarkCommandAsSentAsync(result.Data.Id);
            return Ok(result.Data);
        }

        return BadRequest(new { error = "Failed to send command" });
    }
}
