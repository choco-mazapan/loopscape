-- Loopscape settings panel: three tabs (Wallpaper, Widgets, Look). Shows the current value of each preference
-- and changes it on click. Preferences live in @Resources\Settings.inc (included by both widgets); changing one
-- rewrites that file and applies it to the running widgets.

-- The widgets: config name (= folder = ini name), its Show<name> setting, and whether it's shown when Settings.inc
-- doesn't say (an older Settings.inc; the installer shows the first three then). In MeterWTile1..N order.
local WIDGET_TILES = {
  { 'Clock', '1' }, { 'WiFi', '1' }, { 'NowShowing', '1' }, { 'SunMoon', '0' }, { 'Rain', '0' }, { 'System', '0' },
  { 'Calendar', '0' },
}
local WIDGETS = {}
for i, w in ipairs(WIDGET_TILES) do WIDGETS[i] = 'Loopscape\\' .. w[1] end
local INC

-- Each row: variable, meter that shows the value, list of { value, label }, value when Settings.inc doesn't have it
local ROWS = {
  { 'MatchWidths',   'MeterVal1', { { '1', 'On' }, { '0', 'Off' } } },
  { 'SnapEnabled',   'MeterVal2', { { '1', 'On' }, { '0', 'Off' } } },
  -- read by snap.lua: how far the bottom spots sit from the bottom of the screen
  { 'SnapBottomMargin', 'MeterVal19', { { '76', 'Above taskbar' }, { '28', 'Over taskbar' }, { '8', 'Screen edge' } } },
  { 'WeatherUnit',   'MeterVal3', { { 'fahrenheit', '[\\x00B0]F' }, { 'celsius', '[\\x00B0]C' } } },
  { 'WeatherLocation', 'MeterVal21', { { 'city', 'City' }, { 'windows', 'Windows location' } } },
  { 'PanelAlpha',    'MeterVal4', { { '0', 'Off' }, { '110', 'Light' }, { '150', 'Medium' }, { '200', 'Dark' } } },
  { 'TextEffect',    'MeterVal8', { { 'Shadow', 'Shadow' }, { 'Border', 'Outline' }, { 'None', 'None' } } },
  { 'TextPills',     'MeterVal10', { { '1', 'On' }, { '0', 'Off' } } },
  -- read by snap.lua in each widget (see BOOST there)
  { 'TextBoost',     'MeterVal18', { { '0', 'Off' }, { '1', '1 Soft shadow' }, { '2', '2 Drop shadow' }, { '3', '3 Halo' },
                                     { '4', '4 Outline + halo' }, { '5', '5 Bold + outline' }, { '6', '6 Maximum' } } },
  { 'TextColorMode', 'MeterVal9', { { 'off', 'Off' }, { 'contrast', 'Light / dark' }, { 'smooth', 'Light / dark, fade' },
                                    { 'tinted', 'Wallpaper tint' }, { 'smoothtinted', 'Tint, fade' }, { 'perline', 'Per line' } } },
  { 'ClockFormat',   'MeterVal7', { { '24', '24-hour' }, { '12', '12-hour' } } },
  -- read by snap.lua in each widget (moves them to the wallpaper style's recommended spot)
  { 'AutoPlace',     'MeterVal11', { { '1', 'On' }, { '0', 'Off' } } },
  -- Rotation tab: read by Loopscape straight from Settings.inc
  { 'WallRotateMinutes', 'MeterVal12', { { '15', '15 minutes' }, { '30', '30 minutes' }, { '60', '1 hour' },
                                         { '120', '2 hours' }, { '240', '4 hours' } } },
  { 'WallNight',     'MeterVal13', { { 'fixed', '19:00 [\\x2013] 07:00' }, { 'sun', 'Sunset to sunrise' } } },
  { 'WallWeather',   'MeterVal14', { { '1', 'On' }, { '0', 'Off' } } },
  { 'WallOnBattery', 'MeterVal15', { { 'pause', 'Pause' }, { 'play', 'Keep playing' }, { 'static', 'Still image' } } },
  { 'WallPauseCovered', 'MeterVal16', { { '1', 'On' }, { '0', 'Off' } } },
}

-- Wallpaper tiles, in MeterTile1..N order (Settings.ini lays them out: rotate, then day grid, then night grid)
local TILES = { 'rotate',
  'spectrum', 'halftone', 'bluesky', 'pixelclouds', 'sunset', 'riso', 'dunes', 'lake', 'pastel', 'crt', 'shore', 'gradient',
  'nightclouds', 'aurora', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce' }
local NAMES = { rotate = 'Rotate day/night',
  spectrum = 'Spectrum', halftone = 'Halftone', bluesky = 'Blue sky', pixelclouds = 'Pixel clouds', sunset = 'Sunset strata',
  riso = 'Riso waves', dunes = 'Dunes', lake = 'Lake', pastel = 'Pastel aurora', crt = 'CRT test card', shore = 'Shore', gradient = 'Gradient',
  nightclouds = 'Night clouds', aurora = 'Aurora', nebula = 'Nebula', moon = 'Halftone moon', embers = 'Embers',
  deepsea = 'Deep sea', rain = 'Rainy window', bounce = 'Bouncing logo', loop = 'Gradient' }

local TABS = { 'Wallpaper', 'Rotation', 'Widgets', 'Look' }
local STYLES_DIR = (os.getenv('USERPROFILE') or '') .. '\\Loopscape\\styles\\'

local function excluded()
  local set = {}
  for s in (SKIN:GetVariable('WallExclude', '')):gmatch('[^,%s]+') do set[s:lower()] = true end
  return set
end

local function row(var) for _, r in ipairs(ROWS) do if r[1] == var then return r end end end

local function label(r, value)
  for _, opt in ipairs(r[3]) do if opt[1] == value then return opt[2] end end
  return value
end

local function show()
  for _, r in ipairs(ROWS) do
    SKIN:Bang('!SetOption', r[2], 'Text', label(r, SKIN:GetVariable(r[1], r[4] or '')))
  end
  local city = SKIN:GetVariable('WeatherCity', '')
  SKIN:Bang('!SetOption', 'MeterVal22', 'Text', city ~= '' and city or 'Type a city')
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

-- ---------- "Playing now" (Loopscape writes @Resources\wallstatus.txt) and tile previews ----------
local hovered = 0
local lastStatus = nil

local function readStatus()
  local f = io.open(SKIN:GetVariable('@') .. 'wallstatus.txt', 'r')
  if not f then return {} end
  local t = {}
  for line in f:lines() do local k, v = line:match('^(%w+)=(.*)$'); if k then t[k] = v end end
  f:close()
  return t
end

local function setText(m, text) SKIN:Bang('!SetOption', m, 'Text', text) end
local function preview(id)
  local png = id and id ~= 'gradient' and id ~= 'loop' and id ~= '' and (STYLES_DIR .. id .. '.png') or ''
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
  setText('MeterNowReason', s.reason or '')
  setText('MeterNowNext', (s.next and s.next ~= '') and ('Next change ' .. s.next) or '')
  local state = s.state or ''
  setText('MeterNowState', state ~= '' and (state:sub(1, 1):upper() .. state:sub(2)) or '')
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
local ddVar = nil

-- Note: the value buttons have no tooltips on purpose. Rainmeter shows a meter's tooltip even under the dropdown,
-- and changing ToolTipText at runtime leaves orphaned tooltip windows on screen.
function CloseDropdown()
  ddVar = nil
  SKIN:Bang('!HideMeterGroup', 'Dropdown')
  SKIN:Bang('!Redraw')
end

function ShowTab(name)
  if ddVar then CloseDropdown() end
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

function Initialize()
  INC = SKIN:GetVariable('@') .. 'Settings.inc'
  local tab = SKIN:GetVariable('Tab', 'Wallpaper')
  local known = false
  for _, t in ipairs(TABS) do if t == tab then known = true end end
  show()
  ShowTab(known and tab or 'Wallpaper')
  showNow(true)
end

function Update()
  if SKIN:GetVariable('Tab', '') == 'Wallpaper' then showNow(false) end
  return 0
end

-- Apply a changed setting to the running widgets without refreshing them (a refresh reloads everything:
-- the weather re-fetches, the Wi-Fi details reset and the widgets flicker). Settings.inc is still updated,
-- so the value also holds after the next refresh/restart.
local CLOCK = 'Loopscape\\Clock'
local function apply(var, value)
  local shown = var:match('^Show(%w+)$')
  if shown then
    for _, w in ipairs(WIDGET_TILES) do
      if w[1] == shown then
        if value == '1' then SKIN:Bang('!ActivateConfig', 'Loopscape\\' .. shown, shown .. '.ini')
        else SKIN:Bang('!DeactivateConfig', 'Loopscape\\' .. shown) end
      end
    end
    return
  elseif var == 'TextColorMode' or var:match('^Wall') then
    -- read from Settings.inc by Loopscape, which watches the file and crossfades to a new style by itself
    -- (downloading it first if it isn't on this PC yet)
    return
  end
  -- Everything else is a variable the widgets use with DynamicVariables: set it and redraw. The widgets' meters
  -- only update when asked, so update them twice: their panel and pills come before the text they wrap.
  for _, w in ipairs(WIDGETS) do
    SKIN:Bang('!SetVariable', var, value, w)
    SKIN:Bang('!UpdateMeasure', 'MeasureNaturalWidth', w)   -- not '*': that would fire the weather/Wi-Fi timers
    SKIN:Bang('!UpdateMeter', '*', w)
    SKIN:Bang('!UpdateMeter', '*', w)
    SKIN:Bang('!Redraw', w)
  end
  if var == 'SnapBottomMargin' then
    -- move widgets parked on a bottom spot (and anything stacked on them) to the new height
    for _, w in ipairs(WIDGETS) do SKIN:Bang('!CommandMeasure', 'MeasureSnap', 'Resnap()', w) end
  end
  if var == 'WeatherUnit' or var == 'WeatherLocation' or var == 'WeatherCity' then
    -- show the weather in the new unit / for the new place (only the clock shows weather). A unit change reuses
    -- WeatherFetch's cached result; a new location fetches. The Parameter picks the variables up (set above)
    -- through DynamicVariables
    SKIN:Bang('!UpdateMeasure', 'MeasureWeatherRun', CLOCK)
    SKIN:Bang('!CommandMeasure', 'MeasureWeatherRun', 'Run', CLOCK)
    -- the other widgets that read WeatherFetch: Sun & Moon (sun times), Rain
    SKIN:Bang('!UpdateMeasure', 'MeasureSunRun', 'Loopscape\\SunMoon')
    SKIN:Bang('!CommandMeasure', 'MeasureSunRun', 'Run', 'Loopscape\\SunMoon')
    SKIN:Bang('!UpdateMeasure', 'MeasureRainRun', 'Loopscape\\Rain')
    SKIN:Bang('!CommandMeasure', 'MeasureRainRun', 'Run', 'Loopscape\\Rain')
  end
  if var == 'ClockFormat' then
    -- times shown by the scripts (not by a Time measure)
    SKIN:Bang('!UpdateMeasure', 'MeasureSunMoon', 'Loopscape\\SunMoon')
    SKIN:Bang('!UpdateMeasure', 'MeasureRain', 'Loopscape\\Rain')
  end
end
local function setValue(var, value)
  SKIN:Bang('!SetVariable', var, value)
  SKIN:Bang('!WriteKeyValue', 'Variables', var, value, INC)
  show()
  apply(var, value)
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

-- Weather city: a text box over the City row (MeasureCityInput); Enter calls SetCity with the text in CityInput
function EditCity()
  CloseDropdown()
  SKIN:Bang('!CommandMeasure', 'MeasureCityInput', 'ExecuteBatch 1')
end
function SetCity()
  -- letters, digits, spaces and , . ' - only: the value ends up on WeatherFetch's command line
  local city = (SKIN:GetVariable('CityInput', '')):gsub("[^%w%s,%.'%-\128-\255]", ''):gsub('%s+', ' ')
  city = city:match('^%s*(.-)%s*$')
  if city ~= SKIN:GetVariable('WeatherCity', '') then
    setValue('WeatherCity', city)
    -- typing a city means using it
    if city ~= '' and SKIN:GetVariable('WeatherLocation', '') ~= 'city' then setValue('WeatherLocation', 'city') end
  end
end

-- Skip to the next wallpaper: Loopscape watches @Resources\wallnext.txt
function NextWallpaper()
  local f = io.open(SKIN:GetVariable('@') .. 'wallnext.txt', 'w')
  if f then f:write(os.time(), ' ', os.clock(), '\n'); f:close() end
end

local function openDropdown(r)
  local var, opts = r[1], r[3]
  local value = SKIN:GetMeter(r[2])
  local n = math.min(#opts, DD_MAX)
  local w = 210
  local itemH = tonumber(SKIN:GetVariable('DDItemH')) or 28
  local h = n * itemH + 8
  local x = (tonumber(SKIN:GetVariable('PanelWidth')) or 460) - (tonumber(SKIN:GetVariable('Pad')) or 22) - w
  local y = value:GetY() + value:GetH() + 4
  -- open upwards when the list would run past the bottom of the panel
  local panelH = tonumber(SKIN:GetVariable('PanelHeight')) or SKIN:GetMeter('MeterPanel'):GetH()
  if y + h > panelH and value:GetY() - h - 4 >= 4 then y = value:GetY() - h - 4 end
  local cur = SKIN:GetVariable(var, '')
  SKIN:Bang('!SetVariable', 'DDX', x); SKIN:Bang('!SetVariable', 'DDY', y)
  SKIN:Bang('!SetVariable', 'DDWidth', w)
  SKIN:Bang('!SetVariable', 'DDBackW', w)
  SKIN:Bang('!SetVariable', 'DDCount', n)
  for i = 1, DD_MAX do
    local m = 'DDItem' .. i
    if i <= n then
      local base = (opts[i][1] == cur) and SKIN:GetVariable('ColorPillHover') or '0,0,0,1'
      SKIN:Bang('!SetVariable', 'DDBase' .. i, base)
      SKIN:Bang('!SetOption', m, 'Text', opts[i][2])
      SKIN:Bang('!SetOption', m, 'X', x + 4)
      SKIN:Bang('!SetOption', m, 'Y', y + 4 + (i - 1) * itemH)
      SKIN:Bang('!ShowMeter', m)
    else
      SKIN:Bang('!HideMeter', m)
    end
  end
  SKIN:Bang('!ShowMeter', 'DDCatcher'); SKIN:Bang('!ShowMeter', 'DDBack')
  ddVar = var
  SKIN:Bang('!UpdateMeter', '*')
  SKIN:Bang('!Redraw')
end

function PickDropdown(i)
  local r = ddVar and row(ddVar)
  CloseDropdown()
  if r and r[3][i] then setValue(r[1], r[3][i][1]) end
end

-- Clicking a value: two options toggle; more options open a dropdown list
function Next(var)
  local r = row(var)
  if not r then return end
  local opts = r[3]
  if #opts > 2 then
    if ddVar == var then CloseDropdown() else openDropdown(r) end
    return
  end
  local cur = SKIN:GetVariable(var, r[4] or '')
  local nextValue = opts[1][1]
  for i, opt in ipairs(opts) do
    if opt[1] == cur then nextValue = opts[(i % #opts) + 1][1] end
  end
  setValue(var, nextValue)
end
