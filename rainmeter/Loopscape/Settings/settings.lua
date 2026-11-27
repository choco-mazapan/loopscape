-- Loopscape settings panel: five tabs (Wallpaper, Rotation, Widgets, Look, Profiles). Shows the current value of each
-- preference and changes it on click. Preferences live in @Resources\Settings.inc (included by both widgets); changing one
-- rewrites that file and applies it to the running widgets.

-- Files shared with Loopscape.exe while it runs (what it plays, commands, colours) live in %LOCALAPPDATA%\Loopscape,
-- outside Documents, which OneDrive may sync. Loopscape.exe creates the folder.
local STATE = (os.getenv('LOCALAPPDATA') or ((os.getenv('USERPROFILE') or '') .. '\\AppData\\Local')) .. '\\Loopscape\\'

-- The widgets: config name (= folder = ini name), its Show<name> setting, and whether it's shown when Settings.inc
-- doesn't say (an older Settings.inc; the installer shows the first three then). In MeterWTile1..N order.
local WIDGET_TILES = {
  { 'Clock', '1' }, { 'WiFi', '1' }, { 'NowShowing', '1' }, { 'SunMoon', '0' }, { 'Rain', '0' }, { 'System', '0' },
  { 'Calendar', '0' },
}
local WIDGETS = {}
for i, w in ipairs(WIDGET_TILES) do WIDGETS[i] = 'Loopscape\\' .. w[1] end
local INC

-- Always animate: Settings.inc's WallAlwaysAnimate wins; while it's empty (a Settings.inc from before the panel had the
-- row), Loopscape uses alwaysAnimate= in %USERPROFILE%\Loopscape\Loopscape.ini, which the tray used to set
local function alwaysAnimateFromIni()
  local f = io.open((os.getenv('USERPROFILE') or '') .. '\\Loopscape\\Loopscape.ini', 'r')
  if not f then return '0' end
  local on = false
  for line in f:lines() do
    local v = line:match('^%s*alwaysAnimate%s*=%s*(%S*)')
    if v then v = v:lower(); on = v == 'true' or v == '1' or v == 'yes' end
  end
  f:close()
  return on and '1' or '0'
end

-- Each row: variable, meter that shows the value, list of { value, label }, value when Settings.inc doesn't have it
-- (a string, or a function that works it out). Optional fields:
--   anyOn = value   a two-option row whose stored value can also be one of several older ones: anything but the
--                   other option counts as this one (Text colour used to have six modes)
--   custom = fn     the value isn't in the list (a typed number): fn(value) gives its label
--   compound        the row sets more than one variable: read() gives the value to show, write(value) the
--                   { variable = value } to store
local ROWS = {
  { 'MatchWidths',   'MeterVal1', { { '1', 'On' }, { '0', 'Off' } } },
  -- read by snap.lua: whether a drop snaps, and how close to a target it has to land (SnapEnabled + SnapRadius)
  { 'SnapEnabled',   'MeterVal2', { { '0', 'Off' }, { '24', 'Within 24 px' }, { '48', 'Within 48 px' }, { '96', 'Within 96 px' } },
    custom = function(v) return 'Within ' .. v .. ' px' end,
    compound = {
      read = function()
        if SKIN:GetVariable('SnapEnabled', '1') == '0' then return '0' end
        local r = SKIN:GetVariable('SnapRadius', '')
        return r ~= '' and r or '48'
      end,
      write = function(v) if v == '0' then return { SnapEnabled = '0' } end; return { SnapEnabled = '1', SnapRadius = v } end,
    } },
  -- read by snap.lua: drops that land near no target move to a grid of this spacing (SnapGrid + SnapGridSize)
  { 'SnapGrid',      'MeterVal20', { { '0', 'Off' }, { '12', '12 px' }, { '24', '24 px' }, { '48', '48 px' } },
    custom = function(v) return v .. ' px' end,
    compound = {
      read = function()
        if SKIN:GetVariable('SnapGrid', '1') == '0' then return '0' end
        local g = SKIN:GetVariable('SnapGridSize', '')
        return g ~= '' and g or '24'
      end,
      write = function(v) if v == '0' then return { SnapGrid = '0' } end; return { SnapGrid = '1', SnapGridSize = v } end,
    } },
  -- read by snap.lua: how far the three bottom snap spots sit above the bottom of the screen. "Custom" opens the text
  -- box for any number of pixels.
  { 'SnapBottomMargin', 'MeterVal19', { { '76', 'Above taskbar (76 px)' }, { '28', 'Over taskbar (28 px)' },
                                        { '8', 'Screen edge (8 px)' }, { '#custom', 'Custom height...' } },
    custom = function(v) return v .. ' px' end },
  { 'WeatherUnit',   'MeterVal3', { { 'fahrenheit', '[\\x00B0]F' }, { 'celsius', '[\\x00B0]C' } } },
  { 'WeatherLocation', 'MeterVal21', { { 'city', 'City' }, { 'windows', 'Windows location' } } },
  { 'PanelAlpha',    'MeterVal4', { { '0', 'Off' }, { '110', 'Light' }, { '150', 'Medium' }, { '200', 'Dark' } } },
  { 'TextEffect',    'MeterVal8', { { 'Shadow', 'Shadow' }, { 'Border', 'Outline' }, { 'None', 'None' } } },
  { 'TextPills',     'MeterVal10', { { '1', 'On' }, { '0', 'Off' } } },
  -- read by every widget through @Resources\Scale.inc: 0 = automatic (from the screen height), or a factor.
  -- Initialize adds the automatic size to the Auto label
  { 'WidgetScale',   'MeterVal27', { { '0', 'Auto' }, { '1', '100%' }, { '1.25', '125%' }, { '1.5', '150%' },
                                     { '1.75', '175%' }, { '2', '200%' }, { '2.5', '250%' } }, '0' },
  -- read by snap.lua in each widget (see BOOST there)
  { 'TextBoost',     'MeterVal18', { { '0', 'Off' }, { '1', '1 Soft shadow' }, { '2', '2 Drop shadow' }, { '3', '3 Halo' },
                                     { '4', '4 Outline + halo' }, { '5', '5 Bold + outline' }, { '6', '6 Maximum' } } },
  -- read by Loopscape (WidgetThemes). On is "smooth": white or dark text from the wallpaper around each widget, checked
  -- every second and faded between. The other modes Loopscape still understands (contrast, tinted, smoothtinted,
  -- perline) were either slower to react or gave less contrast, so the panel offers only this one.
  { 'TextColorMode', 'MeterVal9', { { 'smooth', 'On' }, { 'off', 'Off' } }, 'off', anyOn = 'smooth' },
  { 'ClockFormat',   'MeterVal7', { { '24', '24-hour' }, { '12', '12-hour' } } },
  -- the clock's date line: a Rainmeter Time measure Format (MeasureDate in Clock.ini)
  { 'DateFormat',    'MeterVal6', { { '%a, %#d %B', 'Wed, 25 November' }, { '%a, %m/%d/%y', 'MM/DD/YY' },
                                    { '%a, %d/%m/%y', 'DD/MM/YY' }, { '%a, %m/%d/%Y', 'MM/DD/YYYY' },
                                    { '%a, %d/%m/%Y', 'DD/MM/YYYY' } }, '%a, %#d %B' },
  -- read by snap.lua in each widget (moves them to the wallpaper style's recommended spot)
  { 'AutoPlace',     'MeterVal11', { { '1', 'On' }, { '0', 'Off' } } },
  -- Rotation tab: read by Loopscape straight from Settings.inc
  { 'WallRotateMinutes', 'MeterVal12', { { '15', '15 minutes' }, { '30', '30 minutes' }, { '60', '1 hour' },
                                         { '120', '2 hours' }, { '240', '4 hours' } } },
  { 'WallNight',     'MeterVal13', { { 'fixed', '19:00 [\\x2013] 07:00' }, { 'sun', 'Sunset to sunrise' } } },
  { 'WallWeather',   'MeterVal14', { { '1', 'On' }, { '0', 'Off' } } },
  { 'WallOnBattery', 'MeterVal15', { { 'pause', 'Pause' }, { 'play', 'Keep playing' }, { 'static', 'Still image' } } },
  { 'WallPauseCovered', 'MeterVal16', { { '1', 'On' }, { '0', 'Off' } } },
  -- also in the tray menu, which writes the same key
  { 'WallAlwaysAnimate', 'MeterVal5', { { '1', 'On' }, { '0', 'Off' } }, alwaysAnimateFromIni },
}

-- Rows that do nothing while another setting is in a certain state: drawn greyed out, with the hint saying why, and
-- clicks ignored. Row number n = its MeterLabelN / MeterHintN / MeterValN. The check gets the current value of a
-- variable (with the row's default applied).
local DIMMED = {
  { n = 20, why = 'Snapping is off',                when = function(get) return get('SnapEnabled') == '0' end },
  { n = 22, why = 'Not used with Windows location', when = function(get) return get('WeatherLocation') == 'windows' end },
  { n = 10, why = 'Off while Panel darkness is on', when = function(get) return get('PanelAlpha') ~= '0' end },
  { n = 9,  why = 'Off while Panel darkness is on', when = function(get) return get('PanelAlpha') ~= '0' end },
  { n = 8,  why = 'Not used while Text boost is on', when = function(get) return get('TextBoost') ~= '0' end },
  { n = 15, why = 'Always animate overrides it',    when = function(get) return get('WallAlwaysAnimate') == '1' end },
  { n = 16, why = 'Always animate overrides it',    when = function(get) return get('WallAlwaysAnimate') == '1' end },
}
local dimmed = {}   -- n -> true while greyed out
local HINTS = {}    -- the hints as Settings.ini has them, read in Initialize

-- Wallpaper tiles, in MeterTile1..N order (Settings.ini lays them out: rotate, then day grid, then night grid)
local TILES = { 'rotate',
  'spectrum', 'halftone', 'bluesky', 'pixelclouds', 'sunset', 'riso', 'dunes', 'lake', 'pastel', 'crt', 'shore', 'gradient',
  'nightclouds', 'aurora', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce' }
local NAMES = { rotate = 'Rotate day/night',
  spectrum = 'Spectrum', halftone = 'Halftone', bluesky = 'Blue sky', pixelclouds = 'Pixel clouds', sunset = 'Sunset strata',
  riso = 'Riso waves', dunes = 'Dunes', lake = 'Lake', pastel = 'Pastel aurora', crt = 'CRT test card', shore = 'Shore', gradient = 'Gradient',
  nightclouds = 'Night clouds', aurora = 'Aurora', nebula = 'Nebula', moon = 'Halftone moon', embers = 'Embers',
  deepsea = 'Deep sea', rain = 'Rainy window', bounce = 'Bouncing logo', loop = 'Gradient' }

local TABS = { 'Wallpaper', 'Rotation', 'Widgets', 'Look', 'Profiles' }
local STYLES_DIR = (os.getenv('USERPROFILE') or '') .. '\\Loopscape\\styles\\'

local function excluded()
  local set = {}
  for s in (SKIN:GetVariable('WallExclude', '')):gmatch('[^,%s]+') do set[s:lower()] = true end
  return set
end

local function row(var) for _, r in ipairs(ROWS) do if r[1] == var then return r end end end

-- A setting's value, with the row's default when Settings.inc doesn't have it, and older values folded into anyOn
local function current(var)
  local r = row(var)
  if r and r.compound then return r.compound.read() end
  local v = SKIN:GetVariable(var, '')
  if v == '' and r and r[4] then
    if type(r[4]) == 'function' then v = r[4]() else v = r[4] end
  end
  if r and r.anyOn and v ~= '' and v ~= r[3][2][1] then return r.anyOn end
  return v
end

local function label(r, value)
  for _, opt in ipairs(r[3]) do if opt[1] == value then return opt[2] end end
  if r.custom and value ~= '' then return r.custom(value) end
  return value
end

local function isDimmed(meter)
  local n = tonumber((meter or ''):match('(%d+)$'))
  return n ~= nil and dimmed[n] == true
end

-- Grey out (or restore) the rows in DIMMED. Colours are the panel's own, from its variables.
local function showDimmed()
  local main, sub, pill = SKIN:GetVariable('ColorMain'), SKIN:GetVariable('ColorSub'), SKIN:GetVariable('ColorPill')
  for _, d in ipairs(DIMMED) do
    local on = d.when(current) and true or false
    dimmed[d.n] = on
    local val = 'MeterVal' .. d.n
    SKIN:Bang('!SetOption', 'MeterLabel' .. d.n, 'FontColor', on and '255,255,255,95' or main)
    SKIN:Bang('!SetOption', 'MeterHint' .. d.n, 'FontColor', on and '255,255,255,95' or sub)
    SKIN:Bang('!SetOption', 'MeterHint' .. d.n, 'Text', on and d.why or (HINTS[d.n] or ''))
    SKIN:Bang('!SetOption', val, 'FontColor', on and '255,255,255,80' or main)
    SKIN:Bang('!SetOption', val, 'SolidColor', on and '255,255,255,10' or pill)
    -- no hover light on a button that does nothing
    SKIN:Bang('!SetOption', val, 'MouseOverAction', on and '' or
      ('[!SetOption ' .. val .. ' SolidColor #ColorPillHover#][!UpdateMeter ' .. val .. '][!Redraw]'))
    SKIN:Bang('!SetOption', val, 'MouseLeaveAction', on and '' or
      ('[!SetOption ' .. val .. ' SolidColor #ColorPill#][!UpdateMeter ' .. val .. '][!Redraw]'))
  end
end

local showProfiles   -- defined with the profiles, below

local function show()
  for _, r in ipairs(ROWS) do
    SKIN:Bang('!SetOption', r[2], 'Text', label(r, current(r[1])))
  end
  local city = SKIN:GetVariable('WeatherCity', '')
  SKIN:Bang('!SetOption', 'MeterVal22', 'Text', city ~= '' and city or 'Type a city')
  showDimmed()
  showProfiles()
  local cur = SKIN:GetVariable('WallpaperMode', '')
  local ex = excluded()
  for i, id in ipairs(TILES) do
    SKIN:Bang('!SetVariable', 'TileBase' .. i, SKIN:GetVariable(id == cur and 'ColorSel' or 'ColorPill'))
    -- styles left out of the rotation: dimmed and struck through
    SKIN:Bang('!SetVariable', 'TileFont' .. i, ex[id] and '255,255,255,85' or '255,255,255,255')
    SKIN:Bang('!SetOption', 'MeterTile' .. i, 'InlineSetting', ex[id] and 'Strikethrough' or '')
  end
  -- widget tiles: lit while the widget is on screen
  for i, w in ipairs(WIDGET_TILES) do
    local on = SKIN:GetVariable('Show' .. w[1], w[2]) ~= '0'
    SKIN:Bang('!SetVariable', 'WTileBase' .. i, SKIN:GetVariable(on and 'ColorSel' or 'ColorPill'))
    SKIN:Bang('!SetVariable', 'WTileFont' .. i, SKIN:GetVariable(on and 'ColorMain' or 'ColorSub'))
  end
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

-- ---------- "Playing now" (Loopscape writes %LOCALAPPDATA%\Loopscape\wallstatus.txt) and tile previews ----------
local hovered = 0
local lastStatus = nil

local function readStatus()
  local f = io.open(STATE .. 'wallstatus.txt', 'r')
  if not f then return {} end
  local t = {}
  for line in f:lines() do local k, v = line:match('^(%w+)=(.*)$'); if k then t[k] = v end end
  f:close()
  return t
end

local function setText(m, text) SKIN:Bang('!SetOption', m, 'Text', text) end
-- The styles' first frames are styles\<style>.png. The Gradient (loop.mp4) has none, so the skin ships its own picture.
local function preview(id)
  local png = ''
  if id == 'gradient' or id == 'loop' then png = SKIN:GetVariable('@') .. 'gradient.png'
  elseif id and id ~= '' then png = STYLES_DIR .. id .. '.png' end
  SKIN:Bang('!SetOption', 'MeterPreview', 'ImageName', png)
end

local function showNow(force)
  if hovered ~= 0 then return end
  local s = readStatus()
  local key = (s.style or '') .. '|' .. (s.reason or '') .. '|' .. (s.next or '') .. '|' .. (s.state or '')
  if key == lastStatus and not force then return end
  lastStatus = key
  setText('MeterNowHead', 'PLAYING NOW')
  setText('MeterNowName', NAMES[s.style or ''] or (s.style ~= '' and s.style) or 'Nothing playing')
  -- the lines under the name, packed together so a missing one leaves no gap. "One style" isn't shown: the
  -- highlighted tile already says the rotation is off.
  local lines = {}
  local reason = s.reason or ''
  if reason ~= '' and reason:lower() ~= 'one style' then lines[#lines + 1] = reason end
  if s.next and s.next ~= '' then lines[#lines + 1] = 'Next change ' .. s.next end
  local state = s.state or ''
  if state ~= '' then lines[#lines + 1] = state:sub(1, 1):upper() .. state:sub(2) end
  setText('MeterNowReason', lines[1] or '')
  setText('MeterNowNext', lines[2] or '')
  setText('MeterNowState', lines[3] or '')
  preview(s.style)
  for _, m in ipairs({ 'MeterNowHead', 'MeterNowName', 'MeterNowReason', 'MeterNowNext', 'MeterNowState', 'MeterPreview' }) do
    SKIN:Bang('!UpdateMeter', m)
  end
  SKIN:Bang('!Redraw')
end

-- Hovering a tile previews it in the "Playing now" box; leaving it goes back to what's playing
function Hover(i)
  i = tonumber(i) or 0
  hovered = i
  if i == 0 then showNow(true); return end
  local id = TILES[i]
  local ex = excluded()
  setText('MeterNowHead', 'PREVIEW')
  setText('MeterNowName', NAMES[id] or id)
  if id == 'rotate' then
    setText('MeterNowReason', 'Day styles by day, night ones after dark')
    setText('MeterNowNext', 'Timing: see the Rotation tab')
    setText('MeterNowState', '')
    preview(readStatus().style)
  else
    setText('MeterNowReason', ex[id] and 'Skipped in the rotation' or 'In the rotation')
    setText('MeterNowNext', 'Click to play it')
    setText('MeterNowState', 'Right-click to ' .. (ex[id] and 'put it back' or 'skip it'))
    preview(id)
  end
  for _, m in ipairs({ 'MeterNowHead', 'MeterNowName', 'MeterNowReason', 'MeterNowNext', 'MeterNowState', 'MeterPreview' }) do
    SKIN:Bang('!UpdateMeter', m)
  end
  SKIN:Bang('!Redraw')
end

-- ---------- dropdown (settings with more than two options) ----------
local DD_MAX = 8
local ddKey = nil    -- what the open list belongs to (a row's variable, or 'profileN')
local ddPick = nil   -- function(i) run when item i is picked

-- Note: the value buttons have no tooltips on purpose. Rainmeter shows a meter's tooltip even under the dropdown,
-- and changing ToolTipText at runtime leaves orphaned tooltip windows on screen.
function CloseDropdown()
  ddKey, ddPick = nil, nil
  SKIN:Bang('!HideMeterGroup', 'Dropdown')
  SKIN:Bang('!Redraw')
end

function ShowTab(name)
  if ddKey then CloseDropdown() end
  for i, t in ipairs(TABS) do
    local on = (t == name)
    SKIN:Bang(on and '!ShowMeterGroup' or '!HideMeterGroup', 'Tab' .. t)
    SKIN:Bang('!SetVariable', 'TabBase' .. i, SKIN:GetVariable(on and 'ColorSel' or 'ColorPill'))
    SKIN:Bang('!SetVariable', 'TabFont' .. i, SKIN:GetVariable(on and 'ColorMain' or 'ColorSub'))
  end
  SKIN:Bang('!SetVariable', 'Tab', name)
  SKIN:Bang('!WriteKeyValue', 'Variables', 'Tab', name)   -- reopen on the same tab next time
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

-- ---------- Update Loopscape row (Look tab): what update.ps1 last found, from %LOCALAPPDATA%\Loopscape\updatestatus.txt ----------
-- Update Loopscape writes it (update.ps1 for a git clone, Loopscape.exe's Updater.cs for an installed copy); nothing
-- checks GitHub in the background. sync.ps1 deletes it when it installs another commit by hand.
local lastUpdateText = nil

local function readUpdateStatus()
  local f = io.open(STATE .. 'updatestatus.txt', 'r')
  if not f then return nil end
  local st = {}
  for line in f:lines() do
    local k, v = line:match('^([%w]+)=(.*)$')
    if k then st[k] = v:gsub('%s+$', '') end
  end
  f:close()
  return st
end

-- "today", "yesterday" or "26 Nov" for the checked= time ("2026-11-26 17:20")
local function checkedDay(stamp)
  local y, m, d = (stamp or ''):match('^(%d+)-(%d+)-(%d+)')
  if not y then return nil end
  local t = os.time({ year = tonumber(y), month = tonumber(m), day = tonumber(d), hour = 12 })
  local today = os.date('*t')
  local days = math.floor((os.time({ year = today.year, month = today.month, day = today.day, hour = 12 }) - t) / 86400 + 0.5)
  if days == 0 then return 'today' elseif days == 1 then return 'yesterday' end
  return tostring(tonumber(d)) .. ' ' .. os.date('%b', t)
end

local function showUpdate(force)
  local st = readUpdateStatus() or {}
  local label, hint, button = 'Update Loopscape', 'Get the latest version from GitHub', 'Update  [\\x203A]'
  local day = checkedDay(st.checked)
  if st.state == 'uptodate' and (st.version or '') ~= '' then
    label = 'Loopscape is up to date'
    hint = 'Version ' .. st.version .. (day and (', checked ' .. day) or '')
    button = 'Check again  [\\x203A]'
  elseif st.state == 'available' and (tonumber(st.count) or 0) > 0 then
    local n = tonumber(st.count)
    hint = n .. ' new change' .. (n == 1 and '' or 's') .. ' on GitHub' .. (day and (', checked ' .. day) or '')
  elseif st.state == 'available' and (st.latest or '') ~= '' then
    -- an installed copy: a new release rather than a count of commits
    hint = 'Version ' .. st.latest .. ' is available' .. (day and (', checked ' .. day) or '')
  end
  local key = label .. '|' .. hint .. '|' .. button
  if key == lastUpdateText and not force then return end
  lastUpdateText = key
  SKIN:Bang('!SetOption', 'MeterLabel23', 'Text', label)
  SKIN:Bang('!SetOption', 'MeterHint23', 'Text', hint)
  SKIN:Bang('!SetOption', 'MeterVal23', 'Text', button)
  if not force then
    SKIN:Bang('!UpdateMeter', 'MeterLabel23'); SKIN:Bang('!UpdateMeter', 'MeterHint23'); SKIN:Bang('!UpdateMeter', 'MeterVal23')
    SKIN:Bang('!Redraw')
  end
end

-- The size Auto picks on this screen, worked out as Scale.inc does: the screen height over 1080, in steps of 0.25
-- from 1 to 3 (1440p 125%, 4K 200%)
local function autoScale()
  local h = tonumber(SKIN:GetVariable('SCREENAREAHEIGHT', '1080')) or 1080
  local f = math.floor(h * 4 / 1080 + 0.5) / 4
  return math.max(1, math.min(3, f))
end

function Initialize()
  INC = SKIN:GetVariable('@') .. 'Settings.inc'
  row('WidgetScale')[3][1][2] = 'Auto (' .. math.floor(autoScale() * 100 + 0.5) .. '%)'
  for _, d in ipairs(DIMMED) do
    local m = SKIN:GetMeter('MeterHint' .. d.n)
    HINTS[d.n] = m and m:GetOption('Text') or ''
  end
  local tab = SKIN:GetVariable('Tab', 'Wallpaper')
  local known = false
  for _, t in ipairs(TABS) do if t == tab then known = true end end
  show()
  showUpdate(true)   -- before ShowTab, which updates and redraws every meter
  ShowTab(known and tab or 'Wallpaper')
  showNow(true)
end

function Update()
  local tab = SKIN:GetVariable('Tab', '')
  if tab == 'Wallpaper' then showNow(false)
  -- the update window may finish while the panel is open: a small file, read once a second only on this tab
  elseif tab == 'Look' then showUpdate(false) end
  return 0
end

-- Apply a changed setting to the running widgets without refreshing them (a refresh reloads everything:
-- the weather re-fetches, the Wi-Fi details reset and the widgets flicker). Settings.inc is still updated,
-- so the value also holds after the next refresh/restart.
local CLOCK = 'Loopscape\\Clock'

-- Widgets that are switched on (Rainmeter.ini Active=<n>, 0 = off). Bangs to a widget that is off only add
-- warnings to Rainmeter's log. If Rainmeter.ini can't be read, every widget counts as on.
-- (Rainmeter writes the file in UTF-16: dropping the zero bytes leaves the ASCII section names and keys readable.)
local function running()
  local f = io.open(SKIN:GetVariable('SETTINGSPATH', '') .. 'Rainmeter.ini', 'rb')
  local act, sec, found = {}, nil, false
  if f then
    local text = (f:read('*a') or ''):gsub('%z', ''):gsub('^\255\254', '')
    f:close()
    for line in text:gmatch('[^\r\n]+') do
      local s = line:match('^%[(.-)%]%s*$')
      if s then sec = s:lower()
      elseif sec then
        local n = line:match('^Active%s*=%s*(%d+)')
        if n then act[sec] = n ~= '0'; found = true end
      end
    end
  end
  return function(w) return not found or act[w:lower()] == true end
end

local function apply(var, value)
  local shown = var:match('^Show(%w+)$')
  if shown then
    for _, w in ipairs(WIDGET_TILES) do
      if w[1] == shown then
        if value == '1' then SKIN:Bang('!ActivateConfig', 'Loopscape\\' .. shown, shown .. '.ini')
        else
          -- tell the others first: Rainmeter drops the widget from its list at once but runs its OnCloseAction
          -- only after the fade-out, and bangs sent to it in between are logged as warnings
          if running()('Loopscape\\' .. shown) then
            SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Closing()', 'Loopscape\\' .. shown)
          end
          SKIN:Bang('!DeactivateConfig', 'Loopscape\\' .. shown)
        end
      end
    end
    return
  elseif var == 'WidgetScale' then
    -- the widgets work their sizes out when they load (Scale.inc), so refresh the ones on screen. Each keeps its
    -- snap spot: snap.lua places it again at its new size
    local on = running()
    for _, w in ipairs(WIDGETS) do if on(w) then SKIN:Bang('!Refresh', w) end end
    return
  elseif var == 'TextColorMode' or var:match('^Wall') then
    -- read from Settings.inc by Loopscape, which watches the file and crossfades to a new style by itself
    -- (downloading it first if it isn't on this PC yet)
    return
  end
  -- Everything else is a variable the widgets use with DynamicVariables: set it and redraw. The widgets' meters
  -- only update when asked, so update them twice: their panel and pills come before the text they wrap.
  -- (A widget that is off reads Settings.inc when it is switched on.)
  local on = running()
  if var == 'DateFormat' and on(CLOCK) then
    -- MeasureDate reads the format once, when the clock loads: give it the new one and re-read the date
    SKIN:Bang('!SetOption', 'MeasureDate', 'Format', value, CLOCK)
    SKIN:Bang('!UpdateMeasure', 'MeasureDate', CLOCK)
  end
  local shownWidgets = {}
  for _, w in ipairs(WIDGETS) do if on(w) then shownWidgets[#shownWidgets + 1] = w end end
  for _, w in ipairs(shownWidgets) do
    SKIN:Bang('!SetVariable', var, value, w)
    SKIN:Bang('!UpdateMeasure', 'MeasureNaturalWidth', w)   -- not '*': that would fire the weather/Wi-Fi timers
    SKIN:Bang('!UpdateMeter', '*', w)
    SKIN:Bang('!UpdateMeter', '*', w)
    -- the clock's AM/PM text can move on the second pass, after its pill
    if w == 'Loopscape\\Clock' then SKIN:Bang('!UpdateMeter', 'Pill_MeterAmPm', w) end
    SKIN:Bang('!Redraw', w)
  end
  if var == 'SnapBottomMargin' then
    -- move widgets parked on a bottom spot (and anything stacked on them) to the new height
    for _, w in ipairs(shownWidgets) do SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Resnap()', w) end
  end
  local SUN, RAIN = 'Loopscape\\SunMoon', 'Loopscape\\Rain'
  if var == 'WeatherUnit' or var == 'WeatherLocation' or var == 'WeatherCity' then
    -- show the weather in the new unit / for the new place (only the clock shows weather). A unit change reuses
    -- WeatherFetch's cached result; a new location fetches. The Parameter picks the variables up (set above)
    -- through DynamicVariables
    if on(CLOCK) then
      SKIN:Bang('!UpdateMeasure', 'MeasureWeatherRun', CLOCK)
      SKIN:Bang('!CommandMeasure', 'MeasureWeatherRun', 'Run', CLOCK)
    end
    -- the other widgets that read WeatherFetch: Sun & Moon (sun times), Rain
    if on(SUN) then
      SKIN:Bang('!UpdateMeasure', 'MeasureSunRun', SUN)
      SKIN:Bang('!CommandMeasure', 'MeasureSunRun', 'Run', SUN)
    end
    if on(RAIN) then
      SKIN:Bang('!UpdateMeasure', 'MeasureRainRun', RAIN)
      SKIN:Bang('!CommandMeasure', 'MeasureRainRun', 'Run', RAIN)
    end
  end
  if var == 'ClockFormat' then
    -- times shown by the scripts (not by a Time measure)
    if on(SUN) then SKIN:Bang('!UpdateMeasure', 'MeasureSunMoon', SUN) end
    if on(RAIN) then SKIN:Bang('!UpdateMeasure', 'MeasureRain', RAIN) end
  end
end
local function setValue(var, value)
  SKIN:Bang('!SetVariable', var, value)
  SKIN:Bang('!WriteKeyValue', 'Variables', var, value, INC)
  show()
  apply(var, value)
end

-- A row's choice: one variable, or several for a compound row
local function setRow(r, value)
  if not r.compound then setValue(r[1], value); return end
  for k, v in pairs(r.compound.write(value)) do
    if SKIN:GetVariable(k, '') ~= v then setValue(k, v) end
  end
end

function PickTile(i)
  local id = TILES[i]
  if id and id ~= SKIN:GetVariable('WallpaperMode', '') then setValue('WallpaperMode', id) end
end

-- Widgets tab: show / hide a widget
function ToggleWidget(i)
  local w = WIDGET_TILES[tonumber(i) or 0]
  if not w then return end
  setValue('Show' .. w[1], SKIN:GetVariable('Show' .. w[1], w[2]) ~= '0' and '0' or '1')
end

-- Right-click a style: leave it out of the rotation (or put it back)
function ToggleExclude(i)
  local id = TILES[tonumber(i) or 0]
  if not id or id == 'rotate' then return end
  local ex, list = excluded(), {}
  ex[id] = not ex[id] or nil
  for _, t in ipairs(TILES) do if ex[t] then list[#list + 1] = t end end
  setValue('WallExclude', table.concat(list, ','))
  if hovered == tonumber(i) then Hover(i) end
end

-- Skip to the next wallpaper: Loopscape watches %LOCALAPPDATA%\Loopscape\wallnext.txt
function NextWallpaper()
  local f = io.open(STATE .. 'wallnext.txt', 'w')
  if f then f:write(os.time(), ' ', os.clock(), '\n'); f:close() end
end

-- Update Loopscape: Loopscape picks up "update" in %LOCALAPPDATA%\Loopscape\wallcmd.txt and runs the update (or opens the Releases page)
function UpdateLoopscape()
  local f = io.open(STATE .. 'wallcmd.txt', 'a')
  if f then f:write('update\n'); f:close() end
end

-- ---------- the text box: city, custom bottom spot height, profile names ----------
-- One InputText measure (MeasureTextInput) serves every typed field. EditField places it inside the field's value
-- button; Enter calls FieldEntered (the text is in TextInput), and Enter, Esc and clicking elsewhere all call EndEdit.
local PAD_X, PAD_Y = 12, 5   -- StyleValue's Padding
local FIELDS                 -- name -> { meter, minW, number, start(), save(text) }; filled in below
local editing = nil          -- the field being typed in

-- the value buttons' normal hover feedback, also restored by EndEdit
local function hoverAction(meter, on)
  local color = on and '#ColorPillHover#' or '#ColorPill#'
  return '[!SetOption ' .. meter .. ' SolidColor ' .. color .. '][!UpdateMeter ' .. meter .. '][!Redraw]'
end

function EditField(name)
  local f = FIELDS[name]
  if not f or isDimmed(f.meter) then return end
  CloseDropdown()
  if editing then EndEdit() end
  local meterName = f.meter
  local m = SKIN:GetMeter(meterName)
  -- widen the button leftward (it is right-aligned) when its text is short, so there is room to type
  if m:GetW() < f.minW then
    SKIN:Bang('!SetOption', meterName, 'W', f.minW - 2 * PAD_X)
  end
  -- keep the button as the box's frame: lit, with its text hidden and hover turned off while typing
  SKIN:Bang('!SetOption', meterName, 'SolidColor', SKIN:GetVariable('ColorPillHover'))
  SKIN:Bang('!SetOption', meterName, 'FontColor', '0,0,0,0')
  SKIN:Bang('!SetOption', meterName, 'FontEffectColor', '0,0,0,0')
  SKIN:Bang('!SetOption', meterName, 'MouseOverAction', '')
  SKIN:Bang('!SetOption', meterName, 'MouseLeaveAction', '')
  SKIN:Bang('!UpdateMeter', meterName)
  SKIN:Bang('!Redraw')
  -- right edge from the layout: a right-aligned String meter's GetX depends on how it is asked
  local right = (tonumber(SKIN:GetVariable('PanelWidth')) or 460) - (tonumber(SKIN:GetVariable('Pad')) or 22)
  local w, h = m:GetW(), m:GetH()
  SKIN:Bang('!SetVariable', 'TextDefault', f.start())
  SKIN:Bang('!SetOption', 'MeasureTextInput', 'InputNumber', f.number and '1' or '0')
  SKIN:Bang('!SetOption', 'MeasureTextInput', 'X', right - w + PAD_X)
  SKIN:Bang('!SetOption', 'MeasureTextInput', 'Y', m:GetY() + PAD_Y)
  SKIN:Bang('!SetOption', 'MeasureTextInput', 'W', w - 2 * PAD_X)
  SKIN:Bang('!SetOption', 'MeasureTextInput', 'H', h - 2 * PAD_Y)
  SKIN:Bang('!UpdateMeasure', 'MeasureTextInput')
  editing = name
  SKIN:Bang('!CommandMeasure', 'MeasureTextInput', 'ExecuteBatch 1')
end

function EndEdit()
  if not editing then return end
  local meterName = FIELDS[editing].meter
  editing = nil
  SKIN:Bang('!SetOption', meterName, 'W', '')
  SKIN:Bang('!SetOption', meterName, 'SolidColor', SKIN:GetVariable('ColorPill'))
  SKIN:Bang('!SetOption', meterName, 'FontColor', SKIN:GetVariable('ColorMain'))
  SKIN:Bang('!SetOption', meterName, 'FontEffectColor', SKIN:GetVariable('ColorShadow'))
  SKIN:Bang('!SetOption', meterName, 'MouseOverAction', hoverAction(meterName, true))
  SKIN:Bang('!SetOption', meterName, 'MouseLeaveAction', hoverAction(meterName, false))
  SKIN:Bang('!UpdateMeter', meterName)
  SKIN:Bang('!Redraw')
end

function FieldEntered()
  local f = editing and FIELDS[editing]
  if f then f.save(SKIN:GetVariable('TextInput', '')) end
end

-- letters, digits, spaces and , . ' - only (a city ends up on WeatherFetch's command line), trimmed
local function cleanText(text)
  return ((text or ''):gsub("[^%w%s,%.'%-\128-\255]", ''):gsub('%s+', ' '):match('^%s*(.-)%s*$'))
end

local function setCity(text)
  local city = cleanText(text)
  if city ~= SKIN:GetVariable('WeatherCity', '') then
    setValue('WeatherCity', city)
    -- typing a city means using it
    if city ~= '' and SKIN:GetVariable('WeatherLocation', '') ~= 'city' then setValue('WeatherLocation', 'city') end
  end
end

-- Bottom snap spots, typed: 0 to 400 pixels above the bottom of the screen
local function setBottom(text)
  local px = tonumber((text or ''):match('%d+'))
  if not px then return end
  px = math.max(0, math.min(400, math.floor(px)))
  if tostring(px) ~= SKIN:GetVariable('SnapBottomMargin', '') then setValue('SnapBottomMargin', tostring(px)) end
end

-- older Settings panels called these
function EditCity() EditField('city') end
EndEditCity = EndEdit

-- ---------- dropdown (settings with more than two options, and the profile menus) ----------
-- Opens under (or above) value button `meterName`: items are labels, `sel` is the one to light, pick(i) runs on a click
local function openList(key, meterName, items, sel, pick)
  local value = SKIN:GetMeter(meterName)
  local n = math.min(#items, DD_MAX)
  local w = 210
  local itemH = tonumber(SKIN:GetVariable('DDItemH')) or 28
  local h = n * itemH + 8
  local x = (tonumber(SKIN:GetVariable('PanelWidth')) or 460) - (tonumber(SKIN:GetVariable('Pad')) or 22) - w
  local y = value:GetY() + value:GetH() + 4
  -- open upwards when the list would run past the bottom of the panel
  local panelH = tonumber(SKIN:GetVariable('PanelHeight')) or SKIN:GetMeter('MeterPanel'):GetH()
  if y + h > panelH and value:GetY() - h - 4 >= 4 then y = value:GetY() - h - 4 end
  SKIN:Bang('!SetVariable', 'DDX', x); SKIN:Bang('!SetVariable', 'DDY', y)
  SKIN:Bang('!SetVariable', 'DDWidth', w)
  SKIN:Bang('!SetVariable', 'DDBackW', w)
  SKIN:Bang('!SetVariable', 'DDCount', n)
  for i = 1, DD_MAX do
    local m = 'DDItem' .. i
    if i <= n then
      SKIN:Bang('!SetVariable', 'DDBase' .. i, i == sel and SKIN:GetVariable('ColorPillHover') or '0,0,0,1')
      SKIN:Bang('!SetOption', m, 'Text', items[i])
      SKIN:Bang('!SetOption', m, 'X', x + 4)
      SKIN:Bang('!SetOption', m, 'Y', y + 4 + (i - 1) * itemH)
      SKIN:Bang('!ShowMeter', m)
    else
      SKIN:Bang('!HideMeter', m)
    end
  end
  SKIN:Bang('!ShowMeter', 'DDCatcher'); SKIN:Bang('!ShowMeter', 'DDBack')
  ddKey, ddPick = key, pick
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

local function openDropdown(r)
  local cur, items, sel = current(r[1]), {}, 0
  for i, opt in ipairs(r[3]) do
    items[i] = opt[2]
    if opt[1] == cur then sel = i end
  end
  openList(r[1], r[2], items, sel, function(i)
    local v = r[3][i] and r[3][i][1]
    if v == '#custom' then EditField('bottom')
    elseif v then setRow(r, v) end
  end)
end

function PickDropdown(i)
  local pick = ddPick
  CloseDropdown()
  if pick then pick(tonumber(i) or 0) end
end

-- Clicking a value: two options toggle; more options open a dropdown list
function Next(var)
  local r = row(var)
  if not r or isDimmed(r[2]) then return end
  if editing then EndEdit() end
  local opts = r[3]
  if #opts > 2 then
    if ddKey == var then CloseDropdown() else openDropdown(r) end
    return
  end
  local cur = current(var)
  local nextValue = opts[1][1]
  for i, opt in ipairs(opts) do
    if opt[1] == cur then nextValue = opts[(i % #opts) + 1][1] end
  end
  setRow(r, nextValue)
end

-- ---------- profiles ----------
-- A profile is a copy of Settings.inc's values plus each widget's view (View= in its own .ini), kept in
-- @Resources\Profile<N>.ini (sync.ps1 and Update Loopscape leave files they don't ship alone):
--   [Profile]  Name=  Saved=          [Settings]  <every Settings.inc key>=          [Views]  <widget>=<view>
-- Widget positions aren't part of it: they follow the snap spots and the wallpaper.
local PROFILES = 3
local function profilePath(i) return SKIN:GetVariable('@') .. 'Profile' .. i .. '.ini' end
local function widgetIni(name) return SKIN:GetVariable('SKINSPATH', '') .. 'Loopscape\\' .. name .. '\\' .. name .. '.ini' end

-- { section = { key = value } } from a plain ini file, or nil if it can't be read
local function readIni(path)
  local f = io.open(path, 'r')
  if not f then return nil end
  local t, sec = {}, nil
  for line in f:lines() do
    line = line:gsub('^\239\187\191', '')
    local s = line:match('^%s*%[(.-)%]%s*$')
    if s then sec = s; t[sec] = t[sec] or {}
    elseif sec and not line:match('^%s*;') then
      local k, v = line:match('^%s*([^=]-)%s*=(.*)$')
      if k and k ~= '' then t[sec][k] = v end
    end
  end
  f:close()
  return t
end

local function readProfile(i)
  local t = readIni(profilePath(i))
  if not t or not t.Settings then return nil end
  return { name = (t.Profile and t.Profile.Name or ''), saved = (t.Profile and t.Profile.Saved or ''),
           settings = t.Settings, views = t.Views or {} }
end

local function profileName(i, p)
  return (p and p.name ~= '') and p.name or ('Profile ' .. i)
end

showProfiles = function()
  for i = 1, PROFILES do
    local p, n = readProfile(i), 23 + i
    SKIN:Bang('!SetOption', 'MeterLabel' .. n, 'Text', profileName(i, p))
    SKIN:Bang('!SetOption', 'MeterHint' .. n, 'Text', p and ('Saved ' .. p.saved) or 'Empty')
    SKIN:Bang('!SetOption', 'MeterVal' .. n, 'Text', p and 'Load or save' or 'Save here')
  end
end

local function savedStamp()
  local clock = SKIN:GetVariable('ClockFormat', '24') == '12'
  local t = os.date(clock and '%d %b, %I:%M %p' or '%d %b, %H:%M')
  return (t:gsub('^0', ''):gsub(', 0', ', '))
end

local function writeProfile(i, name)
  local inc = readIni(INC) or {}
  local lines = { '; Loopscape widget profile (Settings panel > Profiles). Load it from there.', '[Profile]',
                  'Name=' .. name, 'Saved=' .. savedStamp(), '', '[Settings]' }
  local keys = {}
  for k in pairs(inc.Variables or {}) do keys[#keys + 1] = k end
  table.sort(keys)
  for _, k in ipairs(keys) do lines[#lines + 1] = k .. '=' .. inc.Variables[k] end
  lines[#lines + 1] = ''
  lines[#lines + 1] = '[Views]'
  for _, w in ipairs(WIDGET_TILES) do
    local t = readIni(widgetIni(w[1]))
    local v = t and t.Variables and t.Variables.View
    if v and v ~= '' then lines[#lines + 1] = w[1] .. '=' .. v end
  end
  local f = io.open(profilePath(i), 'w')
  if not f then return end
  f:write(table.concat(lines, '\n') .. '\n')
  f:close()
end

-- Replace the values in Settings.inc in one write (keeping its comments and order); keys it lacks go at the end
local function writeSettings(values)
  local f = io.open(INC, 'rb')
  if not f then return end
  local text = f:read('*a') or ''
  f:close()
  local out, done = {}, {}
  for line in (text .. (text:sub(-1) == '\n' and '' or '\n')):gmatch('(.-)\r?\n') do
    local k = line:match('^([%w_]+)=')
    if k and values[k] ~= nil then line = k .. '=' .. values[k]; done[k] = true end
    out[#out + 1] = line
  end
  while #out > 0 and out[#out] == '' do out[#out] = nil end
  for k, v in pairs(values) do if not done[k] then out[#out + 1] = k .. '=' .. v end end
  f = io.open(INC, 'wb')
  if not f then return end
  f:write(table.concat(out, '\r\n') .. '\r\n')
  f:close()
end

local function loadProfile(i)
  local p = readProfile(i)
  if not p then return end
  local on = running()
  -- which widgets change from shown to hidden (or back), before the panel's own variables are replaced
  local was = {}
  for _, w in ipairs(WIDGET_TILES) do was[w[1]] = SKIN:GetVariable('Show' .. w[1], w[2]) ~= '0' end
  writeSettings(p.settings)
  for name, v in pairs(p.views) do
    SKIN:Bang('!WriteKeyValue', 'Variables', 'View', v, widgetIni(name))
  end
  for _, w in ipairs(WIDGET_TILES) do
    local want = p.settings['Show' .. w[1]]
    local config = 'Loopscape\\' .. w[1]
    if want ~= nil and (want ~= '0') ~= was[w[1]] then
      SKIN:Bang('!SetVariable', 'Show' .. w[1], want)
      apply('Show' .. w[1], want)
    elseif was[w[1]] and on(config) then
      -- a refresh reads Settings.inc and the view again (the weather comes from WeatherFetch's cache)
      SKIN:Bang('!Refresh', config)
    end
  end
  -- Loopscape sees Settings.inc change and picks up the wallpaper settings itself. The panel reloads last.
  SKIN:Bang('!Refresh')
end

-- Profile slot menu: an empty slot saves straight away; a saved one offers Load, Save, Rename and Delete
function ProfileMenu(i)
  i = tonumber(i) or 0
  if i < 1 or i > PROFILES then return end
  if editing then EndEdit() end
  local key = 'profile' .. i
  if ddKey == key then CloseDropdown(); return end
  local p = readProfile(i)
  if not p then writeProfile(i, 'Profile ' .. i); show(); return end
  openList(key, 'MeterVal' .. (23 + i), { 'Load this profile', 'Save current settings here', 'Rename', 'Delete' }, 0,
    function(pick)
      if pick == 1 then loadProfile(i)
      elseif pick == 2 then writeProfile(i, profileName(i, p)); show()
      elseif pick == 3 then EditField(key)
      elseif pick == 4 then os.remove(profilePath(i)); show() end
    end)
end

FIELDS = {
  city = { meter = 'MeterVal22', minW = 180, start = function() return SKIN:GetVariable('WeatherCity', '') end, save = setCity },
  bottom = { meter = 'MeterVal19', minW = 120, number = true,
             start = function() return SKIN:GetVariable('SnapBottomMargin', '76') end, save = setBottom },
}
for i = 1, PROFILES do
  FIELDS['profile' .. i] = { meter = 'MeterVal' .. (23 + i), minW = 180,
    start = function() return profileName(i, readProfile(i)) end,
    save = function(text)
      local name = cleanText(text):sub(1, 32)
      local p = readProfile(i)
      if p and name ~= '' and name ~= p.name then
        -- rename in place: the settings saved in the file stay as they were
        local f = io.open(profilePath(i), 'r'); if not f then return end
        local body = f:read('*a'); f:close()
        body = body:gsub('\nName=[^\n]*', function() return '\nName=' .. name end, 1)
        f = io.open(profilePath(i), 'w'); if not f then return end
        f:write(body); f:close()
        show()
      end
    end }
end
