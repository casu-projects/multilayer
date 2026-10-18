using CasuMp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Together;
using UnityEngine;

namespace CasuMod;

public sealed class MapModule : MonoBehaviour
{
    public static MapModule Instance { get; private set; }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), int> _pendingBlocks = new();

    private string _webAddr = "";
    private string _token = "";
    private string _instanceKey = "";
    private int _depth = 1;
    private string _worldVersion = "";
    private int _serverVersion;
    private bool _initialSent;
    private bool _sendingBlocks;
    private float _nextPlayersAt;
    private float _nextBlocksAt;
    private string _lastError = "";

    private void Awake()
    {
        Instance = this;
        _webAddr = (Environment.GetEnvironmentVariable("CASU_WEB_ADDR") ?? "").TrimEnd('/');
        _token = Environment.GetEnvironmentVariable("CASU_WEB_TOKEN") ?? "";
        if (string.IsNullOrEmpty(_webAddr))
            Plugin.Log.LogWarning("[Map] CASU_WEB_ADDR 없음 — 맵 전송 비활성화.");
    }

    private void Update()
    {
        if (OrchestratorClient.Instance != null)
        {
            _instanceKey = OrchestratorClient.Instance.InstanceKey;
            _depth = OrchestratorClient.Instance.InstanceDepth;
        }

        if (Time.unscaledTime >= _nextPlayersAt)
        {
            _nextPlayersAt = Time.unscaledTime + 1f;
            SendPlayerPositions();
        }

        if (_initialSent && Time.unscaledTime >= _nextBlocksAt)
        {
            _nextBlocksAt = Time.unscaledTime + 1f;
            _ = SendPendingBlocksAsync();
        }
    }

    public void OnWorldGenerated()
    {
        ObjectIndexModule.Instance?.RefreshNow();
        if (string.IsNullOrEmpty(_webAddr)) return;

        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.width == 0 || world.height == 0) return;

        OrchestratorClient? client = OrchestratorClient.Instance;
        string instanceKey = client?.InstanceKey ?? "";
        if (string.IsNullOrEmpty(instanceKey)) return;
        _instanceKey = instanceKey;
        _depth = client?.InstanceDepth ?? _depth;

        _worldVersion = Guid.NewGuid().ToString("N");
        _serverVersion = 0;
        _initialSent = false;
        lock (_lock) _pendingBlocks.Clear();

        try
        {
            MapWorldUpload upload = BuildWorldUpload(world, instanceKey);
            _ = UploadWorldAsync(upload);
        }
        catch (Exception ex)
        {
            LogError($"월드 스냅샷 생성 실패: {ex.Message}");
        }
    }

    private MapWorldUpload BuildWorldUpload(WorldGeneration world, string instanceKey)
    {
        int width = (int)world.width;
        int height = (int)world.height;

        var palette = new List<MapPaletteEntry>();
        Color[] colors = world.tileColors ?? Array.Empty<Color>();
        for (int i = 0; i < colors.Length; i++)
        {
            BlockInfo info = world.GetBlockInfo((ushort)i);
            palette.Add(new MapPaletteEntry
            {
                Block = i,
                Color = "#" + ColorUtility.ToHtmlStringRGB(colors[i]),
                Name = info?.name ?? $"Block {i}",
            });
        }

        var runs = new List<MapRleRun>();
        int current = -1;
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int block = world.GetBlock(new Vector2Int(x, y));
                if (block == current)
                {
                    count++;
                    continue;
                }
                if (count > 0) runs.Add(new MapRleRun { Count = count, Block = current });
                current = block;
                count = 1;
            }
        }
        if (count > 0) runs.Add(new MapRleRun { Count = count, Block = current });

        return new MapWorldUpload
        {
            InstanceKey = instanceKey,
            Depth = _depth,
            BiomeDepth = world.biomeDepth,
            WorldVersion = _worldVersion,
            Width = width,
            Height = height,
            Palette = palette,
            Runs = runs,
        };
    }

    private async Task UploadWorldAsync(MapWorldUpload upload)
    {
        try
        {
            string url = $"{_webAddr}/api/instances/{Uri.EscapeDataString(upload.InstanceKey)}/world";
            using var content = new StringContent(JsonConvert.SerializeObject(upload), Encoding.UTF8, "application/json");
            content.Headers.Add("X-Casu-Token", _token);
            using HttpResponseMessage response = await _http.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                LogError($"월드 업로드 실패: HTTP {(int)response.StatusCode}");
                return;
            }

            string body = await response.Content.ReadAsStringAsync();
            JObject json = JObject.Parse(body);
            _serverVersion = json.Value<int?>("version") ?? 0;
            _initialSent = true;
            Plugin.Log.LogInfo($"[Map] 월드 스냅샷 전송 완료 ({upload.Width}x{upload.Height}, v{_serverVersion}).");
        }
        catch (Exception ex)
        {
            LogError($"월드 업로드 오류: {ex.Message}");
        }
    }

    public void OnBlockChanged(Vector2Int pos, ushort block)
    {
        if (string.IsNullOrEmpty(_worldVersion)) return;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld) return;

        lock (_lock)
        {
            _pendingBlocks[(pos.x, pos.y)] = block;
        }
    }

    private async Task SendPendingBlocksAsync()
    {
        if (_sendingBlocks) return;

        List<MapBlockChange> changes;
        lock (_lock)
        {
            if (_pendingBlocks.Count == 0) return;
            changes = new List<MapBlockChange>(_pendingBlocks.Count);
            foreach (KeyValuePair<(int X, int Y), int> pair in _pendingBlocks)
            {
                changes.Add(new MapBlockChange { X = pair.Key.X, Y = pair.Key.Y, Block = pair.Value });
            }
            _pendingBlocks.Clear();
        }

        _sendingBlocks = true;
        try
        {
            var upload = new MapBlockUpload
            {
                WorldVersion = _worldVersion,
                BaseVersion = _serverVersion,
                Changes = changes,
            };
            string url = $"{_webAddr}/api/instances/{Uri.EscapeDataString(_instanceKey)}/blocks";
            using var content = new StringContent(JsonConvert.SerializeObject(upload), Encoding.UTF8, "application/json");
            content.Headers.Add("X-Casu-Token", _token);
            using HttpResponseMessage response = await _http.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
            {
                LogError($"블록 델타 실패: HTTP {(int)response.StatusCode}");
                MergeBack(changes);
                return;
            }

            string body = await response.Content.ReadAsStringAsync();
            JObject json = JObject.Parse(body);
            _serverVersion = json.Value<int?>("version") ?? _serverVersion;
        }
        catch (Exception ex)
        {
            LogError($"블록 델타 오류: {ex.Message}");
            MergeBack(changes);
        }
        finally
        {
            _sendingBlocks = false;
        }
    }

    private void MergeBack(List<MapBlockChange> changes)
    {
        lock (_lock)
        {
            foreach (MapBlockChange change in changes)
            {
                (int, int) key = (change.X, change.Y);
                if (!_pendingBlocks.ContainsKey(key)) _pendingBlocks[key] = change.Block;
            }
        }
    }

    private void SendPlayerPositions()
    {
        OrchestratorClient client = OrchestratorClient.Instance;
        if (client == null || !client.Connected) return;

        WorldGeneration world = WorldGeneration.world;
        if (world == null) return;

        var entries = new List<PlayerPositionEntry>();
        foreach (ScavPlayer player in ScavPlayer.AllLivingPlayers) AddPlayer(entries, player, world, false);
        foreach (ScavPlayer player in ScavPlayer.AllDeadPlayers) AddPlayer(entries, player, world, true);

        var payload = new PlayerPositionsPayload
        {
            InstanceKey = _instanceKey,
            WorldVersion = _worldVersion,
            Players = entries,
        };
        if (Body_PlaceBody_MultiplayerPatch.has_spawn_location)
        {
            Vector2Int spawn = world.WorldToBlockPos(Body_PlaceBody_MultiplayerPatch.SpawnLocation);
            payload.HasSpawn = true;
            payload.SpawnBlockX = spawn.x;
            payload.SpawnBlockY = (int)world.height - 1 - spawn.y;
        }
        client.SendEvent(MessageTypes.PlayerPositions, payload);
    }

    private static void AddPlayer(List<PlayerPositionEntry> entries, ScavPlayer player,
        WorldGeneration world, bool dead)
    {
        if (player == null) return;

        Vector2 pos = player.pos;
        Vector2Int block = world.WorldToBlockPos(pos);
        FacialExpression face = player.body != null ? player.body.GetComponentInChildren<FacialExpression>() : null;
        entries.Add(new PlayerPositionEntry
        {
            PlayerKey = MigrationModule.PlayerKeyOf(player),
            SteamId = player.SteamId,
            Name = player.playerName ?? "",
            X = pos.x,
            Y = pos.y,
            BlockX = block.x,
            BlockY = block.y,
            Dead = dead,
            HeadSprite = face?.head != null && face.head.sprite != null ? face.head.sprite.name : "",
            EyeSprite = face?.eyes != null && face.eyes.sprite != null ? face.eyes.sprite.name : "",
        });
    }

    private void LogError(string message)
    {
        if (_lastError == message) return;
        _lastError = message;
        Plugin.Log.LogWarning($"[Map] {message}");
    }
}
