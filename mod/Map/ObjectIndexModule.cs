using CasuMp.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using Together;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace CasuMod;

public sealed class ObjectIndexModule : MonoBehaviour
{
    private sealed class ObjectRef
    {
        public int Key;
        public string Category = "";
        public string Id = "";
        public string Name = "";
        public string Sprite = "";
        public int BlockX;
        public int BlockY;
        public GameObject GameObject = null!;
        public Component Component = null!;
    }

    public static ObjectIndexModule Instance { get; private set; } = null!;

    private readonly Dictionary<int, ObjectRef> _refs = new();
    private readonly List<ObjectRef> _objects = new();
    private readonly List<MapBackgroundObject> _backgrounds = new();
    private float _nextRefresh;
    private string _instanceKey = "";
    private bool _hasWorld;

    private void Awake()
    {
        Instance = this;
        _nextRefresh = Time.unscaledTime + 2f;
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 60f;
        Refresh();
    }

    public void RefreshNow()
    {
        _nextRefresh = Time.unscaledTime + 60f;
        Refresh();
    }

    public void HandleQuery(MapObjectQueryRequest? request)
    {
        if (request == null || OrchestratorClient.Instance == null) return;
        var response = new MapObjectQueryResponse
        {
            RequestId = request.RequestId,
            InstanceKey = request.InstanceKey,
            Cell = request.Cell,
        };

        var categories = new HashSet<string>(request.Categories ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        if (categories.Count == 0)
        {
            categories.Add("building");
            categories.Add("container");
            categories.Add("trader");
            categories.Add("animal");
        }
        HashSet<string>? allowedNames = request.FilterNames
            ? new HashSet<string>(request.Names ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
            : null;
        List<ObjectRef> visible = _objects
            .Where(o => o.BlockX >= request.MinX && o.BlockX <= request.MaxX
                && o.BlockY >= request.MinY && o.BlockY <= request.MaxY
                && categories.Contains(o.Category)
                && (allowedNames == null || allowedNames.Contains(o.Name)))
            .ToList();
        response.Total = visible.Count;
        response.Names = _objects
            .Select(o => o.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (request.Cell > 0)
        {
            foreach (var group in visible.GroupBy(o => (o.BlockX / request.Cell, o.BlockY / request.Cell)))
            {
                var cluster = new MapObjectCluster
                {
                    CellX = group.Key.Item1,
                    CellY = group.Key.Item2,
                    X = group.Key.Item1 * request.Cell + request.Cell / 2,
                    Y = group.Key.Item2 * request.Cell + request.Cell / 2,
                    Count = group.Count(),
                };
                foreach (var category in group.GroupBy(o => o.Category))
                    cluster.Categories[category.Key] = category.Count();
                response.Clusters.Add(cluster);
            }
        }
        else
        {
            int max = Math.Max(1, request.MaxObjects);
            response.Objects = visible
                .OrderBy(o => CategoryPriority(o.Category))
                .ThenBy(o => o.Key)
                .Take(max)
                .Select(ToEntry)
                .ToList();
            response.Truncated = visible.Count > max;
        }

        OrchestratorClient.Instance.SendEvent(MessageType.OBJECT_QUERY_RESPONSE, response);
    }

    public void HandleDetail(MapObjectDetailRequest? request)
    {
        if (request == null || OrchestratorClient.Instance == null) return;
        var response = new MapObjectDetailResponse
        {
            RequestId = request.RequestId,
            InstanceKey = request.InstanceKey,
        };

        if (!int.TryParse(request.ObjectKey, out int key) || !_refs.TryGetValue(key, out ObjectRef? reference))
        {
            response.Found = false;
            response.Error = "object not found";
            OrchestratorClient.Instance.SendEvent(MessageType.OBJECT_DETAIL_RESPONSE, response);
            return;
        }

        MapObjectDetail detail = BuildDetail(reference);
        response.Found = true;
        response.Detail = detail;
        OrchestratorClient.Instance.SendEvent(MessageType.OBJECT_DETAIL_RESPONSE, response);
    }

    private void Refresh()
    {
        WorldGeneration? world = WorldGeneration.world;
        OrchestratorClient? client = OrchestratorClient.Instance;
        if (world == null || !world.worldExists || client == null)
        {
            _hasWorld = false;
            return;
        }

        _instanceKey = client.InstanceKey;
        int height = (int)world.height;
        float halfWidth = world.width * 0.5f;
        float halfHeight = world.height * 0.5f;
        var refs = new Dictionary<int, ObjectRef>();
        var objects = new List<ObjectRef>();
        var backgrounds = new List<MapBackgroundObject>();
        var backgroundRenderers = new HashSet<int>();

        BuildingEntity[] buildings = FindObjectsOfType<BuildingEntity>();
        foreach (BuildingEntity build in buildings)
        {
            if (build == null || build.gameObject == null) continue;
            SpriteRenderer? renderer = build.GetComponent<SpriteRenderer>();
            if (renderer == null) renderer = build.GetComponentInChildren<SpriteRenderer>();
            bool background = IsBackground(build, renderer);
            Vector2 position = build.transform.position;
            Vector2Int block = world.WorldToBlockPos(position);
            int displayY = height - 1 - block.y;
            TraderScript? trader = build.GetComponent<TraderScript>();

            if (background)
            {
                foreach (SpriteRenderer childRenderer in build.GetComponentsInChildren<SpriteRenderer>())
                {
                    if (childRenderer == null || childRenderer.sprite == null) continue;
                    Bounds childBounds = childRenderer.bounds;
                    backgrounds.Add(new MapBackgroundObject
                    {
                        Sprite = childRenderer.sprite.name,
                        X = childBounds.center.x + halfWidth,
                        Y = halfHeight - childBounds.center.y,
                        Width = Mathf.Max(0.1f, childBounds.size.x),
                        Height = Mathf.Max(0.1f, childBounds.size.y),
                        Rotation = childRenderer.transform.eulerAngles.z,
                        SortingOrder = childRenderer.sortingOrder,
                    });
                    backgroundRenderers.Add(childRenderer.GetInstanceID());
                }
                continue;
            }

            string category = trader != null ? "trader"
                : build.animal ? "animal"
                : build.GetComponent<Container>() != null ? "container"
                : "building";
            string sprite = trader?.headRender?.sprite?.name ?? renderer?.sprite?.name ?? "";
            string name = !string.IsNullOrEmpty(build.fullName) ? build.fullName : build.id;
            var reference = new ObjectRef
            {
                Key = build.GetInstanceID(),
                Category = category,
                Id = build.id ?? "",
                Name = name,
                Sprite = sprite,
                BlockX = block.x,
                BlockY = displayY,
                GameObject = build.gameObject,
                Component = build,
            };
            refs[reference.Key] = reference;
            objects.Add(reference);
        }

        foreach (Item item in Item.allItems.ToArray())
        {
            if (item == null || item.transform.parent != null) continue;
            Vector2 position = item.transform.position;
            Vector2Int block = world.WorldToBlockPos(position);
            SpriteRenderer? renderer = item.GetComponent<SpriteRenderer>();
            string name = item.Stats != null ? item.Stats.fullName : item.id;
            var reference = new ObjectRef
            {
                Key = item.GetInstanceID(),
                Category = "item",
                Id = item.id ?? "",
                Name = name,
                Sprite = renderer?.sprite?.name ?? "",
                BlockX = block.x,
                BlockY = height - 1 - block.y,
                GameObject = item.gameObject,
                Component = item,
            };
            refs[reference.Key] = reference;
            objects.Add(reference);
        }

        foreach (SpriteRenderer renderer in FindObjectsOfType<SpriteRenderer>())
        {
            if (renderer == null || renderer.sprite == null) continue;
            int rendererId = renderer.GetInstanceID();
            if (backgroundRenderers.Contains(rendererId)) continue;
            if (renderer.sortingOrder > -100 || renderer.sortingOrder <= -9999) continue;
            string objectName = renderer.gameObject.name.ToLowerInvariant();
            string spriteName = renderer.sprite.name.ToLowerInvariant();
            bool looksBackground = objectName.Contains("lifepod")
                || objectName.Contains("background")
                || objectName.Contains("backdrop")
                || spriteName.Contains("lifepod")
                || spriteName.Contains("background")
                || spriteName.Contains("backdrop");
            if (!looksBackground) continue;
            if (renderer.GetComponentInParent<BuildingEntity>() != null) continue;
            Bounds bounds = renderer.bounds;
            backgrounds.Add(new MapBackgroundObject
            {
                Sprite = renderer.sprite.name,
                X = bounds.center.x + halfWidth,
                Y = halfHeight - bounds.center.y,
                Width = Mathf.Max(0.1f, bounds.size.x),
                Height = Mathf.Max(0.1f, bounds.size.y),
                Rotation = renderer.transform.eulerAngles.z,
                SortingOrder = renderer.sortingOrder,
            });
        }

        foreach (TilemapRenderer renderer in FindObjectsOfType<TilemapRenderer>())
        {
            if (renderer == null || !renderer.enabled) continue;
            int sortingOrder = renderer.sortingOrder;
            if (sortingOrder > -100 || sortingOrder <= -9999) continue;
            string objectName = renderer.gameObject.name.ToLowerInvariant();
            bool looksBackground = objectName.Contains("lifepod")
                || objectName.Contains("background")
                || objectName.Contains("backdrop")
                || objectName.Contains("back");
            if (!looksBackground) continue;
            Tilemap? tilemap = renderer.GetComponent<Tilemap>();
            if (tilemap == null) continue;

            Vector3 scale = tilemap.transform.lossyScale;
            BoundsInt bounds = tilemap.cellBounds;
            for (int x = bounds.xMin; x < bounds.xMax; x++)
            {
                for (int y = bounds.yMin; y < bounds.yMax; y++)
                {
                    Vector3Int cell = new(x, y, 0);
                    Sprite? sprite = tilemap.GetSprite(cell);
                    if (sprite == null || string.IsNullOrEmpty(sprite.name)) continue;
                    Vector3 centerWorld = tilemap.GetCellCenterWorld(cell);
                    backgrounds.Add(new MapBackgroundObject
                    {
                        Sprite = sprite.name,
                        X = centerWorld.x + halfWidth,
                        Y = halfHeight - centerWorld.y,
                        Width = Mathf.Max(0.1f, sprite.bounds.size.x * Mathf.Abs(scale.x)),
                        Height = Mathf.Max(0.1f, sprite.bounds.size.y * Mathf.Abs(scale.y)),
                        Rotation = tilemap.transform.eulerAngles.z,
                        SortingOrder = sortingOrder,
                    });
                }
            }
        }

        _refs.Clear();
        foreach (var pair in refs) _refs[pair.Key] = pair.Value;
        _objects.Clear();
        _objects.AddRange(objects);
        _backgrounds.Clear();
        _backgrounds.AddRange(backgrounds.OrderBy(b => b.SortingOrder));
        _hasWorld = true;

        OrchestratorClient.Instance.SendEvent(MessageType.OBJECT_BACKGROUNDS, new MapBackgroundPayload
        {
            InstanceKey = _instanceKey,
            Objects = _backgrounds,
        });

        Plugin.Log.LogInfo($"[Objects] {_objects.Count} objects, {_backgrounds.Count} background objects.");
    }

    private static bool IsBackground(BuildingEntity build, SpriteRenderer? renderer)
    {
        if (renderer != null)
        {
            if (renderer.sortingOrder <= -999 && renderer.sortingOrder > -9999) return true;
            string objectName = build.gameObject.name.ToLowerInvariant();
            string spriteName = renderer.sprite != null ? renderer.sprite.name.ToLowerInvariant() : "";
            if (objectName.Contains("lifepodbackground")
                || objectName.Contains("background")
                || spriteName.Contains("lifepodbackground")
                || spriteName.Contains("background")
                || spriteName.Contains("backdrop"))
            {
                return true;
            }
        }

        TilemapRenderer? tilemapRenderer = build.GetComponent<TilemapRenderer>();
        return tilemapRenderer != null
            && tilemapRenderer.enabled
            && tilemapRenderer.sortingOrder <= -100
            && tilemapRenderer.sortingOrder > -9999;
    }

    private static MapObjectEntry ToEntry(ObjectRef reference)
    {
        return new MapObjectEntry
        {
            Key = reference.Key,
            Category = reference.Category,
            Id = reference.Id,
            Name = reference.Name,
            Sprite = reference.Sprite,
            BlockX = reference.BlockX,
            BlockY = reference.BlockY,
        };
    }

    private static int CategoryPriority(string category)
    {
        return category switch
        {
            "trader" => 0,
            "container" => 1,
            "building" => 2,
            "animal" => 3,
            "item" => 4,
            _ => 5,
        };
    }

    private static MapObjectDetail BuildDetail(ObjectRef reference)
    {
        var detail = new MapObjectDetail
        {
            Key = reference.Key,
            Category = reference.Category,
            Id = reference.Id,
            Name = reference.Name,
            Sprite = reference.Sprite,
            BlockX = reference.BlockX,
            BlockY = reference.BlockY,
        };

        if (reference.Component is BuildingEntity build)
        {
            detail.Health = build.health;
            detail.Description = build.description ?? "";
            TraderScript? trader = build.GetComponent<TraderScript>();
            if (trader != null)
            {
                detail.Trader = new MapTraderDetail
                {
                    Reputation = trader.reputation,
                    Hostile = trader.hostile,
                    ValueGiven = trader.valueGiven,
                    TotalValueGiven = trader.totalValueGiven,
                    MaxValueGiven = TraderScript.MAX_VALUE_GIVEN,
                };
                try
                {
                    detail.Trader.ActualReputation = trader.actualReputation;
                }
                catch
                {
                    detail.Trader.ActualReputation = trader.reputation;
                }

                foreach (TraderItem item in trader.items ?? new List<TraderItem>())
                {
                    if (item == null) continue;
                    ItemInfo? info = Item.GetItem(item.id);
                    detail.Trader.Items.Add(new MapTraderItem
                    {
                        Id = item.id ?? "",
                        Name = info?.fullName ?? item.id ?? "",
                        Value = item.value,
                        Preference = item.preference.ToString(),
                        Price = trader.ItemPrice(item),
                        Bought = item.bought,
                    });
                }
            }
        }
        else if (reference.Component is Item item)
        {
            detail.Condition = item.condition;
            detail.Name = item.Stats != null ? item.Stats.fullName : item.id;
        }

        return detail;
    }
}
