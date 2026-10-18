const API = '';
const POLL_MS = 1000;
const INSTANCE_POLL_MS = 2000;
const DETAIL_MS = 2000;
const ASSET_BASE = '/assets/game/';
const CHAT_POLL_MS = 1500;
const MAP_SCALE = (window.matchMedia('(pointer: coarse)').matches || window.innerWidth < 900) ? 4 : 8;
const OBJECT_FILTER_KEY = 'casumap.objectFilter.v1';
const MAX_ZOOM = 3;

const el = {
  layers: document.getElementById('layers'),
  mapViewport: document.getElementById('mapViewport'),
  mapScaler: document.getElementById('mapScaler'),
  mapImage: document.getElementById('mapImage'),
  canvas: document.getElementById('mapCanvas'),
  objectCanvas: document.getElementById('objectCanvas'),
  spawnMarker: document.getElementById('spawnMarker'),
  markers: document.getElementById('markers'),
  empty: document.getElementById('empty'),
  zoomIn: document.getElementById('zoomIn'),
  zoomOut: document.getElementById('zoomOut'),
  zoomReset: document.getElementById('zoomReset'),
  steamToggle: document.getElementById('steamToggle'),
  objectFilterButton: document.getElementById('objectFilterButton'),
  chatToggle: document.getElementById('chatToggle'),
  usersToggle: document.getElementById('usersToggle'),
  objectFilterPanel: document.getElementById('objectFilterPanel'),
  objectFilterList: document.getElementById('objectFilterList'),
  objectFilterAllOn: document.getElementById('objectFilterAllOn'),
  objectFilterAllOff: document.getElementById('objectFilterAllOff'),
  userList: document.getElementById('userList'),
  userCount: document.getElementById('userCount'),
  chatHistory: document.getElementById('chatHistory'),
  chatForm: document.getElementById('chatForm'),
  chatInput: document.getElementById('chatInput'),
  chatSend: document.getElementById('chatSend'),
  chatAutocomplete: document.getElementById('chatAutocomplete'),
  detailDrawer: document.getElementById('detailDrawer'),
  detailClose: document.getElementById('detailClose'),
  detailHead: document.getElementById('detailHead'),
  detailName: document.getElementById('detailName'),
  detailSub: document.getElementById('detailSub'),
  detailHealth: document.getElementById('detailHealth'),
  detailSkills: document.getElementById('detailSkills'),
  detailLimbs: document.getElementById('detailLimbs'),
  detailInventory: document.getElementById('detailInventory'),
  detailBody: document.getElementById('detailBody'),
  playerDetail: document.getElementById('playerDetail'),
  objectDetail: document.getElementById('objectDetail'),
  objectInfo: document.getElementById('objectInfo'),
  traderSection: document.getElementById('traderSection'),
  traderInfo: document.getElementById('traderInfo'),
  traderStockSection: document.getElementById('traderStockSection'),
  traderStock: document.getElementById('traderStock'),
};

const state = {
  instances: [],
  selected: null,
  chatVisible: true,
  usersVisible: true,
  worldVersion: '',
  version: 0,
  width: 0,
  height: 0,
  tileSize: 8,
  mapWidthPx: 0,
  mapHeightPx: 0,
  mapPngUrl: '',
  mapPng: null,
  palette: [],
  paletteMap: new Map(),
  players: [],
  allUsers: [],
  usersLoading: false,
  pendingFocus: null,
  selectedPlayer: '',
  worldReady: false,
  worldLoading: false,
  playersLoading: false,
  instancesLoading: false,
  detailOpen: false,
  detailLoading: false,
  detail: null,
  detailTimer: null,
  chatMessages: [],
  chatLastId: 0,
  chatLoading: false,
  chatSending: false,
  commands: [],
  autocompleteItems: [],
  autocompleteIndex: 0,
  autocompleteOpen: false,
  showSteam: true,
  objects: [],
  objectClusters: [],
  objectCell: 0,
  objectQueryLoading: false,
  objectQueryPending: false,
  objectQueryTimer: null,
  objectNames: [],
  objectNamesSignature: '',
  objectFilter: {},
  objectFilterOpen: false,
  objectQueryGeneration: 0,
  objectBackgroundVersion: -1,
  spawn: null,
  selectedObject: '',
  pendingPlayerDetail: '',
  detailMode: '',
  objectDetail: null,
  objectDetailLoading: false,
  suppressClickUntil: 0,
  view: {
    zoom: 1,
    panX: 0,
    panY: 0,
    fit: 1,
    cover: 1,
    min: 1,
    max: 1,
    userZoomed: false,
  },
};

let catalog = null;
const imageCache = new Map();
let drag = null;
let viewFrame = 0;

async function loadMeta() {
  try {
    const meta = await getJson('/api/meta');
    if (meta?.serverName) document.title = meta.serverName;
  } catch (error) {
    console.error('meta', error);
  }
}

async function getJson(url) {
  const response = await fetch(API + url, { cache: 'no-store' });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

function loadImage(url) {
  if (imageCache.has(url)) return imageCache.get(url);
  const promise = new Promise(resolve => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => resolve(null);
    image.src = url;
  });
  imageCache.set(url, promise);
  return promise;
}

async function loadCatalog() {
  try {
    const manifest = await getJson(`${ASSET_BASE}manifest.json`);
    if (!manifest) return;
    const blocksById = new Map((manifest.blocks?.entries || []).map(entry => [entry.block, entry]));
    const headsByName = new Map((manifest.heads?.entries || []).map(entry => [entry.name, entry]));
    const itemsById = new Map((manifest.items?.entries || []).map(entry => [entry.id, entry]));
    const objectsByName = new Map((manifest.objects?.entries || []).map(entry => [entry.name, entry]));
    const [blockAtlas, headAtlas, itemAtlas, objectAtlas] = await Promise.all([
      loadImage(`${ASSET_BASE}${manifest.blocks?.atlas || ''}`),
      loadImage(`${ASSET_BASE}${manifest.heads?.atlas || ''}`),
      loadImage(`${ASSET_BASE}${manifest.items?.atlas || ''}`),
      loadImage(`${ASSET_BASE}${manifest.objects?.atlas || ''}`),
    ]);
    catalog = { manifest, blocksById, headsByName, itemsById, objectsByName, blockAtlas, headAtlas, itemAtlas, objectAtlas };
  } catch (error) {
    console.warn('asset catalog unavailable', error);
  }
}

function parseHex(color, fallback = [0, 0, 0]) {
  if (typeof color !== 'string') return fallback;
  const value = color.replace('#', '').trim();
  if (value.length === 3) {
    return [
      parseInt(value[0] + value[0], 16),
      parseInt(value[1] + value[1], 16),
      parseInt(value[2] + value[2], 16),
    ];
  }
  if (value.length === 6 || value.length === 8) {
    return [
      parseInt(value.slice(0, 2), 16),
      parseInt(value.slice(2, 4), 16),
      parseInt(value.slice(4, 6), 16),
    ];
  }
  return fallback;
}

function buildPaletteMap(palette) {
  const map = new Map();
  for (const entry of palette || []) map.set(entry.block, parseHex(entry.color));
  return map;
}

function paletteColor(block) {
  return state.paletteMap.get(block) || [0, 0, 0];
}

function updateViewLimits() {
  const viewportWidth = el.mapViewport.clientWidth;
  const viewportHeight = el.mapViewport.clientHeight;
  const mapWidth = state.mapWidthPx;
  const mapHeight = state.mapHeightPx;
  if (!viewportWidth || !viewportHeight || !mapWidth || !mapHeight) return null;

  const fit = Math.min(viewportWidth / mapWidth, viewportHeight / mapHeight);
  const cover = Math.max(viewportWidth / mapWidth, viewportHeight / mapHeight);
  const max = Math.max(1, cover, MAX_ZOOM);
  state.view.fit = fit;
  state.view.cover = cover;
  state.view.min = fit;
  state.view.max = max;
  return { viewportWidth, viewportHeight, mapWidth, mapHeight, fit, cover, max };
}

function clampZoom(zoom) {
  return Math.min(state.view.max, Math.max(state.view.min, zoom));
}

function clampPan() {
  const viewportWidth = el.mapViewport.clientWidth;
  const viewportHeight = el.mapViewport.clientHeight;
  const width = state.mapWidthPx * state.view.zoom;
  const height = state.mapHeightPx * state.view.zoom;

  if (width <= viewportWidth) {
    state.view.panX = (viewportWidth - width) / 2;
  } else {
    state.view.panX = Math.min(0, Math.max(viewportWidth - width, state.view.panX));
  }

  if (height <= viewportHeight) {
    state.view.panY = (viewportHeight - height) / 2;
  } else {
    state.view.panY = Math.min(0, Math.max(viewportHeight - height, state.view.panY));
  }
}

function updateMarkerPositions() {
  if (!state.mapWidthPx || !state.mapHeightPx || !state.width || !state.height) return;
  const zoom = state.view.zoom;
  for (const marker of el.markers.children) {
    const player = marker._player;
    if (!player) continue;
    const x = state.view.panX + (player.blockX / state.width) * state.mapWidthPx * zoom;
    const y = state.view.panY + (player.blockY / state.height) * state.mapHeightPx * zoom;
    marker.style.transform = `translate3d(${x}px, ${y}px, 0) translate(-50%, -50%)`;
  }
}

function applyView() {
  if (!state.mapWidthPx || !state.mapHeightPx) return;
  clampPan();
  el.mapScaler.style.transform =
    `translate3d(${state.view.panX}px, ${state.view.panY}px, 0) scale(${state.view.zoom})`;
  updateMarkerPositions();
  updateSpawnMarker();
  drawObjectLayer();
}

function updateSpawnMarker() {
  if (!state.worldReady || !state.spawn) {
    el.spawnMarker.classList.add('hidden');
    return;
  }
  const point = objectScreenPoint(state.spawn.x, state.spawn.y);
  el.spawnMarker.classList.remove('hidden');
  el.spawnMarker.style.transform =
    `translate3d(${point.x}px, ${point.y}px, 0) translate(-50%, -100%)`;
}

function scheduleView() {
  if (viewFrame) return;
  viewFrame = requestAnimationFrame(() => {
    viewFrame = 0;
    applyView();
    queueObjectQuery(300);
  });
}

function centerView(zoom) {
  const limits = updateViewLimits();
  if (!limits) return;
  state.view.zoom = clampZoom(zoom);
  state.view.panX = (limits.viewportWidth - limits.mapWidth * state.view.zoom) / 2;
  state.view.panY = (limits.viewportHeight - limits.mapHeight * state.view.zoom) / 2;
  applyView();
}

function resetView() {
  state.view.userZoomed = false;
  const limits = updateViewLimits();
  if (!limits) return;
  centerView(limits.cover);
}

function setMapSize(width, height) {
  state.mapWidthPx = Math.max(1, width | 0);
  state.mapHeightPx = Math.max(1, height | 0);
  el.mapScaler.style.width = `${state.mapWidthPx}px`;
  el.mapScaler.style.height = `${state.mapHeightPx}px`;
  state.view.panX = 0;
  state.view.panY = 0;
  state.view.zoom = 1;
  state.view.userZoomed = false;
  resetView();
}

function zoomAt(clientX, clientY, factor) {
  const limits = updateViewLimits();
  if (!limits) return;
  const rect = el.mapViewport.getBoundingClientRect();
  const mouseX = clientX - rect.left;
  const mouseY = clientY - rect.top;
  const before = state.view.zoom;
  const after = clampZoom(before * factor);
  if (Math.abs(after - before) < 0.00001) return;

  const worldX = (mouseX - state.view.panX) / before;
  const worldY = (mouseY - state.view.panY) / before;
  state.view.zoom = after;
  state.view.panX = mouseX - worldX * after;
  state.view.panY = mouseY - worldY * after;
  state.view.userZoomed = true;
  scheduleView();
}

function zoomBy(factor) {
  const rect = el.mapViewport.getBoundingClientRect();
  zoomAt(rect.left + el.mapViewport.clientWidth / 2, rect.top + el.mapViewport.clientHeight / 2, factor);
}

function handleResize() {
  const limits = updateViewLimits();
  if (!limits) return;
  if (!state.view.userZoomed) {
    resetView();
    return;
  }
  state.view.zoom = clampZoom(state.view.zoom);
  applyView();
  queueObjectQuery(300);
}

function resetWorldView() {
  state.worldVersion = '';
  state.version = 0;
  state.width = 0;
  state.height = 0;
  state.tileSize = 8;
  state.mapWidthPx = 0;
  state.mapHeightPx = 0;
  state.mapPngUrl = '';
  state.mapPng = null;
  state.palette = [];
  state.paletteMap = new Map();
  state.players = [];
  state.objects = [];
  state.objectClusters = [];
  state.objectCell = 0;
  state.objectBackgroundVersion = -1;
  state.spawn = null;
  state.worldReady = false;
  state.selectedPlayer = '';
  state.selectedObject = '';
  if (state.objectQueryTimer) {
    clearTimeout(state.objectQueryTimer);
    state.objectQueryTimer = null;
  }
  state.view.zoom = 1;
  state.view.panX = 0;
  state.view.panY = 0;
  state.view.userZoomed = false;
  state.pendingPlayerDetail = '';
  closeDetail();
  el.mapImage.classList.remove('visible');
  el.mapImage.removeAttribute('src');
  el.mapScaler.style.transform = '';
  el.mapScaler.style.width = '1px';
  el.mapScaler.style.height = '1px';
  el.canvas.width = 1;
  el.canvas.height = 1;
  el.canvas.getContext('2d').clearRect(0, 0, 1, 1);
  el.markers.innerHTML = '';
  el.spawnMarker.classList.add('hidden');
  el.empty.classList.remove('hidden');
  drawObjectLayer();
}

function selectInstance(key) {
  if (state.selected === key) return;
  state.selected = key;
  state.pendingFocus = null;
  resetWorldView();
  renderLayers();
  loadWorld(true);
  loadPlayers();
}

async function loadInstances() {
  if (state.instancesLoading) return;
  state.instancesLoading = true;
  try {
    const list = await getJson('/api/instances');
    state.instances = Array.isArray(list) ? list : [];
    if (!state.selected || !state.instances.some(instance => instance.instanceKey === state.selected)) {
      state.selected = state.instances.length ? state.instances[0].instanceKey : null;
      resetWorldView();
      if (state.selected) {
        loadWorld(true);
        loadPlayers();
      }
    }
    renderLayers();
  } catch (error) {
    console.error('instances', error);
  } finally {
    state.instancesLoading = false;
  }
}

async function loadWorld(force = false) {
  if (!state.selected || state.worldLoading) return;
  state.worldLoading = true;
  const key = state.selected;
  const since = force ? -1 : state.version;
  try {
    const world = await getJson(`/api/instances/${encodeURIComponent(key)}/world?since=${since}`);
    if (!world) {
      if (!state.worldReady) el.empty.classList.remove('hidden');
      return;
    }
    if (state.selected !== key) return;

    const needsFull = force || since < 0 || world.reset || world.worldVersion !== state.worldVersion;
    state.palette = world.palette || state.palette;
    state.paletteMap = buildPaletteMap(state.palette);
    state.width = world.width;
    state.height = world.height;
    state.worldVersion = world.worldVersion;
    state.version = world.version;
    state.tileSize = MAP_SCALE;
    state.mapPngUrl = world.mapPngUrl ? `${world.mapPngUrl}&scale=${MAP_SCALE}` : '';
    const backgroundVersion = world.objectBackgroundVersion ?? 0;
    const backgroundChanged = state.objectBackgroundVersion !== backgroundVersion;
    state.objectBackgroundVersion = backgroundVersion;

    if (needsFull) {
      if (state.mapPngUrl) {
        const image = await loadImage(state.mapPngUrl);
        if (state.selected !== key) return;
        if (image) {
          state.mapPng = image;
          el.mapImage.src = state.mapPngUrl;
          el.mapImage.classList.add('visible');
          el.canvas.width = 1;
          el.canvas.height = 1;
          el.canvas.getContext('2d').clearRect(0, 0, 1, 1);
          setMapSize(image.naturalWidth, image.naturalHeight);
        } else {
          state.mapPng = null;
          el.mapImage.classList.remove('visible');
          drawFallbackFull(world);
        }
      } else {
        state.mapPng = null;
        el.mapImage.classList.remove('visible');
        drawFallbackFull(world);
      }
    } else if (backgroundChanged) {
      await refreshMapImage();
    } else {
      applyChanges(world.changes || []);
    }

    state.worldReady = true;
    el.empty.classList.add('hidden');
    renderMarkers();
    queueObjectQuery(0);
    if (state.pendingFocus && state.pendingFocus.instanceKey === state.selected) {
      const target = state.pendingFocus;
      state.pendingFocus = null;
      focusPlayerAt(target);
    }
  } catch (error) {
    console.error('world', error);
  } finally {
    state.worldLoading = false;
  }
}

async function refreshMapImage() {
  if (!state.mapPngUrl) return false;
  const image = await loadImage(state.mapPngUrl);
  if (!image) return false;
  state.mapPng = image;
  el.mapImage.src = state.mapPngUrl;
  el.mapImage.classList.add('visible');
  el.canvas.width = 1;
  el.canvas.height = 1;
  el.canvas.getContext('2d').clearRect(0, 0, 1, 1);
  state.mapWidthPx = image.naturalWidth;
  state.mapHeightPx = image.naturalHeight;
  el.mapScaler.style.width = `${state.mapWidthPx}px`;
  el.mapScaler.style.height = `${state.mapHeightPx}px`;
  updateViewLimits();
  applyView();
  renderMarkers();
  return true;
}

function drawFallbackFull(world) {
  const width = Math.max(1, world.width | 0);
  const height = Math.max(1, world.height | 0);
  state.width = width;
  state.height = height;
  state.tileSize = 1;
  state.mapPng = null;
  el.canvas.width = width;
  el.canvas.height = height;
  el.mapImage.classList.remove('visible');

  const context = el.canvas.getContext('2d');
  const image = context.createImageData(width, height);
  const data = image.data;
  let pixel = 0;
  for (const run of world.runs || []) {
    const count = Math.max(0, run.count | 0);
    const color = paletteColor(run.block);
    for (let i = 0; i < count && pixel < width * height; i++, pixel++) {
      const row = height - 1 - Math.floor(pixel / width);
      const column = pixel % width;
      const offset = (row * width + column) * 4;
      data[offset] = color[0];
      data[offset + 1] = color[1];
      data[offset + 2] = color[2];
      data[offset + 3] = 255;
    }
  }
  context.putImageData(image, 0, 0);
  setMapSize(width, height);
}

function applyChanges(changes) {
  if (!changes.length) return;
  const context = el.canvas.getContext('2d');
  const tile = state.mapPng ? Math.min(4, state.tileSize) : 1;
  if (state.mapPng) {
    const overlayWidth = Math.max(1, state.width * tile);
    const overlayHeight = Math.max(1, state.height * tile);
    if (el.canvas.width !== overlayWidth || el.canvas.height !== overlayHeight) {
      el.canvas.width = overlayWidth;
      el.canvas.height = overlayHeight;
    }
  }
  for (const change of changes) {
    if (change.x < 0 || change.y < 0 || change.x >= state.width || change.y >= state.height) continue;
    const displayY = state.height - 1 - change.y;
    if (state.mapPng) {
      if (change.block === 0) {
        context.clearRect(change.x * tile, displayY * tile, tile, tile);
        continue;
      }
      const block = catalog?.blocksById.get(change.block);
      if (block && catalog?.blockAtlas) {
        context.clearRect(change.x * tile, displayY * tile, tile, tile);
        context.drawImage(catalog.blockAtlas,
          block.x, block.y, block.w, block.h,
          change.x * tile, displayY * tile, tile, tile);
        continue;
      }
      const color = paletteColor(change.block);
      context.clearRect(change.x * tile, displayY * tile, tile, tile);
      context.fillStyle = `rgb(${color[0]},${color[1]},${color[2]})`;
      context.fillRect(change.x * tile, displayY * tile, tile, tile);
    } else {
      const color = paletteColor(change.block);
      context.fillStyle = `rgb(${color[0]},${color[1]},${color[2]})`;
      context.fillRect(change.x, displayY, 1, 1);
    }
  }
}

async function loadAllUsers() {
  if (state.usersLoading) return;
  state.usersLoading = true;
  try {
    const layers = state.instances.slice();
    if (!layers.length) {
      state.allUsers = [];
      renderUserList();
      return;
    }
    const lists = await Promise.all(layers.map(async instance => {
      try {
        const response = await getJson(`/api/instances/${encodeURIComponent(instance.instanceKey)}/players`);
        return (response?.players || []).map(player => ({
          ...player,
          instanceKey: instance.instanceKey,
          depth: Number(instance.depth) || 0,
        }));
      } catch (error) {
        console.error('users', instance.instanceKey, error);
        return [];
      }
    }));
    state.allUsers = lists.flat();
    renderUserList();
  } finally {
    state.usersLoading = false;
  }
}

function renderUserList() {
  if (!el.userList || !el.userCount) return;
  const scrollTop = el.userList.scrollTop;
  el.userCount.textContent = `${state.allUsers.length}명`;
  el.userList.innerHTML = '';
  if (!state.allUsers.length) {
    const empty = document.createElement('div');
    empty.className = 'user-empty';
    empty.textContent = '온라인 유저 없음';
    el.userList.appendChild(empty);
    return;
  }
  const users = state.allUsers.slice().sort((a, b) =>
    (Number(a.depth) - Number(b.depth)) ||
    (Number(Boolean(a.dead)) - Number(Boolean(b.dead))) ||
    String(a.name || a.playerKey).localeCompare(String(b.name || b.playerKey), 'ko'));
  for (const user of users) {
    const row = document.createElement('div');
    row.className = 'user-row' +
      (user.dead ? ' dead' : '') +
      (state.selectedPlayer === user.playerKey ? ' selected' : '');
    row.title = `${user.name || user.playerKey}\n레이어 ${user.depth} · (${user.blockX}, ${user.blockY})`;
    const avatar = document.createElement('div');
    avatar.className = 'user-avatar';
    avatar.appendChild(headElement(user, 30));
    const main = document.createElement('div');
    main.className = 'user-main';
    const name = document.createElement('div');
    name.className = 'user-name';
    name.textContent = (user.dead ? '☠ ' : '') + (user.name || user.playerKey);
    const layer = document.createElement('div');
    layer.className = 'user-layer';
    layer.textContent = user.dead ? '사망' : '온라인';
    main.append(name, layer);
    const badge = document.createElement('span');
    badge.className = 'user-layer-badge';
    badge.textContent = `L${user.depth}`;
    row.append(avatar, main, badge);
    row.addEventListener('click', () => focusUser(user));
    el.userList.appendChild(row);
  }
  el.userList.scrollTop = scrollTop;
}

function focusUser(user) {
  if (!user) return;
  if (state.selected !== user.instanceKey) {
    selectInstance(user.instanceKey);
    state.pendingFocus = user;
    state.pendingPlayerDetail = user.playerKey;
    return;
  }
  state.pendingPlayerDetail = user.playerKey;
  focusPlayerAt(user);
}

function focusPlayerAt(user) {
  if (!state.worldReady || !state.width || !state.height || !state.mapWidthPx || !state.mapHeightPx) {
    state.pendingFocus = user;
    return;
  }
  state.view.zoom = state.view.max;
  const mapX = (Number(user.blockX) || 0) / state.width * state.mapWidthPx;
  const mapY = (Number(user.blockY) || 0) / state.height * state.mapHeightPx;
  state.view.panX = el.mapViewport.clientWidth / 2 - mapX * state.view.zoom;
  state.view.panY = el.mapViewport.clientHeight / 2 - mapY * state.view.zoom;
  state.view.userZoomed = true;
  state.selectedPlayer = user.playerKey;
  applyView();
  renderMarkers();
  renderUserList();
  if (state.players.some(player => player.playerKey === user.playerKey)) {
    state.pendingPlayerDetail = '';
    openDetail(user.playerKey);
  }
  queueObjectQuery(80);
  state.pendingFocus = null;
}

async function loadPlayers() {
  if (!state.selected || state.playersLoading) return;
  state.playersLoading = true;
  const key = state.selected;
  try {
    const response = await getJson(`/api/instances/${encodeURIComponent(key)}/players`);
    if (state.selected !== key) return;
    state.players = response?.players || [];
    state.spawn = response?.hasSpawn
      ? { x: Number(response.spawnBlockX) || 0, y: Number(response.spawnBlockY) || 0 }
      : null;
    renderMarkers();
    updateSpawnMarker();
    if (state.pendingPlayerDetail) {
      const pendingKey = state.pendingPlayerDetail;
      state.pendingPlayerDetail = '';
      if (state.players.some(player => player.playerKey === pendingKey)) openDetail(pendingKey);
    }
  } catch (error) {
    console.error('players', error);
  } finally {
    state.playersLoading = false;
  }
}

function isChatNearBottom() {
  const element = el.chatHistory;
  return element.scrollHeight - element.scrollTop - element.clientHeight < 40;
}

function chatTime(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function renderChat(scrollToBottom = false) {
  el.chatHistory.innerHTML = '';
  if (!state.chatMessages.length) {
    const empty = document.createElement('div');
    empty.className = 'chat-empty';
    empty.textContent = '채팅 내역이 없습니다.';
    el.chatHistory.appendChild(empty);
    return;
  }

  for (const message of state.chatMessages) {
    const source = message.source || 'game';
    const row = document.createElement('div');
    row.className = `chat-message ${source}`;
    const time = document.createElement('span');
    time.className = 'chat-time';
    time.textContent = chatTime(message.at);
    row.appendChild(time);

    const badge = document.createElement('span');
    badge.className = `chat-badge ${source}`;
    let name = message.speaker || '';
    let text = message.text || '';
    if (source === 'web') {
      badge.textContent = '[WEB]';
      name = '';
    } else if (source === 'discord') {
      badge.textContent = '[D]';
    } else if (source === 'command') {
      badge.textContent = '[CMD]';
      name = '';
    } else {
      badge.textContent = `[${message.layer || '?'}]`;
    }
    row.appendChild(badge);

    if (name) {
      const nameElement = document.createElement('span');
      nameElement.className = 'chat-name';
      nameElement.textContent = `${name}:`;
      if (/^#[0-9a-f]{6}$/i.test(message.color || '')) {
        nameElement.style.color = message.color;
      }
      row.appendChild(nameElement);
    }

    const textElement = document.createElement('span');
    textElement.className = 'chat-text';
    textElement.textContent = text;
    row.appendChild(textElement);
    el.chatHistory.appendChild(row);
  }

  if (scrollToBottom) {
    el.chatHistory.scrollTop = el.chatHistory.scrollHeight;
  }
}

async function loadChat() {
  if (state.chatLoading) return;
  state.chatLoading = true;
  try {
    const response = await getJson(`/api/chat?since=${state.chatLastId}`);
    if (!response) return;
    if (response.lastId < state.chatLastId) {
      state.chatMessages = [];
      state.chatLastId = 0;
    }
    if (response.messages?.length) {
      const stick = isChatNearBottom();
      for (const message of response.messages) {
        if (!state.chatMessages.some(existing => existing.id === message.id)) {
          state.chatMessages.push(message);
        }
      }
      state.chatMessages.sort((a, b) => a.id - b.id);
      if (state.chatMessages.length > 200) {
        state.chatMessages = state.chatMessages.slice(-200);
      }
      state.chatLastId = Math.max(state.chatLastId, response.lastId);
      renderChat(stick);
    } else if (!state.chatMessages.length) {
      renderChat();
    }
  } catch (error) {
    console.error('chat history', error);
  } finally {
    state.chatLoading = false;
  }
}

async function loadCommands() {
  try {
    const commands = await getJson('/api/chat/commands');
    state.commands = Array.isArray(commands) ? commands : [];
  } catch (error) {
    console.error('commands', error);
  }
}

function hideAutocomplete() {
  state.autocompleteOpen = false;
  state.autocompleteItems = [];
  state.autocompleteIndex = 0;
  el.chatAutocomplete.classList.add('hidden');
  el.chatAutocomplete.innerHTML = '';
}

function renderAutocomplete() {
  el.chatAutocomplete.innerHTML = '';
  state.autocompleteItems.forEach((command, index) => {
    const item = document.createElement('div');
    item.className = 'chat-autocomplete-item' + (index === state.autocompleteIndex ? ' active' : '');
    const name = document.createElement('span');
    name.className = 'chat-autocomplete-name';
    name.textContent = `/${command.name}`;
    const usage = document.createElement('span');
    usage.className = 'chat-autocomplete-usage';
    usage.textContent = command.usage || '';
    const description = document.createElement('span');
    description.className = 'chat-autocomplete-desc';
    description.textContent = command.description || '';
    item.appendChild(name);
    item.appendChild(usage);
    item.appendChild(description);
    item.addEventListener('click', () => applyAutocomplete(command));
    el.chatAutocomplete.appendChild(item);
  });
  el.chatAutocomplete.classList.remove('hidden');
}

function updateAutocomplete() {
  const value = el.chatInput.value;
  if (!value.startsWith('/') || value.includes(' ')) {
    hideAutocomplete();
    return;
  }
  const query = value.slice(1).toLowerCase();
  const matches = state.commands
    .filter(command => command.name.toLowerCase().startsWith(query))
    .slice(0, 8);
  if (!matches.length) {
    hideAutocomplete();
    return;
  }
  state.autocompleteOpen = true;
  state.autocompleteItems = matches;
  state.autocompleteIndex = 0;
  renderAutocomplete();
}

function applyAutocomplete(command) {
  el.chatInput.value = `/${command.name} `;
  hideAutocomplete();
  el.chatInput.focus();
}

async function sendCommand(command) {
  if (state.chatSending) return;
  state.chatSending = true;
  el.chatInput.disabled = true;
  el.chatSend.disabled = true;
  try {
    const response = await fetch(`${API}/api/chat/command`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command }),
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const message = await response.json();
    if (message?.id) {
      state.chatMessages.push(message);
      state.chatMessages.sort((a, b) => a.id - b.id);
      state.chatLastId = Math.max(state.chatLastId, message.id);
      renderChat(true);
    }
    el.chatInput.value = '';
  } catch (error) {
    console.error('command send', error);
  } finally {
    state.chatSending = false;
    el.chatInput.disabled = false;
    el.chatSend.disabled = false;
    el.chatInput.focus();
  }
}

async function sendChat(event) {
  event.preventDefault();
  if (state.chatSending) return;
  const text = el.chatInput.value.trim();
  if (!text) return;
  hideAutocomplete();
  if (text.startsWith('/')) {
    await sendCommand(text);
    return;
  }

  state.chatSending = true;
  el.chatInput.disabled = true;
  el.chatSend.disabled = true;
  try {
    const response = await fetch(`${API}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: text }),
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const message = await response.json();
    if (message?.id) {
      state.chatMessages.push(message);
      state.chatMessages.sort((a, b) => a.id - b.id);
      state.chatLastId = Math.max(state.chatLastId, message.id);
      renderChat(true);
    }
    el.chatInput.value = '';
  } catch (error) {
    console.error('chat send', error);
  } finally {
    state.chatSending = false;
    el.chatInput.disabled = false;
    el.chatSend.disabled = false;
    el.chatInput.focus();
  }
}

function renderLayers() {
  el.layers.innerHTML = '';
  for (const instance of state.instances) {
    const button = document.createElement('button');
    const depth = Number(instance.depth) || 0;
    button.textContent = depth > 0 ? `L${depth}` : instance.instanceKey;
    button.title = instance.instanceKey;
    if (instance.instanceKey === state.selected) button.classList.add('active');
    button.addEventListener('click', () => selectInstance(instance.instanceKey));
    el.layers.appendChild(button);
  }
}

function xpeHeadCanvas(player, size) {
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const context = canvas.getContext('2d');
  context.imageSmoothingEnabled = false;
  if (!catalog?.headAtlas) return canvas;
  const head = catalog.headsByName.get(player.headSprite) || catalog.headsByName.get('experimentHeadBack');
  const eyes = catalog.headsByName.get(player.eyeSprite);
  const layers = [head, eyes].filter(Boolean);
  if (!layers.length) return canvas;
  const baseWidth = Math.max(1, ...layers.map(layer => layer.w));
  const baseHeight = Math.max(1, ...layers.map(layer => layer.h));
  const scale = Math.min(size / baseWidth, size / baseHeight);
  const offsetX = (size - baseWidth * scale) / 2;
  const offsetY = (size - baseHeight * scale) / 2;
  for (const layer of layers) {
    context.drawImage(
      catalog.headAtlas,
      layer.x, layer.y, layer.w, layer.h,
      offsetX, offsetY, layer.w * scale, layer.h * scale);
  }
  return canvas;
}

function headElement(player, size) {
  if (state.showSteam && player.avatarUrl) {
    const image = document.createElement('img');
    image.src = player.avatarUrl;
    image.alt = '';
    image.loading = 'lazy';
    image.addEventListener('error', () => {
      image.replaceWith(xpeHeadCanvas(player, size));
    });
    return image;
  }
  return xpeHeadCanvas(player, size);
}

function itemCanvas(id, size) {
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const context = canvas.getContext('2d');
  context.imageSmoothingEnabled = false;
  const item = catalog?.itemsById.get(id);
  if (item && catalog?.itemAtlas) {
    context.drawImage(catalog.itemAtlas, item.x, item.y, item.w, item.h, 0, 0, size, size);
  }
  return canvas;
}

function objectIconCanvas(name, size) {
  const canvas = document.createElement('canvas');
  canvas.width = size;
  canvas.height = size;
  const context = canvas.getContext('2d');
  context.imageSmoothingEnabled = false;
  const entry = catalog?.objectsByName.get(name);
  if (entry && catalog?.objectAtlas) {
    context.drawImage(catalog.objectAtlas, entry.x, entry.y, entry.w, entry.h, 0, 0, size, size);
  }
  return canvas;
}

const OBJECT_COLORS = {
  trader: '#f0c674',
  building: '#8ab4f8',
  container: '#c792ea',
  animal: '#7ee787',
  item: '#79c0ff',
};

function objectScreenPoint(blockX, blockY) {
  return {
    x: state.view.panX + (blockX / Math.max(1, state.width)) * state.mapWidthPx * state.view.zoom,
    y: state.view.panY + (blockY / Math.max(1, state.height)) * state.mapHeightPx * state.view.zoom,
  };
}

function visibleObjectBounds() {
  const scaleX = state.mapWidthPx / Math.max(1, state.width);
  const scaleY = state.mapHeightPx / Math.max(1, state.height);
  const zoom = state.view.zoom;
  const viewportWidth = el.mapViewport.clientWidth;
  const viewportHeight = el.mapViewport.clientHeight;
  return {
    minX: Math.max(0, Math.floor((-state.view.panX / zoom) / scaleX) - 1),
    maxX: Math.min(state.width, Math.ceil((viewportWidth - state.view.panX) / zoom / scaleX) + 1),
    minY: Math.max(0, Math.floor((-state.view.panY / zoom) / scaleY) - 1),
    maxY: Math.min(state.height, Math.ceil((viewportHeight - state.view.panY) / zoom / scaleY) + 1),
  };
}

function objectCellForZoom() {
  const pixelsPerBlock = state.tileSize * state.view.zoom;
  if (pixelsPerBlock < 1.5) return 32;
  if (pixelsPerBlock < 3) return 16;
  if (pixelsPerBlock < 5) return 8;
  return 0;
}

function objectIconSize(category) {
  if (category === 'trader') return 26;
  if (category === 'building') return 22;
  if (category === 'container') return 20;
  if (category === 'animal') return 18;
  return 10;
}

function objectColor(category) {
  return OBJECT_COLORS[category] || '#8b949e';
}

function clusterColor(cluster) {
  const entries = Object.entries(cluster.categories || {});
  if (!entries.length) return '#8b949e';
  entries.sort((a, b) => b[1] - a[1]);
  return objectColor(entries[0][0]);
}

function drawObjectLayer() {
  const canvas = el.objectCanvas;
  const width = el.mapViewport.clientWidth;
  const height = el.mapViewport.clientHeight;
  if (!width || !height) return;
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }
  const context = canvas.getContext('2d');
  context.clearRect(0, 0, width, height);
  if (!state.worldReady || !state.width || !state.height) return;

  context.textAlign = 'center';
  context.textBaseline = 'middle';
  context.font = '700 11px ui-sans-serif, system-ui, sans-serif';

  for (const cluster of state.objectClusters) {
    const point = objectScreenPoint(cluster.x, cluster.y);
    const color = clusterColor(cluster);
    context.beginPath();
    context.arc(point.x, point.y, 14, 0, Math.PI * 2);
    context.fillStyle = 'rgba(13,17,23,.88)';
    context.fill();
    context.lineWidth = 2;
    context.strokeStyle = color;
    context.stroke();
    context.fillStyle = color;
    context.fillText(String(cluster.count), point.x, point.y + 0.5);
  }

  for (const object of state.objects) {
    const point = objectScreenPoint(object.blockX, object.blockY);
    const size = objectIconSize(object.category);
    const entry = catalog?.objectsByName.get(object.sprite);
    if (entry && catalog?.objectAtlas) {
      context.drawImage(catalog.objectAtlas, entry.x, entry.y, entry.w, entry.h,
        point.x - size / 2, point.y - size / 2, size, size);
    } else {
      context.beginPath();
      context.arc(point.x, point.y, Math.max(3, size / 3), 0, Math.PI * 2);
      context.fillStyle = objectColor(object.category);
      context.fill();
    }
    if (object.category === 'trader' || object.category === 'building'
      || object.category === 'animal' || object.category === 'item') {
      context.beginPath();
      context.arc(point.x, point.y, size / 2 + 2, 0, Math.PI * 2);
      context.strokeStyle = objectColor(object.category);
      context.lineWidth = 1.5;
      context.stroke();
    }
  }
}

function loadObjectFilter() {
  try {
    const raw = localStorage.getItem(OBJECT_FILTER_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        state.objectFilter = parsed;
      }
    }
  } catch {
  }
}

function saveObjectFilter() {
  try {
    localStorage.setItem(OBJECT_FILTER_KEY, JSON.stringify(state.objectFilter));
  } catch {
  }
}

function isObjectVisible(name) {
  return state.objectFilter[name] !== false;
}

function selectedObjectNames() {
  if (!state.objectNames.length) return null;
  const selected = state.objectNames.filter(isObjectVisible);
  return selected.length === state.objectNames.length ? null : selected;
}

function setAllObjectNames(visible) {
  for (const name of state.objectNames) state.objectFilter[name] = visible;
  saveObjectFilter();
  state.objectQueryGeneration++;
  renderObjectFilter();
  queueObjectQuery(0);
}

function renderObjectFilter() {
  el.objectFilterList.innerHTML = '';
  if (!state.objectNames.length) {
    const empty = document.createElement('div');
    empty.className = 'object-filter-empty';
    empty.textContent = '오브젝트 목록을 불러오는 중...';
    el.objectFilterList.appendChild(empty);
    return;
  }
  for (const name of state.objectNames) {
    const label = document.createElement('label');
    label.className = 'object-filter-item';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = isObjectVisible(name);
    checkbox.addEventListener('change', () => {
      state.objectFilter[name] = checkbox.checked;
      saveObjectFilter();
      state.objectQueryGeneration++;
      queueObjectQuery(0);
    });
    const text = document.createElement('span');
    text.textContent = name;
    text.title = name;
    label.appendChild(checkbox);
    label.appendChild(text);
    el.objectFilterList.appendChild(label);
  }
}

function toggleObjectFilter(force) {
  const open = typeof force === 'boolean' ? force : !state.objectFilterOpen;
  state.objectFilterOpen = open;
  el.objectFilterPanel.classList.toggle('hidden', !open);
  el.objectFilterButton.classList.toggle('active', open);
  el.objectFilterButton.setAttribute('aria-pressed', String(open));
  if (open) renderObjectFilter();
}

async function loadObjects() {
  if (!state.selected || !state.worldReady) return;
  const cell = objectCellForZoom();
  const categories = ['building', 'container', 'trader', 'animal'];
  if (cell === 0) categories.push('item');
  if (state.objectQueryLoading) {
    state.objectQueryPending = true;
    return;
  }
  state.objectQueryLoading = true;
  const generation = state.objectQueryGeneration;
  const instanceKey = state.selected;
  const bounds = visibleObjectBounds();
  const names = selectedObjectNames();
  let url = `/api/instances/${encodeURIComponent(instanceKey)}/objects?minX=${bounds.minX}&minY=${bounds.minY}` +
    `&maxX=${bounds.maxX}&maxY=${bounds.maxY}&cell=${cell}&maxObjects=2500` +
    `&categories=${encodeURIComponent(categories.join(','))}`;
  if (names !== null) {
    url += `&names=${encodeURIComponent(names.join(','))}`;
  }
  try {
    const response = await getJson(url);
    if (state.selected !== instanceKey || generation !== state.objectQueryGeneration) return;
    state.objects = response?.objects || [];
    state.objectClusters = response?.clusters || [];
    state.objectCell = response?.cell ?? cell;
    const responseNames = Array.isArray(response?.names) ? response.names : [];
    const signature = responseNames.join('\u0001');
    if (signature !== state.objectNamesSignature) {
      state.objectNames = responseNames;
      state.objectNamesSignature = signature;
      if (state.objectFilterOpen) renderObjectFilter();
    }
    drawObjectLayer();
  } catch (error) {
    console.error('objects', error);
  } finally {
    state.objectQueryLoading = false;
    if (state.objectQueryPending) {
      state.objectQueryPending = false;
      queueObjectQuery(100);
    }
  }
}

function queueObjectQuery(delay = 250) {
  if (state.objectQueryTimer) clearTimeout(state.objectQueryTimer);
  state.objectQueryTimer = setTimeout(() => {
    state.objectQueryTimer = null;
    loadObjects();
  }, delay);
}

function objectAtScreen(clientX, clientY) {
  const rect = el.mapViewport.getBoundingClientRect();
  const x = clientX - rect.left;
  const y = clientY - rect.top;
  let nearest = null;
  let nearestDistance = Infinity;

  for (const cluster of state.objectClusters) {
    const point = objectScreenPoint(cluster.x, cluster.y);
    const distance = Math.hypot(point.x - x, point.y - y);
    if (distance <= 18 && distance < nearestDistance) {
      nearest = { type: 'cluster', data: cluster };
      nearestDistance = distance;
    }
  }
  for (const object of state.objects) {
    const point = objectScreenPoint(object.blockX, object.blockY);
    const size = objectIconSize(object.category);
    if (Math.abs(point.x - x) <= size / 2 + 4 && Math.abs(point.y - y) <= size / 2 + 4) {
      const distance = Math.hypot(point.x - x, point.y - y);
      if (distance < nearestDistance) {
        nearest = { type: 'object', data: object };
        nearestDistance = distance;
      }
    }
  }
  return nearest;
}

function zoomToCluster(cluster) {
  const mapX = (cluster.x / Math.max(1, state.width)) * state.mapWidthPx;
  const mapY = (cluster.y / Math.max(1, state.height)) * state.mapHeightPx;
  const viewportWidth = el.mapViewport.clientWidth;
  const viewportHeight = el.mapViewport.clientHeight;
  state.view.zoom = clampZoom(Math.max(state.view.zoom * 1.8, state.view.cover * 1.5));
  state.view.panX = viewportWidth / 2 - mapX * state.view.zoom;
  state.view.panY = viewportHeight / 2 - mapY * state.view.zoom;
  state.view.userZoomed = true;
  applyView();
  queueObjectQuery(80);
}

function markerHeadKey(player) {
  return `${state.showSteam ? 1 : 0}|${player.avatarUrl || ''}|${player.headSprite || ''}|${player.eyeSprite || ''}`;
}

function updateMarkerHead(marker, player) {
  const key = markerHeadKey(player);
  if (marker._headKey === key) return;
  marker._headKey = key;
  const dot = marker.querySelector('.dot');
  if (!dot) return;
  dot.innerHTML = '';
  dot.appendChild(headElement(player, 38));
}

function createMarker(player) {
  const marker = document.createElement('div');
  marker.dataset.playerKey = player.playerKey;
  const dot = document.createElement('span');
  dot.className = 'dot';
  const label = document.createElement('span');
  label.className = 'label';
  marker.appendChild(dot);
  marker.appendChild(label);
  marker.addEventListener('click', () => {
    if (marker._player) selectPlayer(marker._player.playerKey);
  });
  return marker;
}

function renderMarkers() {
  if (!state.worldReady || !state.width || !state.height) {
    el.markers.innerHTML = '';
    return;
  }

  const existing = new Map();
  for (const marker of Array.from(el.markers.children)) {
    existing.set(marker.dataset.playerKey, marker);
  }

  const seen = new Set();
  for (const player of state.players) {
    let marker = existing.get(player.playerKey);
    if (!marker) marker = createMarker(player);
    marker._player = player;
    marker.className = 'marker' + (player.dead ? ' dead' : '') +
      (state.selectedPlayer === player.playerKey ? ' highlight' : '');
    marker.title = `${player.name || player.playerKey}${player.dead ? ' (사망)' : ''}\n(${player.blockX}, ${player.blockY})`;
    const label = marker.querySelector('.label');
    if (label) label.textContent = (player.dead ? '☠ ' : '') + (player.name || player.playerKey);
    updateMarkerHead(marker, player);
    seen.add(player.playerKey);
    el.markers.appendChild(marker);
  }

  for (const [key, marker] of existing) {
    if (!seen.has(key)) marker.remove();
  }
  updateMarkerPositions();
}

function selectPlayer(playerKey) {
  state.selectedPlayer = playerKey;
  renderMarkers();
  openDetail(playerKey);
}

function openDetail(playerKey) {
  const player = state.players.find(candidate => candidate.playerKey === playerKey);
  if (!player) return;
  state.detailMode = 'player';
  state.selectedObject = '';
  state.objectDetail = null;
  state.detailOpen = true;
  state.detail = null;
  el.playerDetail.classList.remove('hidden');
  el.objectDetail.classList.add('hidden');
  el.detailDrawer.classList.add('open');
  el.detailName.textContent = player.name || player.playerKey;
  el.detailSub.textContent = player.steamId ? `Steam ${player.steamId}` : player.playerKey;
  el.detailHead.innerHTML = '';
  el.detailHead.appendChild(headElement(player, 52));
  el.detailBody.scrollTop = 0;
  el.detailHealth.innerHTML = '<div class="detail-empty">불러오는 중...</div>';
  el.detailSkills.innerHTML = '<div class="detail-empty">불러오는 중...</div>';
  el.detailLimbs.innerHTML = '';
  el.detailInventory.innerHTML = '';
  loadDetail(playerKey);
  if (state.detailTimer) clearInterval(state.detailTimer);
  state.detailTimer = setInterval(() => loadDetail(playerKey), DETAIL_MS);
}

function closeDetail() {
  state.detailOpen = false;
  state.detail = null;
  state.objectDetail = null;
  state.detailMode = '';
  state.selectedPlayer = '';
  state.selectedObject = '';
  if (state.detailTimer) {
    clearInterval(state.detailTimer);
    state.detailTimer = null;
  }
  el.playerDetail.classList.remove('hidden');
  el.objectDetail.classList.add('hidden');
  el.detailDrawer.classList.remove('open');
  renderMarkers();
}

async function loadDetail(playerKey) {
  if (!state.selected || !state.detailOpen || state.detailMode !== 'player' || state.detailLoading) return;
  state.detailLoading = true;
  const instanceKey = state.selected;
  try {
    const detail = await getJson(
      `/api/instances/${encodeURIComponent(instanceKey)}/players/${encodeURIComponent(playerKey)}/detail`);
    if (!state.detailOpen || state.selectedPlayer !== playerKey) return;
    if (detail) {
      state.detail = detail;
      renderDetail(detail);
    } else {
      el.detailHealth.innerHTML = '<div class="detail-error">상세 정보를 불러오지 못했습니다.</div>';
    }
  } catch (error) {
    if (state.detailOpen && state.selectedPlayer === playerKey) {
      el.detailHealth.innerHTML = `<div class="detail-error">${String(error.message || error)}</div>`;
    }
  } finally {
    state.detailLoading = false;
  }
}

function renderDetail(detail) {
  state.detailMode = 'player';
  el.playerDetail.classList.remove('hidden');
  el.objectDetail.classList.add('hidden');
  el.detailName.textContent = detail.name || detail.playerKey;
  el.detailSub.textContent = detail.steamId
    ? `Steam ${detail.steamId} · (${detail.blockX}, ${detail.blockY})`
    : `(${detail.blockX}, ${detail.blockY})`;
  el.detailHead.innerHTML = '';
  const player = state.players.find(candidate => candidate.playerKey === detail.playerKey);
  el.detailHead.appendChild(headElement(player ? { ...detail, avatarUrl: player.avatarUrl } : detail, 52));
  renderHealth(detail.health || {});
  renderSkills(detail.skills || {});
  renderLimbs(detail.limbs || []);
  renderInventory(detail.inventory || []);
}

function categoryLabel(category) {
  return {
    building: '건물',
    container: '컨테이너',
    trader: '트레이더',
    animal: '동물',
    item: '아이템',
  }[category] || category;
}

function preferenceLabel(preference) {
  return {
    WantsTrade: '원함',
    Indifferent: '무관',
    WantsKeep: '유지',
  }[preference] || preference;
}

function objectRow(label, value) {
  const row = document.createElement('div');
  row.className = 'object-row';
  const name = document.createElement('span');
  name.textContent = label;
  const content = document.createElement('b');
  content.textContent = value;
  row.appendChild(name);
  row.appendChild(content);
  return row;
}

function openObjectDetail(objectKey) {
  const object = state.objects.find(candidate => String(candidate.key) === String(objectKey));
  if (!object) return;
  state.detailMode = 'object';
  state.detailOpen = true;
  state.selectedObject = String(objectKey);
  state.selectedPlayer = '';
  state.detail = null;
  state.objectDetail = null;
  el.detailDrawer.classList.add('open');
  el.playerDetail.classList.add('hidden');
  el.objectDetail.classList.remove('hidden');
  el.traderSection.classList.add('hidden');
  el.traderStockSection.classList.add('hidden');
  el.detailName.textContent = object.name || object.id || `Object ${object.key}`;
  el.detailSub.textContent = `${categoryLabel(object.category)} · (${object.blockX}, ${object.blockY})`;
  el.detailHead.innerHTML = '';
  el.detailHead.appendChild(objectIconCanvas(object.sprite, 52));
  el.detailBody.scrollTop = 0;
  el.objectInfo.innerHTML = '<div class="detail-empty">불러오는 중...</div>';
  el.traderInfo.innerHTML = '';
  el.traderStock.innerHTML = '';
  loadObjectDetail(objectKey);
  if (state.detailTimer) clearInterval(state.detailTimer);
  state.detailTimer = setInterval(() => loadObjectDetail(objectKey), DETAIL_MS);
}

async function loadObjectDetail(objectKey) {
  if (!state.selected || !state.detailOpen || state.detailMode !== 'object' || state.objectDetailLoading) return;
  state.objectDetailLoading = true;
  const instanceKey = state.selected;
  try {
    const detail = await getJson(
      `/api/instances/${encodeURIComponent(instanceKey)}/objects/${encodeURIComponent(objectKey)}/detail`);
    if (!state.detailOpen || state.detailMode !== 'object' || state.selectedObject !== String(objectKey)) return;
    if (detail) {
      state.objectDetail = detail;
      renderObjectDetail(detail);
    } else {
      el.objectInfo.innerHTML = '<div class="detail-error">오브젝트 정보를 불러오지 못했습니다.</div>';
    }
  } catch (error) {
    if (state.detailOpen && state.detailMode === 'object') {
      el.objectInfo.innerHTML = `<div class="detail-error">${String(error.message || error)}</div>`;
    }
  } finally {
    state.objectDetailLoading = false;
  }
}

function renderObjectDetail(detail) {
  el.detailName.textContent = detail.name || detail.id || `Object ${detail.key}`;
  el.detailSub.textContent = `${categoryLabel(detail.category)} · (${detail.blockX}, ${detail.blockY})`;
  el.detailHead.innerHTML = '';
  el.detailHead.appendChild(objectIconCanvas(detail.sprite, 52));

  el.objectInfo.innerHTML = '';
  el.objectInfo.appendChild(objectRow('종류', categoryLabel(detail.category)));
  if (detail.id) el.objectInfo.appendChild(objectRow('ID', detail.id));
  el.objectInfo.appendChild(objectRow('위치', `${detail.blockX}, ${detail.blockY}`));
  if (Number(detail.condition) >= 0) {
    el.objectInfo.appendChild(objectRow('상태', `${Math.round(Number(detail.condition) * 100)}%`));
  } else {
    el.objectInfo.appendChild(objectRow('체력', Number(detail.health || 0).toFixed(0)));
  }
  if (detail.description) {
    const description = document.createElement('div');
    description.className = 'object-desc';
    description.textContent = detail.description;
    el.objectInfo.appendChild(description);
  }

  if (detail.trader) {
    el.traderSection.classList.remove('hidden');
    el.traderInfo.innerHTML = '';
    const trader = detail.trader;
    el.traderInfo.appendChild(objectRow('체력', Number(detail.health || 0).toFixed(0)));
    el.traderInfo.appendChild(objectRow('Reputation', `${Number(trader.actualReputation || 0).toFixed(0)} (${Number(trader.reputation || 0).toFixed(0)})`));
    el.traderInfo.appendChild(objectRow('적대', trader.hostile ? '예' : '아니오'));
    el.traderInfo.appendChild(objectRow('교역 가치', `${trader.valueGiven} / ${trader.maxValueGiven}`));
    el.traderInfo.appendChild(objectRow('누적 교역', `${trader.totalValueGiven} / ${trader.maxValueGiven}`));

    const stock = trader.items || [];
    if (stock.length) {
      el.traderStockSection.classList.remove('hidden');
      el.traderStock.innerHTML = '';
      for (const item of stock) el.traderStock.appendChild(traderItemRow(item));
    } else {
      el.traderStockSection.classList.add('hidden');
    }
  } else {
    el.traderSection.classList.add('hidden');
    el.traderStockSection.classList.add('hidden');
  }
}

function traderItemRow(item) {
  const row = document.createElement('div');
  row.className = 'inventory-row';
  const icon = document.createElement('div');
  icon.className = 'inventory-icon';
  icon.appendChild(itemCanvas(item.id, 30));
  const main = document.createElement('div');
  main.className = 'inventory-main';
  const name = document.createElement('div');
  name.className = 'inventory-name';
  name.textContent = item.name || item.id;
  const sub = document.createElement('div');
  sub.className = 'inventory-sub';
  sub.textContent = `${preferenceLabel(item.preference)} · 가치 ${item.value}`;
  main.appendChild(name);
  main.appendChild(sub);
  const price = document.createElement('div');
  price.className = 'inventory-cond trader-price';
  price.textContent = item.price > 0 ? `${item.price}` : '무료';
  row.appendChild(icon);
  row.appendChild(main);
  row.appendChild(price);
  return row;
}

function healthRows(health) {
  const number = key => {
    const value = Number(health[key]);
    return Number.isFinite(value) ? value : 0;
  };
  const clamp = value => Math.max(0, Math.min(100, value));
  const whole = value => String(Math.round(value));
  const percentText = value => `${Math.round(value)}%`;
  const row = (label, text, bar, status = '') => ({ label, text, bar: clamp(bar), status });
  const status = (bad, warn) => bad ? 'bad' : warn ? 'warn' : '';

  const brain = number('brainHealth');
  const consciousness = number('consciousness');
  const pain = number('pain');
  const blood = number('bloodVolume');
  const bleeding = number('bleeding');
  const bleedLpm = bleeding * 1.5;
  const oxygen = number('bloodOxygen');
  const immunity = number('immunity');
  const hunger = number('hunger');
  const thirst = number('thirst');
  const sickness = number('sickness');
  const respiratory = number('respiratoryRate');
  const energy = number('energy');
  const happiness = number('happiness');
  const radiation = number('radiationSickness');
  const weightOffset = number('weightOffset');
  const weightKg = 50 + weightOffset * 0.34;
  const weightText = Number(weightKg.toFixed(1)).toFixed(1).replace(/\.0$/, '');
  const temperature = number('temperature');
  const temperatureText = String(Number((Math.round(temperature * 10) / 10).toFixed(1)));
  const heartRate = number('heartRate');
  const bloodPressure = number('bloodPressure');
  const defib = number('defibShockedFrames') > 0;
  const pressureBad = bloodPressure > 160 || bloodPressure < 75;
  const pressureWarn = bloodPressure > 150 || bloodPressure < 90;
  const cardiac = Boolean(health.inCardiacArrest);

  return [
    row('뇌', whole(brain), brain, status(brain < 50, brain < 70)),
    row('의식', percentText(consciousness), consciousness, status(consciousness < 40, consciousness < 60)),
    row('통증', percentText(pain), pain, status(pain > 75, pain > 50)),
    row('혈액', `${(2.5 + blood * 0.025).toFixed(2)}l`, blood, status(blood < 25, blood < 40)),
    row('출혈', bleeding > 0.0001 ? `${bleedLpm.toFixed(2)}l/m` : '-', bleeding * 1000,
      status(bleeding > 0.06, bleeding > 0.02)),
    row('산소', percentText(oxygen), oxygen, status(oxygen < 70, oxygen < 90)),
    row('면역', percentText(immunity), immunity / 2, status(immunity < 50, immunity < 75)),
    row('배고픔', whole(hunger), hunger, status(hunger < 10, hunger < 25)),
    row('갈증', whole(thirst), thirst, status(thirst < 10, thirst < 25)),
    row('질병', percentText(sickness), sickness, status(sickness > 70, sickness > 40)),
    row('호흡', `${Math.round(respiratory * 0.25)}/m`, respiratory, status(respiratory < 0.5, respiratory < 1)),
    row('에너지', percentText(energy), energy, status(energy < 5, energy < 20)),
    row('행복', happiness.toFixed(1), (happiness + 100) / 2, status(happiness <= -75, happiness < -40)),
    row('방사능', `${(radiation * 0.3).toFixed(1)}gy`, radiation, status(radiation > 25, radiation > 10)),
    row('체중', `${weightText}kg`, ((weightOffset + 80) / 180) * 100,
      status(Math.abs(weightOffset) > 55, Math.abs(weightOffset) > 40)),
    row('체온', `${temperatureText}'c`, ((temperature - 28) / 12.5) * 100,
      status(temperature < 30 || temperature > 41, temperature < 35 || temperature > 39)),
    row('심박/혈압', `${defib ? '???' : Math.round(heartRate)} | ${Math.round(bloodPressure)}/${Math.round(bloodPressure * 0.66)}`,
      (heartRate / 180) * 100,
      status(cardiac || pressureBad, pressureWarn || heartRate < 50 || heartRate > 110)),
    row('스태미나', percentText(number('stamina')), number('stamina'), status(number('stamina') < 20)),
    row('흉혈', percentText(number('hemothorax')), number('hemothorax'), status(number('hemothorax') > 50, number('hemothorax') > 20)),
  ];
}

function renderHealth(health) {
  el.detailHealth.innerHTML = '';
  for (const entry of healthRows(health || {})) {
    const row = document.createElement('div');
    row.className = `health-row${entry.status ? ` ${entry.status}` : ''}`;
    const label = document.createElement('div');
    label.className = 'health-label';
    label.textContent = entry.label;
    const track = document.createElement('div');
    track.className = 'health-track';
    const fill = document.createElement('div');
    fill.className = 'health-fill';
    fill.style.width = `${entry.bar}%`;
    track.appendChild(fill);
    const value = document.createElement('div');
    value.className = 'health-value';
    value.textContent = entry.text;
    row.append(label, track, value);
    el.detailHealth.appendChild(row);
  }
}

function renderSkills(skills) {
  el.detailSkills.innerHTML = '';
  const entries = [
    ['STR', skills.str],
    ['RES', skills.res],
    ['INT', skills.int],
  ];
  for (const [label, skill] of entries) {
    const level = Math.max(0, Number(skill?.level) || 0);
    const min = Math.max(0, Number(skill?.minExp) || 0);
    const max = Math.max(min + 1, Number(skill?.maxExp) || min + 1);
    const exp = Math.max(min, Number(skill?.exp) || min);
    const current = Math.max(0, Math.round(exp - min));
    const needed = Math.max(1, max - min);
    const remaining = Math.max(0, Math.round(max - exp));
    const percent = Math.max(0, Math.min(100, current / needed * 100));
    const row = document.createElement('div');
    row.className = 'skill-row';
    const head = document.createElement('div');
    head.className = 'skill-head';
    const name = document.createElement('span');
    name.className = 'skill-label';
    name.textContent = label;
    const value = document.createElement('span');
    value.className = 'skill-level';
    value.textContent = String(level);
    head.append(name, value);
    const track = document.createElement('div');
    track.className = 'skill-track';
    const fill = document.createElement('div');
    fill.className = 'skill-fill';
    fill.style.width = `${percent.toFixed(1)}%`;
    track.appendChild(fill);
    const meta = document.createElement('div');
    meta.className = 'skill-meta';
    const points = document.createElement('span');
    points.textContent = `${current.toLocaleString()} / ${needed.toLocaleString()}`;
    const left = document.createElement('span');
    left.textContent = `${remaining.toLocaleString()} 남음`;
    meta.append(points, left);
    row.append(head, track, meta);
    el.detailSkills.appendChild(row);
  }
}

function renderLimbs(limbs) {
  el.detailLimbs.innerHTML = '';
  if (!limbs.length) {
    el.detailLimbs.innerHTML = '<div class="detail-empty">림브 정보가 없습니다.</div>';
    return;
  }
  for (const limb of limbs) {
    const row = document.createElement('div');
    row.className = 'limb-row';
    const title = document.createElement('div');
    title.className = 'limb-title';
    const name = document.createElement('b');
    name.textContent = limb.name || (limb.isHead ? '머리' : '림브');
    const values = document.createElement('span');
    values.textContent = `피부 ${Number(limb.skinHealth || 0).toFixed(0)} · 근육 ${Number(limb.muscleHealth || 0).toFixed(0)}`;
    title.appendChild(name);
    title.appendChild(values);
    row.appendChild(title);

    const badges = document.createElement('div');
    badges.className = 'limb-badges';
    const flags = [
      ['골절', limb.broken],
      ['탈구', limb.dislocated],
      ['감염', limb.infected],
      ['부목', limb.splinted],
      ['절단', limb.dismembered],
    ];
    for (const [text, active] of flags) {
      if (!active) continue;
      const badge = document.createElement('span');
      badge.className = 'badge bad';
      badge.textContent = text;
      badges.appendChild(badge);
    }
    if (badges.childElementCount) row.appendChild(badges);

    const bars = document.createElement('div');
    bars.className = 'limb-bars';
    const pain = document.createElement('span');
    pain.textContent = `통증 ${Number(limb.pain || 0).toFixed(0)}`;
    const bleed = document.createElement('span');
    bleed.textContent = `출혈 ${Number(limb.bleed || 0).toFixed(2)}`;
    bars.appendChild(pain);
    bars.appendChild(bleed);
    row.appendChild(bars);
    el.detailLimbs.appendChild(row);
  }
}

function renderInventory(items) {
  el.detailInventory.innerHTML = '';
  if (!items.length) {
    el.detailInventory.innerHTML = '<div class="detail-empty">인벤토리가 비어 있습니다.</div>';
    return;
  }
  for (const item of items) el.detailInventory.appendChild(inventoryRow(item));
}

function inventoryRow(item) {
  const row = document.createElement('div');
  row.className = 'inventory-row';
  const icon = document.createElement('div');
  icon.className = 'inventory-icon';
  icon.appendChild(itemCanvas(item.id, 30));
  const main = document.createElement('div');
  main.className = 'inventory-main';
  const name = document.createElement('div');
  name.className = 'inventory-name';
  name.textContent = item.name || item.id;
  const sub = document.createElement('div');
  sub.className = 'inventory-sub';
  sub.textContent = item.wearSlot
    ? `착용 · ${item.wearSlot}`
    : (item.slot >= 0 ? `슬롯 ${item.slot}` : item.id);
  main.appendChild(name);
  main.appendChild(sub);
  const condition = document.createElement('div');
  condition.className = 'inventory-cond';
  condition.textContent = `${Math.round(Number(item.condition || 0) * 100)}%`;
  row.appendChild(icon);
  row.appendChild(main);
  row.appendChild(condition);
  if (item.contents?.length) {
    const contents = document.createElement('div');
    contents.className = 'inventory-contents';
    for (const child of item.contents) contents.appendChild(inventoryRow(child));
    row.appendChild(contents);
  }
  return row;
}

el.detailClose.addEventListener('click', closeDetail);
el.chatInput.addEventListener('input', updateAutocomplete);
el.chatInput.addEventListener('keydown', event => {
  if (!state.autocompleteOpen) return;
  if (event.key === 'ArrowDown') {
    event.preventDefault();
    state.autocompleteIndex = (state.autocompleteIndex + 1) % state.autocompleteItems.length;
    renderAutocomplete();
  } else if (event.key === 'ArrowUp') {
    event.preventDefault();
    state.autocompleteIndex = (state.autocompleteIndex - 1 + state.autocompleteItems.length) % state.autocompleteItems.length;
    renderAutocomplete();
  } else if (event.key === 'Enter' || event.key === 'Tab') {
    event.preventDefault();
    const command = state.autocompleteItems[state.autocompleteIndex];
    if (command) applyAutocomplete(command);
  } else if (event.key === 'Escape') {
    hideAutocomplete();
  }
});
el.chatForm.addEventListener('submit', sendChat);
el.objectFilterAllOn.addEventListener('click', () => setAllObjectNames(true));
el.objectFilterAllOff.addEventListener('click', () => setAllObjectNames(false));
el.objectFilterButton.addEventListener('click', event => {
  event.stopPropagation();
  toggleObjectFilter();
});
document.addEventListener('click', event => {
  if (!state.objectFilterOpen) return;
  if (event.target.closest('#objectFilterPanel') || event.target.closest('#objectFilterButton')) return;
  toggleObjectFilter(false);
});
function setChatVisible(visible) {
  state.chatVisible = visible;
  document.body.classList.toggle('chat-hidden', !visible);
  el.chatToggle.classList.toggle('active', visible);
  el.chatToggle.setAttribute('aria-pressed', String(visible));
  if (!visible) {
    el.chatInput.blur();
    hideAutocomplete();
  }
}

function setUsersVisible(visible) {
  state.usersVisible = visible;
  document.body.classList.toggle('users-hidden', !visible);
  el.usersToggle.classList.toggle('active', visible);
  el.usersToggle.setAttribute('aria-pressed', String(visible));
  if (visible) loadAllUsers();
}

el.chatToggle.addEventListener('click', () => setChatVisible(!state.chatVisible));
el.usersToggle.addEventListener('click', () => setUsersVisible(!state.usersVisible));
el.steamToggle.addEventListener('click', () => {
  state.showSteam = !state.showSteam;
  el.steamToggle.classList.toggle('active', state.showSteam);
  el.steamToggle.setAttribute('aria-pressed', String(state.showSteam));
  renderMarkers();
  renderUserList();
  if (state.detailOpen && state.detail) renderDetail(state.detail);
});
el.zoomIn.addEventListener('click', () => zoomBy(1.25));
el.zoomOut.addEventListener('click', () => zoomBy(0.8));
el.zoomReset.addEventListener('click', resetView);

el.mapViewport.addEventListener('wheel', event => {
  event.preventDefault();
  zoomAt(event.clientX, event.clientY, Math.exp(-event.deltaY * 0.0015));
}, { passive: false });

el.mapViewport.addEventListener('pointerdown', event => {
  if (event.button !== 0) return;
  if (event.target.closest('.marker')) return;
  drag = {
    id: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    panX: state.view.panX,
    panY: state.view.panY,
    moved: false,
  };
  el.mapViewport.classList.add('dragging');
  el.mapViewport.setPointerCapture(event.pointerId);
});

el.mapViewport.addEventListener('pointermove', event => {
  if (!drag || event.pointerId !== drag.id) return;
  const dx = event.clientX - drag.x;
  const dy = event.clientY - drag.y;
  if (Math.abs(dx) > 3 || Math.abs(dy) > 3) drag.moved = true;
  state.view.panX = drag.panX + dx;
  state.view.panY = drag.panY + dy;
  scheduleView();
});

function endDrag(event) {
  if (!drag || event.pointerId !== drag.id) return;
  if (drag.moved) state.suppressClickUntil = performance.now() + 250;
  drag = null;
  el.mapViewport.classList.remove('dragging');
  if (el.mapViewport.hasPointerCapture(event.pointerId)) {
    el.mapViewport.releasePointerCapture(event.pointerId);
  }
}

el.mapViewport.addEventListener('pointerup', endDrag);
el.mapViewport.addEventListener('pointercancel', endDrag);

el.mapViewport.addEventListener('click', event => {
  if (event.target.closest('.marker')) return;
  if (performance.now() < state.suppressClickUntil) return;
  const hit = objectAtScreen(event.clientX, event.clientY);
  if (!hit) return;
  if (hit.type === 'cluster') {
    zoomToCluster(hit.data);
  } else {
    openObjectDetail(hit.data.key);
  }
});

window.addEventListener('keydown', event => {
  if (event.key === 'Escape' && state.detailOpen) closeDetail();
});

window.addEventListener('resize', handleResize);
new ResizeObserver(handleResize).observe(el.mapViewport);
setInterval(() => { loadWorld(false); loadPlayers(); }, POLL_MS);
setInterval(() => { loadInstances(); }, INSTANCE_POLL_MS);
setInterval(loadChat, CHAT_POLL_MS);
setInterval(loadObjects, 60000);
setInterval(loadAllUsers, 2000);

(async () => {
  loadObjectFilter();
  await loadMeta();
  await loadCatalog();
  resetWorldView();
  loadChat();
  loadCommands();
  await loadInstances();
  loadAllUsers();
})();
