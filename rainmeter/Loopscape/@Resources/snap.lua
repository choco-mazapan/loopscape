-- Shared by the Loopscape widgets (via @Resources\Snap.inc).
--
-- Snapping: when a widget is dropped within SnapRadius of a target, it moves into it. Targets are:
--   * the 8 screen spots (corners, top/bottom centre, left/right middle); bottom spots leave room for the taskbar.
--   * stack spots directly above / below each other widget, aligned to its left edge, right edge or centre
--     depending on which side of the screen it sits.
--   * spots beside each other widget, with the top corners or the bottom corners lined up.
-- A target that would make any widget overlap another one, or go off screen, is not offered.
-- Dropped farther from every target, the widget moves to the nearest point of a SnapGridSize grid (SnapGrid=1) or
-- stays where it was dropped (SnapGrid=0).
-- Stacking: a widget dropped on a stack or side spot stays attached to the widget it sits on. If that widget moves or
-- changes size (e.g. weather loads), the attached one follows. Dragging the attached widget away detaches it.
-- Groups: a widget and everything stacked on it (directly or through each other) move as one. While it is dragged
-- or glides into its spot, the dragged widget moves the others itself ("carrying" them), and a target is only
-- offered if the whole group fits there.
--
-- Widgets share their position through variables: each one writes its rect and stack info into Peer_<name> of the
-- others. Rainmeter has no "drag finished" event, so a drop is detected as "position unchanged for DROP_SECS".
-- While dragging, the Loopscape\SnapGuides skin shows dashed outlines of the targets near the widget and a tinted
-- outline where it would land.
--
-- Tick rates (MeasureFastTick in each widget): 1 s when idle, 100 ms while the mouse is over the widget (to notice
-- a drag starting), and 16 ms while it is being dragged or glides into its spot after a drop.
-- The faster rates only run while something is moving, so idle CPU use stays the same.

-- Files shared with Loopscape.exe while it runs (what it plays, commands, colours) live in %LOCALAPPDATA%\Loopscape,
-- outside Documents, which OneDrive may sync. Loopscape.exe creates the folder.
local STATE = (os.getenv('LOCALAPPDATA') or ((os.getenv('USERPROFILE') or '') .. '\\AppData\\Local')) .. '\\Loopscape\\'

local STILL_TICKS = 2      -- ticks a parked widget waits before re-aligning to its spot (PeerChanged, Resnap)
local DROP_SECS = 0.16     -- how long a dragged widget must sit still to count as dropped
local GLIDE_SECS = 0.15    -- how long the widget takes to slide from where it was dropped into its spot
local BLOCKED_SECS = 1.5   -- how long a stacked widget waits with no room on either side before it moves elsewhere
local NEWCOMER_SECS = 10   -- a widget that loaded this recently checks it isn't on top of each widget it hears from
local CARRY_SECS = 150     -- a carried widget gives up waiting for the one carrying it after this long
local GUIDES = 'Loopscape\\SnapGuides'
local MAX_GUIDES = 12      -- Guide1..11 show targets near the widget, Guide12 where it would land
local LANDING = MAX_GUIDES

-- MeasureFastTick action lists (see the widget .ini files)
local TICK_DRAG, TICK_GLIDE = 4, 5

local me, peers = nil, {}
local lastX, lastY, lastW, lastH = nil, nil, nil, nil
local still, dragging, enabled = 0, false, true
local movedAt = 0           -- os.clock() of the last tick on which the user's drag moved the widget
local targets = {}
local attach = nil          -- { peer = 'Loopscape\\Clock', rel = 'above'|'below'|'leftT'|'leftB'|'rightT'|'rightB' }
local place = nil           -- where a free-standing widget is parked: { spot = 'br' } or { hx, ox, vy, oy } (see freePlace)
local recheck = nil         -- checking which widgets are still there (see PeerGone)
local ringTicks = 0         -- ticks we've been part of a ring of stacked widgets
local glide = nil           -- { fx, fy, tx, ty, start, atX, atY, from, to } while sliding into a spot after a drop
local drag = nil            -- { group, obstacles, free } while being dragged
local carry = nil           -- { members, at } while we move the widgets stacked on us ourselves
local carried, carriedAt = false, 0   -- another widget is moving us (we are stacked on it and it is being dragged)
local blockedSince = nil    -- os.clock() since when our stack spot has had no room on either side

local function fastTick(list, on)
  SKIN:Bang('!CommandMeasure', 'MeasureFastTick', (on and 'Execute ' or 'Stop ') .. list)
end

-- ---------- helpers ----------
-- Loading: for the first seconds after a widget loads its panel is smaller than it will be (the clock's weather and
-- forecast, the Wi-Fi details arrive a moment later). Widgets stacked on it would follow it in, then out again, and
-- a widget on a bottom or right spot would itself move twice. So until the content is in we report the size it had
-- last time (SnapSize, saved in the skin's own ini whenever it changes noticeably).
local SETTLE = 5                       -- seconds at most
local savedW, savedH = 0, 0            -- SnapSize as saved
local settleUntil = 0                  -- os.clock() when loading is over at the latest (0: not loading)

-- The widget's visible size is its panel. (The window can be larger: Rainmeter still counts the positions of
-- hidden meters when sizing it, so the window is not a reliable size in the smaller views.)
local function rawSize()
  local p = SKIN:GetMeter('MeterPanel')
  if p and p:GetW() > 1 and p:GetH() > 1 then return p:GetW(), p:GetH() end
  return SKIN:GetW(), SKIN:GetH()
end
local function size()
  local w, h = rawSize()
  if settleUntil > 0 then
    if h >= savedH or os.clock() > settleUntil then settleUntil = 0   -- the content is in (or it's smaller now)
    else return math.max(w, savedW), savedH end
  end
  return w, h
end
local function dbg(msg)
  if SKIN:GetVariable('SnapDebug', '0') ~= '1' then return end
  local f = io.open(STATE .. 'snapdebug.txt', 'a')
  if f then f:write(os.date('%H:%M:%S ') .. (me or '?') .. ' ' .. msg .. '\n'); f:close() end
end
local function num(name, default)
  return tonumber(SKIN:GetVariable(name, tostring(default))) or default
end
-- Widget size (Settings > Widgets > Widget size): #Scale# from Scale.inc, a formula such as (0>0 ? ... : ...). It only
-- changes on a refresh, so it is read once.
local scaleValue
local function scale()
  if not scaleValue then scaleValue = tonumber(SKIN:ParseFormula(SKIN:GetVariable('Scale', '1'))) or 1 end
  return scaleValue
end
-- A distance in Snap.inc (margins, gaps), in pixels at the current widget size
local function px(name, default) return math.floor(num(name, default) * scale() + 0.5) end
local function key(config) return (config:gsub('[^%w]', '_')) end
local function quoted(s) return '"' .. (s:gsub('\\', '\\\\')) .. '"' end

-- ---------- which widgets are running ----------
-- Bangs aimed at a widget that is switched off (or still loading) only fill Rainmeter's log with warnings, so the
-- widgets talk only to peers they know are running (live). A widget that loads says Hello to the widgets
-- Rainmeter.ini lists as active; they add it, and it adds them. One that closes says so (Closing / PeerGone).
local live = {}
local helloPending = true
local closed = false      -- Closing() has run: talk to nobody

-- Configs that Rainmeter.ini lists as switched on (Active=<n>, 0 = off), lower-cased; nil if it can't be read.
-- Rainmeter writes the file in UTF-16: dropping the zero bytes leaves the (ASCII) section names and keys readable.
function activeConfigs()
  local f = io.open(SKIN:GetVariable('SETTINGSPATH', '') .. 'Rainmeter.ini', 'rb')
  if not f then return nil end
  local text = (f:read('*a') or ''):gsub('%z', ''):gsub('^\255\254', '')
  f:close()
  local out, sec = {}, nil
  for line in text:gmatch('[^\r\n]+') do
    local s = line:match('^%[(.-)%]%s*$')
    if s then sec = s:lower()
    elseif sec then
      local n = line:match('^Active%s*=%s*(%d+)')
      if n then out[sec] = n ~= '0' end
    end
  end
  if next(out) == nil then return nil end   -- nothing recognisable: don't guess
  return out
end

local function livePeers()
  local out = {}
  for _, p in ipairs(peers) do if live[p] then out[#out + 1] = p end end
  return out
end

-- Run about a second after loading, not during the load itself: when Rainmeter starts (or refreshes all skins) it
-- loads the widgets one after another, and a widget listed as active may not have its script yet. A peer counts as
-- live once it answers (its Peer_ variable is filled, see noticeAnswers) or says Hello itself.
local HELLO_AFTER = 0.8
local greeting = false    -- sayHello is running: the answers come in one by one, so wait for all of them
local function sayHello()
  helloPending = false
  local act = activeConfigs()
  greeting = true
  for _, p in ipairs(peers) do
    if not act or act[p:lower()] then
      SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Hello(' .. quoted(me) .. ')', p)
    end
  end
  greeting = false
end

-- A peer that shared its position with us is running. (Its PeerChanged call can be dropped while our script is
-- busy, but the variable it sets always arrives.)
local function noticeAnswers()
  if closed then return end
  local new = false
  for _, p in ipairs(peers) do
    if not live[p] and SKIN:GetVariable('Peer_' .. key(p), '') ~= '' then live[p] = true; new = true end
  end
  if new and SKIN:GetW() > 1 then Broadcast() end   -- the newcomer gets our position too
end

-- A widget that just loaded: from now on it gets our position; send it now (it may have loaded after us)
function Hello(from)
  if from and not closed then live[from] = true end
  Broadcast()
end

-- The widgets' meters only update when asked (DefaultUpdateDivider=-1), so there is no later tick to catch up.
-- A change that can move or resize text needs two passes: the panel and the text pills come before the text they
-- wrap, so the first pass gives them the old widths. A change of colour alone needs one.
local function redrawAll(onePass)
  SKIN:Bang('!UpdateMeter', '*')
  if not onePass then
    SKIN:Bang('!UpdateMeter', '*')
    -- the clock's AM/PM text can move on the second pass, after its pill: one more for that pill
    if SKIN:GetMeter('Pill_MeterAmPm') then SKIN:Bang('!UpdateMeter', 'Pill_MeterAmPm') end
  end
  SKIN:Bang('!Redraw')
end

local function screen()
  return num('SCREENAREAX', 0), num('SCREENAREAY', 0), num('SCREENAREAWIDTH', 1920), num('SCREENAREAHEIGHT', 1080)
end

-- Other widgets' rectangles as last broadcast:
--   { name = { x, y, w, h, attachedTo, naturalWidth, rel, loadedMs } }
local function peerRects(includeFollowers)
  local out, all = {}, {}
  for _, p in ipairs(peers) do
    local s = SKIN:GetVariable('Peer_' .. key(p), '')
    -- "x,y,w,h,attachedTo,naturalWidth,rel,loadedMs,alone" (older versions sent only the first six or eight)
    local x, y, w, h, att, nat, rel, ld, al = s:match('^(-?%d+),(-?%d+),(%d+),(%d+),?([^,]*),?(%d*),?(%a*),?(%d*),?(%d*)$')
    local r = x and { tonumber(x), tonumber(y), tonumber(w), tonumber(h), att, tonumber(nat), rel, tonumber(ld), al == '1' }
    -- while checking who's still there, a widget that hasn't answered yet counts as where it was
    if not r and recheck and recheck.old then r = recheck.old[p] end
    if r then all[p] = r end
  end
  -- widgets stacked on us, directly or on one that is, follow us: they are not obstacles or targets (unless
  -- includeFollowers). Stacking on one of them would make a ring that follows itself forever.
  for p, r in pairs(all) do
    local q, n = r[5], 0
    while q and q ~= '' and q ~= me and all[q] and n < 8 do q, n = all[q][5], n + 1 end
    if includeFollowers or q ~= me then out[p] = r end
  end
  return out
end

-- true if following the stack from peer p leads back to us (p sits on us, directly or not); also returns the
-- widgets on the way
local function followsMe(p)
  local rects, n, path = peerRects(true), 0, {}
  while p and p ~= '' and n < 8 do
    if p == me then return true, path end
    path[#path + 1] = p
    p, n = rects[p] and rects[p][5], n + 1
  end
  return false
end

-- Space between stacked widgets. With the panel tint off their padding is invisible, so they overlap a little on
-- purpose (SnapGapClear is negative) to bring the text lines together.
local function gap()
  return (num('PanelAlpha', 0) == 0) and px('SnapGapClear', -16) or px('SnapGap', 12)
end

-- Whether we stand alone in the current view (SnapAloneViews in Snap.inc): we don't stack on other widgets, and they
-- don't stack on us. A peer that stands alone says so in its broadcast (field 9 of its rect, see peerRects).
local function alone()
  local v = SKIN:GetVariable('View', '0')
  for n in (SKIN:GetVariable('SnapAloneViews', '')):gmatch('[^|]+') do
    if n == v then return true end
  end
  return false
end

-- Whether rect a (x, y, w, h) covers rect r. Overlap up to the stacking overlap above (plus a few pixels) doesn't
-- count, so stacked widgets aren't obstacles to each other.
local function hits(ax, ay, aw, ah, r)
  local pad = 4 + math.max(0, -gap())
  return ax < r[1] + r[3] - pad and ax + aw > r[1] + pad and ay < r[2] + r[4] - pad and ay + ah > r[2] + pad
end

-- Which screen column (0 left, 1 centre, 2 right) a rect's centre is in
local function columnOf(x, w)
  local sx, _, sw, _ = screen()
  local cx = x + w / 2 - sx
  return (cx < sw / 3) and 0 or ((cx > sw * 2 / 3) and 2 or 1)
end

-- Where a widget of size w x h goes when attached to peer rect r:
--   above / below: aligned to r's left edge, right edge or centre, depending on r's screen column
--   leftT / leftB / rightT / rightB: beside r, on its left or right, with top (T) or bottom (B) edges lined up
local VERTICAL = { above = true, below = true }
local OPPOSITE = { above = 'below', below = 'above', leftT = 'rightT', rightT = 'leftT', leftB = 'rightB', rightB = 'leftB' }
local RELS = { 'above', 'below', 'leftT', 'leftB', 'rightT', 'rightB' }
local function stackPos(r, rel, w, h)
  local g = gap()
  if VERTICAL[rel] then
    local col, x = columnOf(r[1], r[3])
    if col == 0 then x = r[1]                               -- left column: align left edges
    elseif col == 2 then x = r[1] + r[3] - w                -- right column: align right edges
    else x = r[1] + math.floor((r[3] - w) / 2) end          -- middle: centre on it
    local y = (rel == 'above') and (r[2] - h - g) or (r[2] + r[4] + g)
    return x, y
  end
  local x = (rel:sub(1, 1) == 'l') and (r[1] - w - g) or (r[1] + r[3] + g)
  local y = (rel:sub(-1) == 'T') and r[2] or (r[2] + r[4] - h)
  return x, y
end

-- ---------- groups: a widget and the widgets stacked on it ----------
-- members are listed parents first: { name, dx, dy, w, h, parent, rel } with dx, dy the offset from us.
-- box = { left, top, right, bottom } of the whole group, relative to our top-left corner.
-- at = { name = { x, y } } overrides the broadcast positions (for widgets we are carrying, which don't broadcast).
local function groupOf(x, y, w, h, at)
  local rects = peerRects(true)
  local members, box = {}, { 0, 0, w, h }
  local queue, qi, seen = { me }, 1, { [me] = true }
  while qi <= #queue do
    local parent = queue[qi]; qi = qi + 1
    -- a widget that stands alone carries nothing (anything still stacked on it is about to let go)
    local carries = not ((parent == me) and alone() or (rects[parent] and rects[parent][9]))
    for _, p in ipairs(peers) do   -- in SnapPeers order, so the layout is the same every time
      local r = rects[p]
      if carries and r and not seen[p] and r[5] == parent then
        seen[p] = true; queue[#queue + 1] = p
        local px, py = r[1], r[2]
        if at and at[p] then px, py = at[p][1], at[p][2] end
        local dx, dy = px - x, py - y
        members[#members + 1] = { name = p, dx = dx, dy = dy, w = r[3], h = r[4], parent = parent, rel = r[7] or '' }
        box[1], box[2] = math.min(box[1], dx), math.min(box[2], dy)
        box[3], box[4] = math.max(box[3], dx + r[3]), math.max(box[4], dy + r[4])
      end
    end
  end
  return { members = members, box = box }
end

-- Where every member goes if we are at x, y: each one where its own stacking rule puts it (see the attached branch
-- in Update), so the group lands exactly as it will settle. Returns { name = { x, y, w, h } }, us included.
local function layoutGroup(group, x, y, w, h)
  local pos = { [me] = { x, y, w, h } }
  for _, m in ipairs(group.members) do
    local pr = pos[m.parent]
    if pr and OPPOSITE[m.rel] then
      local mx, my = stackPos(pr, m.rel, m.w, m.h)
      pos[m.name] = { mx, my, m.w, m.h }
    else
      pos[m.name] = { x + m.dx, y + m.dy, m.w, m.h }
    end
  end
  return pos
end

-- The box of the group as layoutGroup places it with us at x, y, relative to our top-left corner. (The members'
-- alignment depends on the screen column, so this can differ a little from group.box, which is where they are now.)
local function layoutBox(group, x, y, w, h)
  local l, t, r, b = 0, 0, w, h
  for _, p in pairs(layoutGroup(group, x, y, w, h)) do
    l, t = math.min(l, p[1] - x), math.min(t, p[2] - y)
    r, b = math.max(r, p[1] + p[3] - x), math.max(b, p[2] + p[4] - y)
  end
  return { l, t, r, b }
end

-- Whether the group fits with us at x, y: every member on screen between top and bottom, and clear of every
-- obstacle. Returns true and the layout, or false and the obstacle in the way (nil when it is the screen edge).
local function groupFits(group, x, y, w, h, obstacles, top, bottom)
  local sx, _, sw, _ = screen()
  local pos = layoutGroup(group, x, y, w, h)
  for _, r in pairs(pos) do
    if r[1] < sx or r[1] + r[3] > sx + sw or r[2] < top or r[2] + r[4] > bottom then return false, nil end
    for p, o in pairs(obstacles) do if hits(r[1], r[2], r[3], r[4], o) then return false, p end end
  end
  return true, pos
end

-- Room kept free for a bar along the top of the screen (Settings: "Room for a top bar", TopBarBuffer=1): the height
-- the bar reserves from the desktop (the work area starts below it), or TopBarHeight for a bar that reserves none.
-- 0 while the setting is off.
local function topBar()
  if num('TopBarBuffer', 0) ~= 1 then return 0 end
  local reserved = num('WORKAREAY', 0) - num('SCREENAREAY', 0)
  if reserved > 0 and reserved < num('SCREENAREAHEIGHT', 1080) / 2 then return reserved end
  return math.max(0, num('TopBarHeight', 28))
end

-- How high and low a widget may go: near the top, and above the bottom spots' margin (which keeps the taskbar clear).
-- The same limits hold for snap targets, free placement and stacked widgets, so a group placed anywhere also fits
-- by its own members' rules.
local function snapLimits()
  local _, sy, _, sh = screen()
  return sy + topBar() + math.floor(px('SnapMargin', 28) / 2), sy + sh - num('SnapBottomMargin', 76)
end

-- Our position when the group's box sits on screen spot name (tl tc tr / ml mr / bl bc br)
local SPOTS = { 'tl', 'tc', 'tr', 'ml', 'mr', 'bl', 'bc', 'br' }
local function spotPos(name, box)
  local sx, sy, sw, sh = screen()
  local m, mb = px('SnapMargin', 28), num('SnapBottomMargin', 76)
  local bw, bh = box[3] - box[1], box[4] - box[2]
  local col, row = name:sub(2, 2), name:sub(1, 1)
  local bx = (col == 'l') and sx + m or ((col == 'r') and sx + sw - bw - m or sx + math.floor((sw - bw) / 2))
  local by = (row == 't') and sy + topBar() + m or ((row == 'b') and sy + sh - bh - mb or sy + math.floor((sh - bh) / 2))
  return bx - box[1], by - box[2]
end

-- All snap targets for our group: { x, y, spot } or { x, y, peer, rel }
local function buildTargets(group, w, h, obstacles)
  obstacles = obstacles or peerRects()
  local top, bottom = snapLimits()
  local list = {}
  local function add(t, x, y)
    if groupFits(group, x, y, w, h, obstacles, top, bottom) then t.x, t.y = x, y; list[#list + 1] = t end
  end
  for _, s in ipairs(SPOTS) do
    local x, y = spotPos(s, group.box)
    x, y = spotPos(s, layoutBox(group, x, y, w, h))
    add({ spot = s }, x, y)
  end
  if not alone() then
    for p, r in pairs(obstacles) do
      if not r[9] then
        for _, rel in ipairs(RELS) do
          local x, y = stackPos(r, rel, w, h)
          add({ peer = p, rel = rel }, x, y)
        end
      end
    end
  end
  return list
end

local function nearestTarget(x, y, list)
  local best, bestD
  for _, t in ipairs(list or targets) do
    local d = (t.x - x) ^ 2 + (t.y - y) ^ 2
    if not bestD or d < bestD then best, bestD = t, d end
  end
  return best, bestD
end

-- Where a drop at x, y lands: the nearest target within SnapRadius; otherwise the nearest grid point (or x, y with
-- the grid off), kept on screen; if the group doesn't fit there, the nearest target after all.
local function chooseTarget(x, y, w, h)
  local best, d = nearestTarget(x, y)
  local radius = num('SnapRadius', 48)
  if best and d <= radius * radius then return best end
  local sx, sy, sw, sh = screen()
  local fx, fy = x, y
  if num('SnapGrid', 1) == 1 then
    -- the grid starts at the screen margin, so it lines up with the corner spots
    local g, m = math.max(4, num('SnapGridSize', 24)), px('SnapMargin', 28)
    fx = sx + m + math.floor((x - sx - m) / g + 0.5) * g
    local gy = sy + topBar() + m
    fy = gy + math.floor((y - gy) / g + 0.5) * g
  end
  local box = layoutBox(drag.group, fx, fy, w, h)
  local top, bottom = snapLimits()
  fx = math.max(sx - box[1], math.min(fx, sx + sw - box[3]))
  fy = math.max(top - box[2], math.min(fy, bottom - box[4]))
  -- (the same free spot as on the last tick: no need to check it again)
  local f = drag.free
  if not (f and f.x == fx and f.y == fy) then
    f = { x = fx, y = fy, free = true }
    f.ok = groupFits(drag.group, fx, fy, w, h, drag.obstacles, top, bottom)
    drag.free = f
  end
  if f.ok then return f end
  return best
end

-- ---------- where a free-standing widget is parked ----------
-- A screen spot is remembered by name. Anywhere else, the group's box is remembered by its distance from the screen
-- edges it is nearest to (hx: L, C or R; vy: T, M or B), so a widget on the right or at the bottom grows away from
-- that edge, like one on a spot. Saved as SnapPlace in the skin's own ini ("spot:br" or "free:R,40,B,120").
local function freePlace(x, y, box)
  local sx, sy, sw, sh = screen()
  local bx, by, bw, bh = x + box[1], y + box[2], box[3] - box[1], box[4] - box[2]
  local p = {}
  local col = columnOf(bx, bw)
  if col == 0 then p.hx, p.ox = 'L', bx - sx
  elseif col == 2 then p.hx, p.ox = 'R', sx + sw - (bx + bw)
  else p.hx, p.ox = 'C', math.floor(bx + bw / 2 - (sx + sw / 2) + 0.5) end
  local cy = by + bh / 2 - sy
  if cy < sh / 3 then p.vy, p.oy = 'T', by - sy
  elseif cy > sh * 2 / 3 then p.vy, p.oy = 'B', sy + sh - (by + bh)
  else p.vy, p.oy = 'M', math.floor(by + bh / 2 - (sy + sh / 2) + 0.5) end
  return p
end

local function placePos(p, box)
  if p.spot then return spotPos(p.spot, box) end
  local sx, sy, sw, sh = screen()
  local bw, bh = box[3] - box[1], box[4] - box[2]
  local bx = (p.hx == 'L') and sx + p.ox or ((p.hx == 'R') and sx + sw - p.ox - bw or math.floor(sx + sw / 2 + p.ox - bw / 2))
  local by = (p.vy == 'T') and sy + p.oy or ((p.vy == 'B') and sy + sh - p.oy - bh or math.floor(sy + sh / 2 + p.oy - bh / 2))
  -- not up under a top bar (the place itself is kept, so turning "Room for a top bar" off puts the widget back)
  if topBar() > 0 then
    local top = snapLimits()
    if by < top then by = top end
  end
  return bx - box[1], by - box[2]
end

local function savePlace()
  local s = ''
  if place and place.spot then s = 'spot:' .. place.spot
  elseif place then s = string.format('free:%s,%d,%s,%d', place.hx, place.ox, place.vy, place.oy) end
  SKIN:Bang('!WriteKeyValue', 'Variables', 'SnapPlace', s)
end

local function loadPlace()
  local s = SKIN:GetVariable('SnapPlace', '')
  local spot = s:match('^spot:(%a%a)$')
  if spot then return { spot = spot } end
  local hx, ox, vy, oy = s:match('^free:([LCR]),(-?%d+),([TMB]),(-?%d+)$')
  if hx then return { hx = hx, ox = tonumber(ox), vy = vy, oy = tonumber(oy) } end
  return nil
end

-- No place saved (snapping was just switched on, or a version before SnapPlace): the spot we're on, or near, or
-- the free place where we are
local function derivePlace(x, y, box)
  local r = num('SnapRadius', 48)
  local best, bestD
  for _, s in ipairs(SPOTS) do
    local px, py = spotPos(s, box)
    local d = (px - x) ^ 2 + (py - y) ^ 2
    if not bestD or d < bestD then best, bestD = s, d end
  end
  if best and bestD <= r * r then return { spot = best } end
  return freePlace(x, y, box)
end

-- ---------- matching widths of stacked widgets ----------
local curMatched, lastNat = 0, nil

-- Width the panel would have from its own content (MeasureNaturalWidth in the skin)
-- fresh = re-evaluate now (after a view change); otherwise use the measure's once-a-second value
function naturalWidth(fresh)
  if fresh then SKIN:Bang('!UpdateMeasure', 'MeasureNaturalWidth') end
  local m = SKIN:GetMeasure('MeasureNaturalWidth')
  local v = m and m:GetValue() or 0
  return v > 0 and math.floor(v + 0.5) or SKIN:GetW()
end

-- When MatchWidths is on, every widget in a column of stacked widgets (linked above/below, directly or through each
-- other) takes the width of the widest one. Widgets beside each other keep their own widths.
local function updateMatch()
  local want = 0
  if num('MatchWidths', 1) == 1 then
    local rects = peerRects(true)
    local nat = naturalWidth()
    local links = {}   -- name -> list of names it is stacked with vertically
    local function link(a, b)
      links[a] = links[a] or {}; links[b] = links[b] or {}
      table.insert(links[a], b); table.insert(links[b], a)
    end
    if attach and VERTICAL[attach.rel] and rects[attach.peer] then link(me, attach.peer) end
    for p, r in pairs(rects) do
      if r[5] and r[5] ~= '' and VERTICAL[r[7]] and (r[5] == me or rects[r[5]]) then link(p, r[5]) end
    end
    local widest, seen, queue, qi = nat, { [me] = true }, { me }, 1
    while qi <= #queue do
      local q = queue[qi]; qi = qi + 1
      for _, o in ipairs(links[q] or {}) do
        if not seen[o] then
          seen[o] = true; queue[#queue + 1] = o
          local n = rects[o] and rects[o][6]
          if n and n > widest then widest = n end
        end
      end
    end
    if #queue > 1 and widest > nat then want = widest end
  end
  if want ~= curMatched then
    curMatched = want
    SKIN:Bang('!SetVariable', 'MatchedWidth', want)
    redrawAll()
  end
end

-- Which screen column the widget sits in (0 left, 1 centre, 2 right), for skins that align content by column
local curAlign = nil
local function updateAlign(x, w)
  local a = columnOf(x, w)
  if a ~= curAlign then
    curAlign = a
    SKIN:Bang('!SetVariable', 'ColumnAlign', a)
    redrawAll()
  end
end

-- ---------- sharing position with the other widgets ----------
local loadedAt = 0
function Broadcast(bx, by)
  if not me then return end
  if writeLinesNow then writeLinesNow() end
  dbg('broadcast ' .. tostring(bx) .. ',' .. tostring(by))
  lastNat = naturalWidth()
  local w, h = size()
  -- remember the size for the next load (the height exactly; the width changes with the clock's digits, so only
  -- a bigger change is worth a write)
  if settleUntil == 0 and (h ~= savedH or math.abs(w - savedW) > 24) then
    savedW, savedH = w, h
    SKIN:Bang('!WriteKeyValue', 'Variables', 'SnapSize', w .. ',' .. h)
  end
  local s = string.format('%d,%d,%d,%d,%s,%d,%s,%d,%d', bx or SKIN:GetX(), by or SKIN:GetY(), w, h,
    attach and attach.peer or '', lastNat, attach and attach.rel or '', math.floor(loadedAt * 1000), alone() and 1 or 0)
  for _, p in ipairs(livePeers()) do
    SKIN:Bang('!SetVariable', 'Peer_' .. key(me), s, p)
    -- let the other widget follow/match now rather than on its next tick
    SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'PeerChanged(' .. quoted(me) .. ')', p)
  end
end

local function saveAttach()
  SKIN:Bang('!WriteKeyValue', 'Variables', 'SnapAttach', attach and (attach.peer .. '|' .. attach.rel) or '')
end

-- ---------- widgets closing ----------
-- A widget that closes (hidden from Settings, unloaded, or refreshing) runs SnapOnClose (Snap.inc), which calls
-- Closing() in its own script, which calls PeerGone() in the running widgets. Its Peer_ variable would otherwise stay
-- behind: widgets stacked on it would keep following a widget that isn't there, and its empty spot would still block
-- snap targets. So on the next tick (when the closing widget is really gone) we clear all Peer_ variables and ask
-- everyone to share their position again; whoever hasn't after GONE_WAIT seconds is gone (a refreshing widget is
-- back well within that). The answers are checked through the variables: a peer's PeerChanged call can arrive while
-- our script is still running, and is then dropped.
local GONE_WAIT = 1.5
local gone = {}       -- last rect of each widget that closed: a widget stacked on it takes its place

function PeerGone(from)
  -- (it says Hello again if it is only refreshing; its variable goes too, or noticeAnswers would take it back)
  if from then live[from] = nil; SKIN:Bang('!SetVariable', 'Peer_' .. key(from), '') end
  if not recheck then recheck = {} end
end

function Closing()
  if not me then return end
  for _, p in ipairs(livePeers()) do SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'PeerGone(' .. quoted(me) .. ')', p) end
  -- (the Settings panel calls this just before switching us off: stay quiet until we're gone)
  live, helloPending, closed = {}, false, true
end

local function checkGone()
  if not recheck then return end
  if not recheck.old then
    recheck.old = peerRects(true)
    recheck.at = os.clock()
    for _, p in ipairs(peers) do SKIN:Bang('!SetVariable', 'Peer_' .. key(p), '') end
    for _, p in ipairs(livePeers()) do SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Broadcast()', p) end
    return
  end
  if os.clock() - recheck.at < GONE_WAIT then return end
  local old = recheck.old
  recheck = nil
  local rects = peerRects(true)
  for p, r in pairs(old) do
    if not rects[p] then dbg(p .. ' closed'); gone[p] = r; live[p] = nil; still = math.max(still, STILL_TICKS) end
  end
end

-- Called by a peer right after it shared a new position/size: react immediately
local inPeerChanged = false
function PeerChanged(from)
  if from then
    gone[from] = nil
    -- a widget we hadn't heard from yet: if we just loaded, check that we aren't on top of it
    if not live[from] and os.clock() - loadedAt < NEWCOMER_SECS then still = math.min(still, STILL_TICKS - 1) end
    if not closed then live[from] = true end
  end
  -- (no chain reaction back into us, and no moves before every widget has answered our Hello: the next tick
  -- catches up)
  if dragging or carried or glide or carry or inPeerChanged or greeting then return end
  inPeerChanged = true
  if still ~= STILL_TICKS - 1 then still = math.max(still, STILL_TICKS) end
  Update()
  inPeerChanged = false
end

-- Called by the Settings panel after a margin changed: re-snap to the nearest spot as if just parked.
function Resnap()
  if dragging or carried or not enabled then return end
  still = STILL_TICKS - 1
  Update()
end

-- Called by a peer that has just attached itself to us: we can't be attached to it at the same time
function DetachFrom(peer)
  if attach and attach.peer == peer then attach = nil; saveAttach() end
end

-- ---------- carrying the widgets stacked on us ----------
-- While we are dragged or glide, we move the widgets stacked on us ourselves, one !Move each per frame. (Letting
-- each of them follow through PeerChanged made every frame a cascade: each moved widget told every other widget,
-- and each of those ran its whole snap logic.) A carried widget only keeps track of where it is (Carry) and settles
-- through its own logic once we let go of it.
function Carry(on)
  if tonumber(on) == 1 then
    carried, carriedAt = true, os.clock()
    if glide then glide = nil; fastTick(TICK_GLIDE, false) end
    return
  end
  if not carried then return end
  carried = false
  lastX, lastY = SKIN:GetX(), SKIN:GetY()
  still = STILL_TICKS - 1   -- the next Update re-aligns and shares our position, for the widgets stacked on us
  Update()
end

local function startCarry(group)
  if #group.members == 0 then carry = nil; return end
  local at = carry and carry.at or {}
  carry = { members = group.members, at = at }
  for _, m in ipairs(group.members) do
    if live[m.name] then SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Carry(1)', m.name) end
    if not at[m.name] then at[m.name] = { nil, nil } end
  end
end

-- pos: { name = { x, y } } for the members
local function carryTo(pos)
  if not carry then return end
  for _, m in ipairs(carry.members) do
    local p, a = pos[m.name], carry.at[m.name]
    if p and live[m.name] and (a[1] ~= p[1] or a[2] ~= p[2]) then
      SKIN:Bang('!Move', p[1], p[2], m.name)
      carry.at[m.name] = { p[1], p[2] }
    end
  end
end

-- the members at the offsets they had from us when the drag started
local function rigid(x, y)
  local pos = {}
  for _, m in ipairs(carry.members) do pos[m.name] = { x + m.dx, y + m.dy } end
  return pos
end

-- Let go of the carried widgets, parents first, so each one has shared where it is before its own followers settle.
-- Call it after our own Broadcast.
local function endCarry()
  if not carry then return end
  local members = carry.members
  carry = nil
  inPeerChanged = true   -- their broadcasts call our PeerChanged: we've just settled, nothing to do
  for _, m in ipairs(members) do
    if live[m.name] then SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Carry(0)', m.name) end
  end
  inPeerChanged = false
end

-- ---------- guides overlay ----------
local function setGuide(name, value) SKIN:Bang('!SetVariable', name, value, GUIDES) end

-- kind: 'snap' (where it lands, on a target), 'free' (where it lands, off the targets) or 'near' (another target)
local function styleGuide(j, kind)
  if kind == 'snap' then
    setGuide('Fill' .. j, '120,175,255,70'); setGuide('Stroke' .. j, '255,255,255,235'); setGuide('Width' .. j, '2')
  elseif kind == 'free' then
    setGuide('Fill' .. j, '255,255,255,28'); setGuide('Stroke' .. j, '255,255,255,200'); setGuide('Width' .. j, '1.5')
  else
    setGuide('Fill' .. j, '0,0,0,0'); setGuide('Stroke' .. j, '255,255,255,110'); setGuide('Width' .. j, '1.5')
  end
end

-- What each guide shows ('x,y,kind' or 'hidden'): only guides that change are sent and redrawn
local guideShown = {}
local guidesAt = 0      -- os.clock() of the last redraw
local function setSlot(j, gx, gy, kind)
  local state = kind and (gx .. ',' .. gy .. ',' .. kind) or 'hidden'
  if guideShown[j] == state then return false end
  guideShown[j] = state
  if kind then
    setGuide('GX' .. j, gx); setGuide('GY' .. j, gy); setGuide('GH' .. j, '0'); styleGuide(j, kind)
  else
    setGuide('GH' .. j, '1')
  end
  SKIN:Bang('!UpdateMeter', 'Guide' .. j, GUIDES)
  return true
end

-- Tooltips are left alone while dragging: Windows already hides a tooltip when the mouse button goes down, and
-- toggling ToolTipHidden at runtime left half-faded tooltips stuck on screen.

-- Show the landing spot and the targets within reach of x, y. A landing spot that only moved along the grid is
-- redrawn at most every 50 ms: the guides are a screen-sized window, and redrawing it on every frame of a fast drag
-- costs more than the drag itself.
local function updateGuides(x, y, landing)
  if not landing then return end
  local now = os.clock()
  local key = (landing.free and 'free' or tostring(landing)) .. ',' .. landing.x .. ',' .. landing.y
  if key == drag.landingKey then return end
  if landing.free and drag.landingFree and now - guidesAt < 0.05 then return end
  drag.landingKey, drag.landingFree = key, landing.free
  local sx, sy = screen()
  local box = drag.group.box
  local reach = (5 * num('SnapRadius', 48)) ^ 2
  local near = {}
  for _, t in ipairs(targets) do
    local d = (t.x - x) ^ 2 + (t.y - y) ^ 2
    if t ~= landing and d <= reach then near[#near + 1] = { t = t, d = d } end
  end
  table.sort(near, function(a, b) return a.d < b.d end)
  local changed = false
  for j = 1, MAX_GUIDES - 1 do
    local n = near[j]
    if n then changed = setSlot(j, n.t.x + box[1] - sx, n.t.y + box[2] - sy, 'near') or changed
    else changed = setSlot(j) or changed end
  end
  changed = setSlot(LANDING, landing.x + box[1] - sx, landing.y + box[2] - sy, landing.free and 'free' or 'snap') or changed
  if changed then SKIN:Bang('!Redraw', GUIDES); guidesAt = now end
end

local function showGuides()
  local box = drag.group.box
  setGuide('W', box[3] - box[1]); setGuide('H', box[4] - box[2])
  guideShown = {}
  for j = 1, MAX_GUIDES do setGuide('GH' .. j, '1'); guideShown[j] = 'hidden' end
  SKIN:Bang('!UpdateMeter', '*', GUIDES)
  SKIN:Bang('!ShowFade', GUIDES)
  dragging = true
  fastTick(TICK_DRAG, true)
end

local function hideGuides()
  if dragging then SKIN:Bang('!HideFade', GUIDES); fastTick(TICK_DRAG, false) end
  dragging = false
  drag = nil
end

-- Where we go when the widget we're stacked on (last at rect g) has closed: into its place, keeping the edge
-- that faced it (stacked below it: our top where its top was; above it: our bottom where its bottom was;
-- beside it: our outer edge where its outer edge was)
local function placeOf(g, rel, w, h)
  if not VERTICAL[rel] then
    local x = (rel:sub(1, 1) == 'l') and (g[1] + g[3] - w) or g[1]
    local y = (rel:sub(-1) == 'T') and g[2] or (g[2] + g[4] - h)
    return x, y
  end
  local col, x = columnOf(g[1], g[3])
  if col == 0 then x = g[1]
  elseif col == 2 then x = g[1] + g[3] - w
  else x = g[1] + math.floor((g[3] - w) / 2) end
  return x, (rel == 'below') and g[2] or (g[2] + g[4] - h)
end

-- First shown widget (in SnapPeers order) that nothing is stacked on and that isn't stacked on us
function stackFallback()
  local rects = peerRects(true)
  for _, p in ipairs(peers) do
    local r = rects[p]
    if r and not r[9] and (attach == nil or p ~= attach.peer) and not followsMe(p) then
      local taken = false
      for q, r2 in pairs(rects) do if q ~= p and r2[5] == p then taken = true end end
      if not taken then return p end
    end
  end
  return nil
end

-- Take target t: attach to its widget, or park on its spot / free place
local function takeTarget(t, box)
  if t.peer then
    attach = { peer = t.peer, rel = t.rel }
    if live[t.peer] then SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'DetachFrom(' .. quoted(me) .. ')', t.peer) end
  else
    attach = nil
    place = t.spot and { spot = t.spot } or freePlace(t.x, t.y, box)
    savePlace()
  end
  saveAttach()
end

-- We overlap another widget (we just loaded on top of it, or the spot we stack on has no room): move to the nearest
-- target that is free. Returns its position, or nil if there is none.
local function relocate(x, y, w, h)
  local group = groupOf(x, y, w, h)
  local t = nearestTarget(x, y, buildTargets(group, w, h))
  if not t then return nil end
  dbg('overlapping: moving to ' .. t.x .. ',' .. t.y .. (t.peer and (' ' .. t.rel .. ' ' .. t.peer) or (' spot ' .. t.spot)))
  takeTarget(t, group.box)
  return t.x, t.y
end

-- Called by a widget that overlaps us and has nowhere else to go: we move to the nearest free target instead
function MakeRoom()
  if dragging or carried or glide or carry or not enabled then return end
  local x, y = SKIN:GetX(), SKIN:GetY()
  local w, h = size()
  local tx, ty = relocate(x, y, w, h)
  if not tx or (tx == x and ty == y) then return end
  SKIN:Bang('!Move', tx, ty)
  lastX, lastY, still = tx, ty, STILL_TICKS
  updateMatch(); updateAlign(tx, w)
  Broadcast(tx, ty)
end

-- Overlapping peer p: whichever of us loaded later moves (ties: the one later in SnapPeers), so only one does
local function iShouldMove(p, rects)
  local r = rects[p]
  local theirs, mine = r and r[8], math.floor(loadedAt * 1000)
  if theirs and theirs ~= mine then return theirs < mine end
  for q in (SKIN:GetVariable('SnapPeers', '')):gmatch('[^|]+') do
    if q == me then return false end
    if q == p then return true end
  end
  return true
end

-- ---------- glide into the spot after a drop ----------
local function easeOut(t) return 1 - (1 - t) ^ 3 end

-- The widgets we carry glide with us, each from where it is to where it settles (to = layoutGroup)
local function startGlide(fx, fy, tx, ty, to)
  local from = {}
  if carry then for _, m in ipairs(carry.members) do from[m.name] = carry.at[m.name] end end
  local far = math.abs(tx - fx) + math.abs(ty - fy) >= 4
  for name, p in pairs(from) do
    if to[name] and p[1] and math.abs(to[name][1] - p[1]) + math.abs(to[name][2] - p[2]) >= 4 then far = true end
  end
  if not far then return false end   -- too close to see: just move
  glide = { fx = fx, fy = fy, tx = tx, ty = ty, start = os.clock(), atX = fx, atY = fy, from = from, to = to }
  fastTick(TICK_GLIDE, true)
  return true
end

local function stopGlide()
  glide = nil
  fastTick(TICK_GLIDE, false)
end

-- One frame of the glide. Returns true when it has reached the spot.
local function stepGlide()
  local g = glide
  local p = (os.clock() - g.start) / GLIDE_SECS
  local e = (p >= 1) and 1 or easeOut(math.max(0, p))
  local function at(a, b) return math.floor(a + (b - a) * e + 0.5) end
  local nx, ny = at(g.fx, g.tx), at(g.fy, g.ty)
  if nx ~= g.atX or ny ~= g.atY then
    SKIN:Bang('!Move', nx, ny)
    g.atX, g.atY = nx, ny
    lastX, lastY = nx, ny
  end
  if carry then
    local pos = {}
    for name, f in pairs(g.from) do
      local t = g.to[name]
      if t and f[1] then pos[name] = { at(f[1], t[1]), at(f[2], t[2]) } elseif t then pos[name] = { t[1], t[2] } end
    end
    carryTo(pos)
  end
  return p >= 1
end

-- Done moving after a drop: match, align, share where we are, and let go of the widgets we carried
local function settle(x, y, w)
  lastX, lastY, still = x, y, STILL_TICKS
  updateMatch(); updateAlign(x, w)
  Broadcast(x, y)
  endCarry()
end

-- ---------- main ----------
function Initialize()
  loadedAt = os.clock()
  local sw, sh = (SKIN:GetVariable('SnapSize', '')):match('^(%d+),(%d+)$')
  if sw then savedW, savedH, settleUntil = tonumber(sw), tonumber(sh), loadedAt + SETTLE end
  me = SKIN:GetVariable('CURRENTCONFIG')
  for p in (SKIN:GetVariable('SnapPeers', '')):gmatch('[^|]+') do
    if p ~= me then peers[#peers + 1] = p end
  end
  enabled = num('SnapEnabled', 1) == 1
  local a = SKIN:GetVariable('SnapAttach', '')
  local ap, ar = a:match('^(.-)|(%a+)$')
  if ap and ap ~= '' and OPPOSITE[ar] then attach = { peer = ap, rel = ar } end
  place = loadPlace()
  -- the guides overlay: switch it on unless Rainmeter already has it (on, or about to load it at startup)
  local act = activeConfigs()
  if not (act and act[GUIDES:lower()]) then SKIN:Bang('!ActivateConfig', GUIDES, 'SnapGuides.ini') end
  -- the saved view is applied on the first Update: meters aren't ready yet during Initialize
  viewPending = (tonumber(SKIN:GetVariable('View', '0')) or 0) > 0
  -- say Hello to the others (and get their positions) once loading is over: see sayHello
  helloPending = true
end

-- Dropped: move the group to where it lands, and remember a stack relationship or the place
local function dropped(x, y, w, h)
  local tx, ty
  local group = drag.group
  group.box[3], group.box[4] = math.max(group.box[3], w), math.max(group.box[4], h)
  targets = buildTargets(group, w, h, drag.obstacles)
  local t = chooseTarget(x, y, w, h)
  hideGuides()
  if t then
    tx, ty = t.x, t.y
    takeTarget(t, group.box)
    dbg('dropped -> ' .. tx .. ',' .. ty .. (t.peer and (' stacked ' .. t.rel .. ' ' .. t.peer) or (t.spot and (' spot ' .. t.spot) or ' free')))
  else
    -- no target and no room where it was dropped (everything is taken): it stays there
    tx, ty = x, y
    attach, place = nil, freePlace(x, y, group.box)
    saveAttach(); savePlace()
  end
  -- Slide into the spot instead of jumping there; the glide's last frame does the usual matching and sharing
  if startGlide(x, y, tx, ty, layoutGroup(group, tx, ty, w, h)) then return end
  if tx ~= x or ty ~= y then SKIN:Bang('!Move', tx, ty) end
  if carry then carryTo(layoutGroup(group, tx, ty, w, h)) end
  settle(tx, ty, w)
end

-- Stacked: where we go to follow the widget we sit on (nil: stay)
local function followTarget(x, y, w, h)
  local tx, ty
  dbg('attached to ' .. attach.peer .. ' ' .. attach.rel .. ' peerVar=' .. SKIN:GetVariable('Peer_' .. key(attach.peer), '(none)'))
  local rects = peerRects(true)
  -- a ring (saved by an older version, or both dropped at once): the widget we sit on follows us, so we stand
  -- free. Only once it has lasted a while: right after a widget loads, the others' stack info can be out of date.
  -- The widget first in SnapPeers order lets go, and snaps to a free spot.
  local ring, path = followsMe(attach.peer)
  if ring then ringTicks = ringTicks + 1 else ringTicks = 0 end
  if ringTicks > 20 then
    ringTicks = 0
    local first
    for p in (SKIN:GetVariable('SnapPeers', '')):gmatch('[^|]+') do
      if p == me then first = true; break end
      for _, q in ipairs(path) do if q == p then first = false end end
      if first == false then break end
    end
    if first then
      dbg('ring via ' .. attach.peer .. ': detached'); attach = nil; saveAttach(); rects = {}
      still = STILL_TICKS - 1
    end
  end
  local r, own = attach and rects[attach.peer], true
  -- The widget we sit on is hidden. attach (saved) stays as it is, so we go back once it's shown again.
  local g = attach and not r and gone[attach.peer]
  if g then
    -- it closed while we followed it: sit on what it sat on, or take its place
    if g[5] and g[5] ~= '' and g[5] ~= me and rects[g[5]] then r, own = rects[g[5]], false
    else tx, ty = placeOf(g, attach.rel, w, h) end
  elseif attach and not r and os.clock() - loadedAt > 3 then
    -- not shown since we loaded: stack on another widget rather than stay where the skin was never placed
    r, own = rects[stackFallback()], false
  end
  if r then
    -- No room on our side (e.g. it was dragged to the screen edge, or the widget it sits on flipped over to the
    -- other side of its own one, which is now in our way): switch to the other side
    local sx, _, sw, _ = screen()
    local top, bottom = snapLimits()
    local function fits(rel)
      local fx, fy = stackPos(r, rel, w, h)
      if fy < top or fy + h > bottom or fx < sx or fx + w > sx + sw then return false, fx, fy end
      for p2, r2 in pairs(rects) do
        if r2 ~= r and not followsMe(p2) and hits(fx, fy, w, h, r2) then return false, fx, fy, p2 end
      end
      return true, fx, fy
    end
    local ok, blocker
    ok, tx, ty, blocker = fits(attach.rel)
    if not ok then
      local ok2, ox, oy = fits(OPPOSITE[attach.rel])
      if ok2 then
        ok, tx, ty = true, ox, oy
        if own then attach.rel = OPPOSITE[attach.rel]; saveAttach() end   -- not for a stand-in: that would change the real choice
      end
    end
    -- No room on either side: we'd sit on top of another widget, so we stay where we are. (Moving there would push
    -- that widget out of its own spot instead.) If that lasts, we move to the nearest free target, unless the widget
    -- in our way loaded after us: then it is the one that moves.
    if ok then blockedSince = nil
    else
      tx, ty = nil, nil
      if not blockedSince then blockedSince = os.clock()
      elseif os.clock() - blockedSince > BLOCKED_SECS and (not blocker or iShouldMove(blocker, rects)) then
        blockedSince = nil
        tx, ty = relocate(x, y, w, h)
      end
    end
  end
  return tx, ty
end

-- Parked: keep our place (e.g. on a right or bottom spot, grow away from the edge)
local placeMembers = nil   -- the widgets stacked on us when place was last set or checked ('a|b|c')
local function parkedTarget(x, y, w, h)
  local group = groupOf(x, y, w, h)
  local names = {}
  for _, m in ipairs(group.members) do names[#names + 1] = m.name end
  local members = table.concat(names, '|')
  -- A free place describes where the group's box is. When widgets join or leave the group, its box changes, so
  -- the place is taken again from where we are (otherwise we would jump to keep the new box at the old distance).
  if place and not place.spot and placeMembers and members ~= placeMembers then place = nil end
  placeMembers = members
  if not place then place = derivePlace(x, y, group.box); savePlace() end
  local tx, ty = placePos(place, group.box)
  -- Another widget is in the way there (we just loaded on top of it, e.g. shown from Settings, or one of us grew).
  -- Whichever of us loaded later moves to the nearest free target; if that's the other one, we stay and tell it.
  local top, bottom = snapLimits()
  local rects = peerRects()
  local ok, p = groupFits(group, tx, ty, w, h, rects, top, bottom)
  if not ok and p then
    tx, ty = nil, nil
    if iShouldMove(p, rects) then
      tx, ty = relocate(x, y, w, h)
      -- no free target for us (e.g. a tall stack): the other widget makes room instead
      if not tx and live[p] then Broadcast(x, y); SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'MakeRoom()', p) end
    elseif live[p] then
      Broadcast(x, y)   -- (so it sees our new size)
      SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Resnap()', p)
    end
  end
  return tx, ty
end

function Update()
  if helloPending and os.clock() - loadedAt >= HELLO_AFTER then sayHello() end
  if viewPending then viewPending = false; applyView() end
  checkGone()
  noticeAnswers()
  checkTheme()
  checkPendingMove()
  checkPlacement()
  stepWallFade()
  local x, y = SKIN:GetX(), SKIN:GetY()
  local w, h = size()
  if w <= 1 or h <= 1 then return 0 end
  -- can be changed live from the Settings panel; once it's back on, the place is worked out again from where we are
  local nowEnabled = num('SnapEnabled', 1) == 1
  if nowEnabled and not enabled then place = nil end
  enabled = nowEnabled
  if not enabled then
    if glide then stopGlide() end
    if carry then endCarry() end
    if x ~= lastX or y ~= lastY or w ~= lastW or h ~= lastH then lastX, lastY, lastW, lastH = x, y, w, h; Broadcast() end
    return 0
  end

  -- Another widget is carrying us (we are stacked on it and it is being dragged): just keep track of where we are
  if carried then
    if os.clock() - carriedAt < CARRY_SECS then lastX, lastY = x, y; return 0 end
    carried = false
  end

  if glide then
    if x == glide.atX and y == glide.atY then
      if not stepGlide() then return 1 end
      -- Arrived. lastW/lastH still hold the size at the drop, so a size change during the glide re-aligns next tick.
      local tx, ty = glide.tx, glide.ty
      stopGlide()
      settle(tx, ty, w)
      return 1
    end
    -- Grabbed again mid-glide, or moved by something else (a wallpaper change): handled below as usual
    stopGlide()
  end

  if x ~= lastX or y ~= lastY then
    if lastX ~= nil then
      -- Being dragged by the user: show the targets near it and where it would land, and carry its stack along
      if not dragging then
        attach = nil
        -- (the offsets are from where we were before this first step of the drag: the others haven't moved yet)
        drag = { group = groupOf(lastX, lastY, w, h, carry and carry.at), obstacles = peerRects() }
        targets = buildTargets(drag.group, w, h, drag.obstacles)
        startCarry(drag.group)
        showGuides()
      end
      updateGuides(x, y, chooseTarget(x, y, w, h))
      if carry then carryTo(rigid(x, y)) end
    end
    lastX, lastY, still = x, y, 0
    lastW, lastH = w, h
    movedAt = os.clock()
    return 0
  end

  still = still + 1
  if dragging then
    if os.clock() - movedAt < DROP_SECS and w == lastW and h == lastH then return 0 end
    still = STILL_TICKS
  elseif still < STILL_TICKS and w == lastW and h == lastH then
    return 0
  end
  local sizeChanged = (w ~= lastW or h ~= lastH)
  lastW, lastH = w, h

  -- We switched to a view that stands alone, or the widget we sit on did: let go and park where we are. (The widget
  -- we sat on hears of it from our next broadcast.)
  if attach and not dragging then
    local r = peerRects(true)[attach.peer]
    if alone() or (r and r[9]) then
      dbg('detached from ' .. attach.peer .. ': one of us stands alone in this view')
      attach, place = nil, nil
      saveAttach()
      still = STILL_TICKS
    end
  end

  local tx, ty
  if dragging then
    dropped(x, y, w, h)
    return 1
  elseif attach then
    tx, ty = followTarget(x, y, w, h)
  elseif sizeChanged or still == STILL_TICKS then
    tx, ty = parkedTarget(x, y, w, h)
  end

  local moved = false
  if tx and (tx ~= x or ty ~= y) then
    SKIN:Bang('!Move', tx, ty)
    lastX, lastY, still = tx, ty, STILL_TICKS
    x, y = tx, ty
    moved = true
  end
  if not dragging then updateMatch(); updateAlign(x, w) end
  if moved or sizeChanged or still == STILL_TICKS or naturalWidth() ~= lastNat then Broadcast(x, y) end
  return 1
end


-- ---------- move with the wallpaper (Settings: "Move widgets with wallpaper", AutoPlace=1) ----------
-- Loopscape writes the style on screen to %LOCALAPPDATA%\Loopscape\wallstyle.txt. When it changes, each free-standing widget
-- (one not stacked on another) moves to that style's recommended screen spot, away from its busy/bright parts;
-- stacked widgets follow the one they sit on. The first free widget (in SnapPeers order) gets the first spot.
-- Spots: tl tc tr / ml mr / bl bc br
local STYLE_SPOTS = {
  spectrum = { 'tr', 'bl' }, halftone = { 'br', 'tr' }, bluesky = { 'tl', 'tr' }, pixelclouds = { 'tl', 'tr' },
  sunset = { 'tl', 'tr' }, riso = { 'bl', 'br' }, dunes = { 'tl', 'tc' }, lake = { 'tl', 'tr' },
  pastel = { 'bl', 'br' }, crt = { 'bl', 'br' }, shore = { 'tl', 'bl' },
  nightclouds = { 'tl', 'tr' }, aurora = { 'bl', 'br' }, nebula = { 'tl', 'tr' }, moon = { 'tl', 'bl' },
  embers = { 'tl', 'tr' }, deepsea = { 'bl', 'bc' }, rain = { 'tl', 'tr' },
}
-- A third (or later) free widget takes the first of these that the style's own list doesn't use
local SPARE_SPOTS = { 'tc', 'bc', 'tl', 'tr', 'bl', 'br', 'ml', 'mr' }
local function spotFor(spots, rank)
  if rank + 1 <= #spots then return spots[rank + 1] end
  local n = rank + 1 - #spots
  for _, s in ipairs(SPARE_SPOTS) do
    local used = false
    for _, u in ipairs(spots) do if u == s then used = true end end
    if not used then n = n - 1; if n == 0 then return s end end
  end
  return spots[#spots]
end
local placedStyle, lastPlaceCheck = nil, -10

-- Instant wallpaper switch: get the text colours for the new spot first, then move. We write where we (and any
-- widget stacked on us) are going to %LOCALAPPDATA%\Loopscape\moveto.txt; Loopscape samples the new wallpaper there and pushes
-- ThemeNow() with entries tagged at=<x>,<y>. checkTheme applies such an entry, then calls finishMove.
-- If no answer comes (adaptive text off, Loopscape busy) we move anyway after MOVE_WAIT seconds.
local pendingMove = nil   -- { x, y, deadline }
local MOVE_WAIT = 0.6
local function moveFile() return STATE .. 'moveto.txt' end

-- Tell Loopscape where we (and widgets stacked on us) are going; false if adaptive text is off or the file failed
function writeMoveFile(tx, ty)
  if SKIN:GetVariable('TextColorMode', 'off') == 'off' then return false end
  local x, y = SKIN:GetX(), SKIN:GetY()
  -- our coordinates are DPI-scaled, Loopscape's are physical pixels: the screen size lets it convert
  local sx, sy, sw, sh = screen()
  local out = { string.format('screen %d %d %d %d', sx, sy, sw, sh), string.format('%s %d %d', me, tx, ty) }
  for _, p in ipairs(peers) do
    local r = peerRects(true)[p]
    if r and r[5] == me then out[#out + 1] = string.format('%s %d %d', p, r[1] + tx - x, r[2] + ty - y) end
  end
  local f = io.open(moveFile(), 'w')
  if not f then return false end
  f:write(table.concat(out, '\n') .. '\n'); f:close()
  return true
end

function beginMove(tx, ty, spot)
  if not writeMoveFile(tx, ty) then moveForWallpaper(tx, ty, spot); return end
  pendingMove = { x = tx, y = ty, spot = spot, deadline = os.clock() + MOVE_WAIT }
  SKIN:Bang('!CommandMeasure', 'MeasureFastTick', 'Execute 2')   -- 100 ms ticks so the fallback isn't a full second late
end

function finishMove()
  if not pendingMove then return end
  local tx, ty, spot = pendingMove.x, pendingMove.y, pendingMove.spot
  pendingMove = nil
  moveForWallpaper(tx, ty, spot)
  os.remove(moveFile())
end

-- an entry Loopscape sampled at our destination: apply it, then move
local function movingTo(set)
  local at = pendingMove and set.W and set.W.at
  return at and at[1] == pendingMove.x and at[2] == pendingMove.y
end

function checkPendingMove()   -- global: Update calls it (the fallback)
  if pendingMove and os.clock() >= pendingMove.deadline then dbg('move: no colours for the new spot in time'); finishMove() end
end

function checkPlacement()   -- global: Update (above) calls it
  if num('AutoPlace', 0) ~= 1 then placedStyle = nil; return end
  if dragging or carried then return end
  local now = os.clock()
  if now - lastPlaceCheck < 1 then return end
  lastPlaceCheck = now
  local f = io.open(STATE .. 'wallstyle.txt', 'r')
  if not f then return end
  local style = (f:read('*l') or ''):match('^%s*(.-)%s*$')
  local fadeMs = tonumber(((f:read('*l') or ''):match('^fade=(%d+)'))) or 0
  f:close()
  if style == '' or style == placedStyle then return end
  if placedStyle == nil then fadeMs = 0 end   -- first read (skin load/refresh): the fade line is old news
  placedStyle = style
  local spots = STYLE_SPOTS[style]
  if not spots or attach then return end   -- unknown style, or we follow another widget
  -- rank among the free-standing widgets
  local rank, rects = 0, peerRects(true)
  for p in (SKIN:GetVariable('SnapPeers', '')):gmatch('[^|]+') do
    if p == me then break end
    local r = rects[p]
    if r and (r[5] == nil or r[5] == '') then rank = rank + 1 end
  end
  local w, h = size()
  local spot = spotFor(spots, rank)
  -- the whole group goes to the spot, so a tall stack still fits on a middle or bottom spot
  local x, y = SKIN:GetX(), SKIN:GetY()
  local tx, ty = spotPos(spot, groupOf(x, y, w, h).box)
  dbg('wallpaper ' .. style .. ' -> ' .. spot .. ' ' .. tx .. ',' .. ty .. ' fade ' .. fadeMs)
  if tx == x and ty == y then place = { spot = spot }; savePlace(); return end
  -- The wallpaper is crossfading: Loopscape wrote the file as the crossfade started, and its PlaceNow() call takes
  -- about 0.2 s to reach us. Fade out with the old wallpaper, move at the crossfade's midpoint, fade in over the
  -- new one. Widgets stacked on us join in.
  local dur = fadeMs / 1000 - 0.2
  if dur >= 0.4 then
    startWallFade(os.clock(), dur, tx, ty, spot)
  else
    beginMove(tx, ty, spot)
  end
end

-- ---------- fade with the wallpaper crossfade ----------
local wallFade = nil   -- { start, dur, tx, ty, spot, moved } (tx = nil: stacked, we follow the widget below/above us)
local OUT_END, IN_START = 0.42, 0.58   -- fractions of the fade: fading out, invisible (move at 0.5), fading in

-- spot: the screen spot we move to, which becomes our place
function moveForWallpaper(tx, ty, spot)
  if spot and not attach then place = { spot = spot }; savePlace() end
  if tx == SKIN:GetX() and ty == SKIN:GetY() then return end
  SKIN:Bang('!Move', tx, ty)
  lastX, lastY, still = tx, ty, STILL_TICKS
  Broadcast(tx, ty)
end

local function setAlpha(a) SKIN:Bang('!SetTransparency', math.max(0, math.min(255, math.floor(a + 0.5)))) end
local function ease(t) return t * t * (3 - 2 * t) end

-- Colours: Loopscape reads our destination from the incoming style (moveto.txt) and sends entries tagged at=;
-- checkTheme keeps them in wallFade.colours while we're still visible at the old spot, and they're applied while
-- we're invisible, just before the move, so we fade in already in the right colours.
function startWallFade(start, dur, tx, ty, spot)
  wallFade = { start = start, dur = dur, tx = tx, ty = ty, spot = spot, moved = false }
  if tx then wallFade.notify = writeMoveFile(tx, ty) end
  -- widgets stacked on us move when we do, so they fade on the same clock (one Rainmeter process: os.clock is shared)
  for _, p in ipairs(livePeers()) do
    local r = peerRects(true)[p]
    if r and r[5] == me then
      SKIN:Bang('!CommandMeasure', 'MeasureSnap', string.format('FollowFade(%.3f, %.3f)', start, dur), p)
    end
  end
  SKIN:Bang('!CommandMeasure', 'MeasureFastTick', 'Execute 3')   -- 50 ms ticks while fading
  stepWallFade()
end

function FollowFade(start, dur)
  if wallFade or dragging or carried then return end
  startWallFade(start, dur, nil, nil)
end

function stepWallFade()   -- global: Update (above) calls it
  local f = wallFade
  if not f then return end
  local p = (os.clock() - f.start) / f.dur
  if dragging then p = 1; f.moved = true end   -- the user grabbed it: just show it again where it is
  if p >= 0.5 and not f.moved then
    f.moved = true
    if f.colours then applyStashed(f.colours); dbg('wallfade: colours for the new spot applied, moving')
    elseif f.notify then dbg('wallfade: no colours for the new spot yet') end
    if f.tx then moveForWallpaper(f.tx, f.ty, f.spot) end
  end
  if p >= 1 then
    if f.notify then os.remove(moveFile()) end
    wallFade = nil
    setAlpha(255)
    SKIN:Bang('!CommandMeasure', 'MeasureFastTick', 'Stop 3')
    return
  end
  local a = 0
  if p < OUT_END then a = 1 - ease(math.max(0, p) / OUT_END)
  elseif p > IN_START then a = ease((p - IN_START) / (1 - IN_START)) end
  setAlpha(255 * a)
  dbg(string.format('wallfade %.2f alpha %d', p, math.floor(255 * a + 0.5)))
end

-- ---------- adaptive text colour ----------
-- Loopscape samples the wallpaper around each widget and writes %LOCALAPPDATA%\Loopscape\bgtheme.txt (see wallpaper\Loopscape\WidgetThemes.cs):
--   mode=<off|contrast|smooth|tinted|smoothtinted|perline>
--   W|<config>|text=R,G,B|pill=R,G,B,A|shadow=R,G,B,A          whole widget
--   L|<config>|<meter>|text=...|pill=...|shadow=...             per line (perline mode)
-- Each text meter gets the text colour with its role's opacity; icons are tinted; pills use PillFill_<meter>.
-- In the smooth modes and per-line mode, colour changes fade over FADE seconds.
local ROLES = {
  ['Loopscape\\Clock'] = {
    MeterTime = 'main', MeterAmPm = 'main', MeterWeatherTemp = 'main', MeterWeatherDesc = 'sub', MeterWeatherCity = 'city',
    MeterDate = 'sub', MeterBattery = 'battery', MeterWeatherIcon = 'icon',
    MeterFcDay1 = 'sub', MeterFcDay2 = 'sub', MeterFcDay3 = 'sub', MeterFcTemp1 = 'main', MeterFcTemp2 = 'main', MeterFcTemp3 = 'main',
    MeterFcIcon1 = 'icon', MeterFcIcon2 = 'icon', MeterFcIcon3 = 'icon' },
  ['Loopscape\\WiFi'] = {
    MeterSSID = 'main', MeterQuality = 'sub', MeterIcon = 'icon', MeterTraffic = 'main', MeterTrafficUp = 'main',
    MeterL1 = 'label', MeterL2 = 'label', MeterL3 = 'label', MeterL4 = 'label', MeterL5 = 'label', MeterL6 = 'label', MeterL7 = 'label',
    MeterV1 = 'main', MeterV2 = 'main', MeterV3 = 'main', MeterV4 = 'main', MeterV5 = 'main', MeterV6 = 'main', MeterV7 = 'main' },
  ['Loopscape\\NowShowing'] = {
    MeterHead = 'label', MeterName = 'main', MeterInfo = 'sub',
    MeterBtn1 = 'main', MeterBtn2 = 'main', MeterBtn3 = 'main', MeterBtn4 = 'main' },
  ['Loopscape\\SunMoon'] = { MeterHead = 'label', MeterName = 'main', MeterInfo = 'sub', MeterMoon = 'sub' },
  ['Loopscape\\Rain'] = {
    MeterHead = 'label', MeterName = 'main', MeterInfo = 'sub', MeterT0 = 'label', MeterT1 = 'label', MeterT2 = 'label', MeterT3 = 'label' },
  ['Loopscape\\System'] = {
    MeterHead = 'label', MeterL1 = 'label', MeterL2 = 'label', MeterL3 = 'label', MeterV1 = 'main', MeterV2 = 'main', MeterV3 = 'main',
    MeterLine = 'main' },
  ['Loopscape\\Calendar'] = { MeterHead = 'main', MeterName = 'main', MeterInfo = 'sub' },
}
-- the calendar's weekday letters and 42 day cells
for i = 1, 7 do ROLES['Loopscape\\Calendar']['MeterH' .. i] = 'label' end
for i = 1, 42 do ROLES['Loopscape\\Calendar']['MeterD' .. i] = 'sub' end
-- weather icons are colour-coded (see weather.lua / make-weather-icons.ps1) instead of tinted with the text
WX_ICONS = { MeterWeatherIcon = true, MeterFcIcon1 = true, MeterFcIcon2 = true, MeterFcIcon3 = true }
local ALPHA = { main = 255, sub = 225, label = 155, city = 205, battery = 225, icon = 255 }
local DEFAULT = { text = { 255, 255, 255 }, pill = { 0, 0, 0, 160 }, shadow = { 0, 0, 0, 170 } }
local FADE = 1.2
local FADE_MODES = { smooth = true, smoothtinted = true, perline = true }

local themeMode, shown, target, fadeFrom, fadeStart = 'off', nil, nil, nil, nil
local lastThemeRead, lastLinesText, lastFx = 0, nil, nil

local function nums(s) local t = {}; for n in (s or ''):gmatch('%d+') do t[#t + 1] = tonumber(n) end; return t end
local function parseEntry(rest)
  local e = {}
  for k, v in rest:gmatch('(%a+)=([%d,]+)') do e[k] = nums(v) end
  if not (e.text and e.pill and e.shadow) then return nil end
  return e
end
local function copy(e) return { text = { unpack(e.text) }, pill = { unpack(e.pill) }, shadow = { unpack(e.shadow) } } end
local function lerp(a, b, t) local o = {}; for i = 1, #b do o[i] = math.floor((a[i] or b[i]) + ((b[i] - (a[i] or b[i])) * t) + 0.5) end; return o end
local function col(t, alpha) return t[1] .. ',' .. t[2] .. ',' .. t[3] .. (alpha and (',' .. alpha) or (t[4] and (',' .. t[4]) or '')) end

-- Text boost (Settings.inc TextBoost, 0-6): readability without any backdrop, mild to extreme.
--   effect = StringEffect (nil: the TextEffect setting), halo = { x, y, blur, alpha } blurred shadow in the shadow colour,
--   bold = font weight added, solid = pure white/black text at full opacity (no tint, no dimmed secondary lines)
local BOOST = {
  [0] = {},
  -- (Direct2D's shadow blur barely spreads past the glyphs, so halos use small blurs at full strength)
  [1] = { effect = 'None',   halo = { 1, 1, 1.5, 190 } },                                -- soft drop shadow
  [2] = { effect = 'None',   halo = { 2, 2, 1, 255 } },                                  -- crisp drop shadow
  [3] = { effect = 'Shadow', halo = { 0, 0, 1.5, 255 } },                                -- halo
  [4] = { effect = 'Border', halo = { 1, 1, 2, 255 } },                                  -- outline + halo
  [5] = { effect = 'Border', halo = { 1, 1, 2, 255 }, bold = 200 },                      -- bold, outline + halo
  [6] = { effect = 'Border', halo = { 2, 2, 1.5, 255 }, bold = 200, solid = true },      -- maximum
}
local baseWeight = {}
local lastBoost = nil   -- text boost / effect last applied: a change alters font weights, so text widths
local function boost() return BOOST[tonumber(SKIN:GetVariable('TextBoost', '0')) or 0] or BOOST[0] end
local function solidOf(t) return ((t[1] * 0.299 + t[2] * 0.587 + t[3] * 0.114) >= 128) and { 255, 255, 255 } or { 0, 0, 0 } end

-- Colours to show: { W = entry, [meter] = entry }
local function applyColours(set)
  local roles = ROLES[me] or {}
  local W = set.W or DEFAULT
  local B = boost()
  if B.solid then
    local s = {}
    for k, e in pairs(set) do
      local t = solidOf(e.text)
      s[k] = { text = t, pill = e.pill, shadow = { 255 - t[1], 255 - t[2], 255 - t[3], 255 } }
    end
    set = s; W = set.W or DEFAULT
  end
  local function alphaOf(role) return B.solid and 255 or ALPHA[role] end
  -- Weather icons get the same readability help as the text: the Clock's Halo_<icon>_1..8 meters draw a 1 px
  -- outline (for the Border effect, or a halo boost) and Halo_<icon>_9 a drop shadow, all in the shadow colour.
  local function iconHalo(m, e)
    local effect = B.effect or SKIN:GetVariable('TextEffect', 'Shadow')
    local strength = B.halo and 255 or (e.shadow[4] or 170)
    local outline, drop = 0, nil
    if effect == 'Border' then outline = strength end
    if B.halo then
      local h = B.halo
      -- a halo centred on the text (no offset) becomes a softer outline; an offset one becomes the drop shadow
      if h[1] == 0 and h[2] == 0 then outline = math.max(outline, math.floor(h[4] * 0.6)) else drop = { h[1], h[2], h[4] } end
    end
    if not drop and effect == 'Shadow' then drop = { 1, 1, strength } end
    local rgb = string.format('%.3f;%.3f;%.3f;0;1', e.shadow[1] / 255, e.shadow[2] / 255, e.shadow[3] / 255)
    for i = 1, 9 do
      local h = 'Halo_' .. m .. '_' .. i
      if SKIN:GetMeter(h) then
        local a = (i <= 8) and outline or (drop and drop[3] or 0)
        SKIN:Bang('!SetOption', h, 'ColorMatrix4', string.format('0;0;0;%.3f;0', a / 255))
        SKIN:Bang('!SetOption', h, 'ColorMatrix5', rgb)
      end
    end
    -- (the halo offsets are pixels at 100%: they grow with the widget size, like the icons do)
    if drop then
      SKIN:Bang('!SetVariable', 'WxDropX', drop[1] * scale()); SKIN:Bang('!SetVariable', 'WxDropY', drop[2] * scale())
    end
  end
  local function textEffects(m, e, role)
    SKIN:Bang('!SetOption', m, 'StringEffect', B.effect or SKIN:GetVariable('TextEffect', 'Shadow'))
    local inl = {}
    if B.halo then
      local h = B.halo
      local k = scale()
      inl[#inl + 1] = 'Shadow | ' .. h[1] * k .. ' | ' .. h[2] * k .. ' | ' .. h[3] * k .. ' | ' .. col(e.shadow, h[4])
    end
    -- per-line mode: the text itself shades from one end to the other, following the wallpaper beneath it
    if e.tend and role ~= 'battery' then
      inl[#inl + 1] = 'GradientColor | ' .. ((e.ang and e.ang[1]) or 0) .. ' | '
        .. col(e.text, alphaOf(role)) .. ' ; 0.0 | ' .. col(B.solid and e.text or e.tend, alphaOf(role)) .. ' ; 1.0'
    end
    -- InlineSetting, InlineSetting2: Rainmeter stops at the first empty one
    for i = 1, 2 do SKIN:Bang('!SetOption', m, i == 1 and 'InlineSetting' or 'InlineSetting2', inl[i] or '') end
    if baseWeight[m] == nil then
      local mt = SKIN:GetMeter(m)
      baseWeight[m] = tonumber(mt and mt:GetOption('FontWeight', '400') or '400') or 400
    end
    SKIN:Bang('!SetOption', m, 'FontWeight', math.min(900, baseWeight[m] + (B.bold or 0)))
  end
  -- widget-wide variables (used by anything not listed in ROLES, e.g. the battery colour in battery.lua)
  SKIN:Bang('!SetVariable', 'ColorMain', col(W.text, 255)); SKIN:Bang('!SetVariable', 'ColorSub', col(W.text, 225))
  SKIN:Bang('!SetVariable', 'ColorLabel', col(W.text, 155)); SKIN:Bang('!SetVariable', 'ColorCity', col(W.text, 175))
  SKIN:Bang('!SetVariable', 'ColorShadow', col(W.shadow)); SKIN:Bang('!SetVariable', 'IconTint', col(W.text, 255))
  SKIN:Bang('!SetVariable', 'PillColor', col(W.pill))
  for m, role in pairs(roles) do
    local e = set[m] or W
    if role == 'icon' and WX_ICONS[m] then
      -- colour-coded weather icons keep their own colours: light set on dark backgrounds, deep set on light ones
      SKIN:Bang('!SetOption', m, 'ImageTint', '255,255,255,255')
      local lum = e.text[1] * 0.299 + e.text[2] * 0.587 + e.text[3] * 0.114
      SKIN:Bang('!SetVariable', 'WxShade_' .. m, lum >= 128 and 'light' or 'deep')
      iconHalo(m, e)
    elseif role == 'icon' then
      SKIN:Bang('!SetOption', m, 'ImageTint', col(e.text, 255))
    elseif role == 'battery' then   -- battery.lua owns that meter's colour (low-battery warning)
      SKIN:Bang('!SetVariable', 'ColorBattery', col(e.text, alphaOf(role)))
      SKIN:Bang('!SetOption', m, 'FontEffectColor', col(e.shadow, B.halo and 255 or nil))
      textEffects(m, e, role)
    else
      SKIN:Bang('!SetOption', m, 'FontColor', col(e.text, alphaOf(role)))
      SKIN:Bang('!SetOption', m, 'FontEffectColor', col(e.shadow, B.halo and 255 or nil))
      textEffects(m, e, role)
    end
    SKIN:Bang('!SetVariable', 'PillFill_' .. m, col(e.pill))
  end
  if SKIN:GetMeasure('MeasureBatteryText') then SKIN:Bang('!UpdateMeasure', 'MeasureBatteryText') end
  -- colour fades call this ten times a second; only a boost/effect change can resize text and needs two passes
  local fx = SKIN:GetVariable('TextBoost', '0') .. '|' .. SKIN:GetVariable('TextEffect', '')
  redrawAll(fx == lastBoost)
  lastBoost = fx
end

local function readTheme()
  local set, mode = {}, 'off'
  local f = io.open(STATE .. 'bgtheme.txt', 'r')
  if not f then return mode, set end
  for line in f:lines() do
    local m = line:match('^mode=(%a+)')
    if m then mode = m
    else
      local kind, cfg, rest = line:match('^(%a)|(.-)|(.*)$')
      if kind == 'W' and cfg == me then set.W = parseEntry(rest)
      elseif kind == 'L' then
        local c2, meter, rest2 = line:match('^L|(.-)|(.-)|(.*)$')
        if c2 == me then set[meter] = parseEntry(rest2) end
      end
    end
  end
  f:close()
  return mode, set
end

local function sameSet(a, b)
  if not a or not b then return false end
  for k, e in pairs(b) do
    local o = a[k]
    if not o then return false end
    for _, part in ipairs({ 'text', 'pill', 'shadow', 'tend', 'ang' }) do
      if (e[part] == nil) ~= (o[part] == nil) then return false end
      for i = 1, #(e[part] or {}) do if o[part][i] ~= e[part][i] then return false end end
    end
  end
  for k in pairs(a) do if not b[k] then return false end end
  return true
end

-- Per-line mode: publish where each text line sits (skin px, relative to the window) for Loopscape to sample beside
function writeLines()
  if themeMode ~= 'perline' then return end
  local roles = ROLES[me] or {}
  local out = { 'size ' .. SKIN:GetW() .. ' ' .. SKIN:GetH() }
  for m in pairs(roles) do
    local mt = SKIN:GetMeter(m)
    if mt and mt:GetW() > 1 then
      out[#out + 1] = string.format('%s %d %d %d %d', m, mt:GetX() + mt:GetW() / 2, mt:GetY() + mt:GetH() / 2, mt:GetW(), mt:GetH())
    end
  end
  table.sort(out)
  local text = table.concat(out, '\n') .. '\n'
  if text == lastLinesText then return end
  lastLinesText = text
  local f = io.open(STATE .. 'lines-' .. me:gsub('\\', '_') .. '.txt', 'w')
  if f then f:write(text); f:close() end
end

writeLinesNow = writeLines

-- Called every Update: read Loopscape's colours (at most once a second) and fade/apply them
function applyStashed(set) fadeStart = nil; shown = set; applyColours(set) end

-- instant = the wallpaper was just swapped (Loopscape calls ThemeNow): read now and apply without a fade
function checkTheme(instant)
  local now = os.clock()
  if instant then fadeStart = nil; lastThemeRead = 0 end
  if fadeStart then
    local t = math.min(1, (now - fadeStart) / FADE)
    local cur = {}
    for k, e in pairs(target) do
      local a = fadeFrom[k] or fadeFrom.W or DEFAULT
      cur[k] = { text = lerp(a.text, e.text, t), pill = lerp(a.pill, e.pill, t), shadow = lerp(a.shadow, e.shadow, t),
                 tend = e.tend and lerp(a.tend or a.text, e.tend, t), ang = e.ang }
    end
    applyColours(cur)
    if t >= 1 then fadeStart = nil; shown = target end
    return
  end
  -- the Settings panel changed Text boost / Text effect: re-apply the current colours with the new effects
  local fx = SKIN:GetVariable('TextBoost', '0') .. '|' .. SKIN:GetVariable('TextEffect', '')
  if fx ~= lastFx then
    local first = lastFx == nil
    lastFx = fx
    if not first and shown then applyColours(shown) end
  end
  if now - lastThemeRead < 1 then return end
  lastThemeRead = now
  local mode, set = readTheme()
  if mode ~= themeMode then themeMode = mode; lastLinesText = nil end
  writeLines()
  if mode == 'off' or not set.W then set = { W = DEFAULT } end
  -- fading out for a wallpaper crossfade: colours for where we're going wait until we're invisible (stepWallFade)
  local at = set.W and set.W.at
  if wallFade and not wallFade.moved and at and not (at[1] == SKIN:GetX() and at[2] == SKIN:GetY()) then
    wallFade.colours = set
    return
  end
  local arriving = movingTo(set)   -- colours for the spot we're about to move to: no fade, then move
  if sameSet(shown, set) then
    if arriving then finishMove() end
    return
  end
  if FADE_MODES[mode] and shown and not instant and not arriving then
    fadeFrom, target, fadeStart = shown, set, now
    SKIN:Bang('!CommandMeasure', 'MeasureFastTick', 'Execute 2')   -- 100 ms ticks for the fade
  else
    shown = set
    applyColours(set)
  end
  if arriving then dbg('move: colours for the new spot applied, moving'); finishMove() end
end
function ThemeNow() checkTheme(true) end
-- Loopscape just wrote wallstyle.txt: read it now instead of within the next second
function PlaceNow() lastPlaceCheck = -1; checkPlacement() end
-- ---------- views ----------
-- Each widget has numbered views (View=0 is the full view) named in ViewNames ("Full|Simple|..."). A meter in
-- group V<n> has Hidden=(#View#>=n), so it is hidden from view n upwards; the panel size follows via #View# in its formulas.
local VIEW_MENU = 'Loopscape\\ViewMenu'

local function viewNames()
  local names = {}
  for n in (SKIN:GetVariable('ViewNames', 'Full|Simple')):gmatch('[^|]+') do names[#names + 1] = n end
  return names
end

-- Meters hide themselves via Hidden=(#View#>=n); after changing View they just need re-evaluating
function applyView()   -- global: Update (defined above) calls it
  redrawAll()
end

function SetView(n)
  local count = #viewNames()
  n = math.max(0, math.min(count - 1, tonumber(n) or 0))
  SKIN:Bang('!SetVariable', 'View', n)
  SKIN:Bang('!WriteKeyValue', 'Variables', 'View', n)
  applyView()
  -- Network widget: read the connection details now if the new view shows them (they're skipped in the Speeds view)
  if SKIN:GetMeasure('MeasureNetshTimer') then SKIN:Bang('!UpdateMeasure', 'MeasureNetshTimer') end
  -- re-measure and tell the stack right away instead of on the next tick
  naturalWidth(true)
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
  Update()
end

function CycleView()
  local v = tonumber(SKIN:GetVariable('View', '0')) or 0
  SetView((v + 1) % #viewNames())
end
ToggleSimple = CycleView   -- older menu entries

-- Middle-click: with two views just switch; with more, open a small menu at the cursor
function ViewMenu(mx, my)
  local names = viewNames()
  if #names <= 2 then CycleView(); return end
  SKIN:Bang('!ActivateConfig', VIEW_MENU, 'ViewMenu.ini')
  SKIN:Bang('!SetVariable', 'Target', me, VIEW_MENU)
  SKIN:Bang('!SetVariable', 'Names', table.concat(names, '|'), VIEW_MENU)
  SKIN:Bang('!SetVariable', 'Current', SKIN:GetVariable('View', '0'), VIEW_MENU)
  -- open with the cursor just inside the menu's top-left corner, so moving away closes it
  local inset = math.floor(14 * scale() + 0.5)
  SKIN:Bang('!Move', SKIN:GetX() + (tonumber(mx) or 0) - inset, SKIN:GetY() + (tonumber(my) or 0) - inset, VIEW_MENU)
  SKIN:Bang('!CommandMeasure', 'MeasureMenu', 'Build()', VIEW_MENU)
end

-- Context menu: "Next wallpaper" (Loopscape watches %LOCALAPPDATA%\Loopscape\wallnext.txt and skips to the next style)
function NextWallpaper()
  local f = io.open(STATE .. 'wallnext.txt', 'w')
  if f then f:write(os.time(), ' ', os.clock(), '\n'); f:close() end
end

-- Context menu: "Toggle snapping"
function Toggle()
  enabled = not enabled
  local v = enabled and '1' or '0'
  SKIN:Bang('!SetVariable', 'SnapEnabled', v)
  SKIN:Bang('!WriteKeyValue', 'Variables', 'SnapEnabled', v, SKIN:GetVariable('@') .. 'Settings.inc')
  -- (switched back on: the place is worked out again from where the widget is now)
  if enabled then still, place = 0, nil else hideGuides(); attach = nil; saveAttach() end
end
